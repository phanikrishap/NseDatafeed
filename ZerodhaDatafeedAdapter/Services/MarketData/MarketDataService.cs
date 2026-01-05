using System;
using System.Buffers;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Net.WebSockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using NinjaTrader.Cbi;
using NinjaTrader.Data;
using ZerodhaAPI.Common.Enums;
using ZerodhaAPI.Zerodha.Websockets;
using ZerodhaAPI.Zerodha.Utility;
using ZerodhaDatafeedAdapter.Models;
using ZerodhaDatafeedAdapter.Models.MarketData;
using ZerodhaDatafeedAdapter.Services.Configuration;
using ZerodhaDatafeedAdapter.Services.Instruments;
using ZerodhaDatafeedAdapter.Services.WebSocket;
using ZerodhaDatafeedAdapter;
using ZerodhaDatafeedAdapter.Classes;
using ZerodhaDatafeedAdapter.Logging;

namespace ZerodhaDatafeedAdapter.Services.MarketData
{
    /// <summary>
    /// Service for handling real-time market data subscriptions.
    ///
    /// OPTIMIZED: Now uses centralized OptimizedTickProcessor for high-performance tick processing:
    /// - Async queue-based processing to decouple WebSocket receive from NinjaTrader callbacks
    /// - Object pooling to reduce GC pressure
    /// - Intelligent backpressure management
    /// - O(1) callback caching
    /// - 30-second health monitoring
    /// </summary>
    public class MarketDataService
    {
        private static MarketDataService _instance;
        private readonly InstrumentManager _instrumentManager;
        private readonly WebSocketManager _webSocketManager;
        private readonly ConfigurationManager _configManager;
        private readonly ConcurrentDictionary<string, int> _lastVolumeMap = new ConcurrentDictionary<string, int>(); // Added for volumeDelta

        // OPTIMIZATION: Centralized tick processor for high-performance processing
        private OptimizedTickProcessor _tickProcessor;
        private readonly object _tickProcessorLock = new object();

        /// <summary>
        /// Gets the singleton instance of the MarketDataService
        /// </summary>
        public static MarketDataService Instance
        {
            get
            {
                if (_instance == null)
                    _instance = new MarketDataService();
                return _instance;
            }
        }

        /// <summary>
        /// Gets the OptimizedTickProcessor instance, creating it if necessary
        /// </summary>
        public OptimizedTickProcessor TickProcessor
        {
            get
            {
                if (_tickProcessor == null)
                {
                    lock (_tickProcessorLock)
                    {
                        if (_tickProcessor == null)
                        {
                            _tickProcessor = new OptimizedTickProcessor(20000); // 20K queue capacity
                            NinjaTrader.NinjaScript.NinjaScript.Log(
                                "[MDS] OptimizedTickProcessor initialized with 20K queue capacity",
                                NinjaTrader.Cbi.LogLevel.Information);
                        }
                    }
                }
                return _tickProcessor;
            }
        }

        /// <summary>
        /// Private constructor to enforce singleton pattern
        /// </summary>
        private MarketDataService()
        {
            _instrumentManager = InstrumentManager.Instance;
            _webSocketManager = WebSocketManager.Instance;
            _configManager = ConfigurationManager.Instance;
        }

        /// <summary>
        /// Updates the subscription cache in the OptimizedTickProcessor.
        /// Should be called whenever subscriptions change.
        /// </summary>
        public void UpdateProcessorSubscriptionCache(ConcurrentDictionary<string, L1Subscription> subscriptions)
        {
            // Use TickProcessor property to ensure it's initialized before updating cache
            TickProcessor.UpdateSubscriptionCache(subscriptions);
        }

        /// <summary>
        /// Gets diagnostic information from the tick processor
        /// </summary>
        public string GetProcessorDiagnostics()
        {
            if (_tickProcessor != null)
            {
                return _tickProcessor.GetDiagnosticInfo();
            }
            return "OptimizedTickProcessor not initialized";
        }

        /// <summary>
        /// Gets current tick processor metrics
        /// </summary>
        public TickProcessorMetrics GetProcessorMetrics()
        {
            if (_tickProcessor != null)
            {
                return _tickProcessor.GetMetrics();
            }
            return null;
        }

        // NOTE: Legacy per-symbol SubscribeToTicks method removed.
        // All tick subscriptions now use the shared WebSocket via SubscribeToTicksShared.
        // This avoids hitting Zerodha's connection limits and reduces thread overhead.

        /// <summary>
        /// Subscribes to market depth for a symbol
        /// </summary>
        /// <param name="nativeSymbolName">The native symbol name</param>
        /// <param name="marketType">The market type</param>
        /// <param name="symbol">The symbol</param>
        /// <param name="l2Subscriptions">The L2 subscriptions dictionary</param>
        /// <param name="webSocketConnectionFunc">The WebSocket connection function</param>
        /// <returns>A task representing the asynchronous operation</returns>
        public async Task SubscribeToDepth(
            string nativeSymbolName,
            MarketType marketType,
            string symbol,
            ConcurrentDictionary<string, L2Subscription> l2Subscriptions,
            WebSocketConnectionFunc webSocketConnectionFunc)
        {
            if (string.IsNullOrEmpty(symbol) || webSocketConnectionFunc == null) return;

            int retryCount = 0;
            const int maxRetries = 10;
            var cts = new CancellationTokenSource();
            StartExitMonitoringTask(webSocketConnectionFunc, cts);

            while (!cts.Token.IsCancellationRequested && retryCount < maxRetries)
            {
                ClientWebSocket ws = null;
                byte[] buffer = null;

                try
                {
                    if (retryCount > 0)
                    {
                        int delay = Math.Min(10000, (int)Math.Pow(2, retryCount) * 500);
                        await Task.Delay(delay, cts.Token);
                    }

                    ws = _webSocketManager.CreateWebSocketClient();
                    await _webSocketManager.ConnectAsync(ws);

                    long token = _instrumentManager.GetInstrumentToken(symbol);
                    int tokenInt = (int)token;
                    await _webSocketManager.SubscribeAsync(ws, tokenInt, "full");

                    buffer = ArrayPool<byte>.Shared.Rent(16384);

                    while (ws.State == System.Net.WebSockets.WebSocketState.Open && !cts.Token.IsCancellationRequested)
                    {
                        WebSocketReceiveResult result = await _webSocketManager.ReceiveMessageAsync(ws, buffer, cts.Token);

                        if (result.MessageType == WebSocketMessageType.Close) break;
                        if (result.MessageType == WebSocketMessageType.Text || result.Count < 2) continue;

                        // Parse and queue depth data
                        var tickData = _webSocketManager.ParseBinaryMessage(buffer, tokenInt, nativeSymbolName, false, false);
                        if (tickData != null)
                        {
                            if (tickData.HasMarketDepth)
                            {
                                TickProcessor.UpdateL2SubscriptionCache(l2Subscriptions);
                                bool queued = TickProcessor.QueueTick(nativeSymbolName, tickData);
                                if (!queued)
                                {
                                    ZerodhaTickDataPool.Return(tickData);
                                }
                            }
                            else
                            {
                                // Not a depth packet or no depth data - return to pool
                                ZerodhaTickDataPool.Return(tickData);
                            }
                        }
                    }
                    retryCount = 0;
                }
                catch (OperationCanceledException) { break; }
                catch (Exception ex)
                {
                    retryCount++;
                    NinjaTrader.NinjaScript.NinjaScript.Log($"[DEPTH-SUBSCRIBE] Error for {nativeSymbolName}: {ex.Message}. Retry {retryCount}/{maxRetries}", NinjaTrader.Cbi.LogLevel.Warning);
                }
                finally
                {
                    if (buffer != null) ArrayPool<byte>.Shared.Return(buffer);
                    if (ws != null)
                    {
                        try { await _webSocketManager.CloseAsync(ws); } catch { }
                        ws.Dispose();
                    }
                }
            }
            cts.Cancel();
            cts.Dispose();
        }

        /// <summary>
        /// Processes market depth data
        /// </summary>
        /// <param name="data">The binary data</param>
        /// <param name="tokenInt">The instrument token</param>
        /// <param name="nativeSymbolName">The native symbol name</param>
        /// <param name="l2Subscriptions">The L2 subscriptions dictionary</param>
        private void ProcessDepthData(byte[] data, int tokenInt, string nativeSymbolName,
            ConcurrentDictionary<string, L2Subscription> l2Subscriptions)
        {
            if (data.Length < 2)
            {
                NinjaTrader.NinjaScript.NinjaScript.Log("[DEPTH PARSER] Packet too small", NinjaTrader.Cbi.LogLevel.Warning);
                return;
            }

            int offset = 0;
            int packetCount = ZerodhaAPI.Zerodha.Utility.ZerodhaBinaryReader.ReadInt16BE(data, offset);
            offset += 2;

            for (int i = 0; i < packetCount; i++)
            {
                // Check if we have enough data for packet length
                if (offset + 2 > data.Length)
                    break;

                int packetLength = ZerodhaAPI.Zerodha.Utility.ZerodhaBinaryReader.ReadInt16BE(data, offset);
                offset += 2;

                // Check if we have enough data for the packet content
                if (offset + packetLength > data.Length)
                    break;

                // Only process packets with valid length (we need 184 bytes for market depth)
                if (packetLength != 184)
                {
                    offset += packetLength; // Skip this packet
                    continue;
                }

                // Check if this is our subscribed token
                int iToken = ZerodhaAPI.Zerodha.Utility.ZerodhaBinaryReader.ReadInt32BE(data, offset);
                if (iToken != tokenInt)
                {
                    offset += packetLength; // Skip this packet
                    continue;
                }

                // Process market depth packet
                ProcessDepthPacket(data, offset, packetLength, nativeSymbolName, l2Subscriptions);

                // Move to next packet
                offset += packetLength;
            }
        }

        /// <summary>
        /// Processes a market depth packet
        /// </summary>
        /// <param name="data">The binary data</param>
        /// <param name="offset">The offset in the data</param>
        /// <param name="packetLength">The packet length</param>
        /// <param name="nativeSymbolName">The native symbol name</param>
        /// <param name="l2Subscriptions">The L2 subscriptions dictionary</param>
        private void ProcessDepthPacket(byte[] data, int offset, int packetLength, string nativeSymbolName,
            ConcurrentDictionary<string, L2Subscription> l2Subscriptions)
        {
            try
            {
                // Get the instrument token
                int iToken = ZerodhaAPI.Zerodha.Utility.ZerodhaBinaryReader.ReadInt32BE(data, offset);

                // Get segment information for MCX check
                string segment = _instrumentManager.GetSegmentForToken(iToken);
                bool isMcxSegment = !string.IsNullOrEmpty(segment) && segment.Equals("MCX", StringComparison.OrdinalIgnoreCase);

                // Parse the binary message into a rich data structure
                // Note: Market depth packets are only for tradeable instruments (not indices), so isIndex is always false here
                var tickData = _webSocketManager.ParseBinaryMessage(data, iToken, nativeSymbolName, isMcxSegment, isIndex: false);

                if (tickData == null || !tickData.HasMarketDepth)
                {
                    // POOL FIX: Return tickData to pool if we're not using it
                    if (tickData != null)
                        ZerodhaTickDataPool.Return(tickData);
                    return;
                }

                try
                {
                    // Get the current time in Indian Standard Time
                    // OPTIMIZATION: Use cached TimeZone to avoid FindSystemTimeZoneById in hot path
                    DateTime now = TimeZoneInfo.ConvertTime(DateTime.Now, Constants.IndianTimeZone);

                    // Update market depth in NinjaTrader
                    if (l2Subscriptions.TryGetValue(nativeSymbolName, out var l2Subscription))
                    {
                        for (int index = 0; index < l2Subscription.L2Callbacks.Count; ++index)
                        {
                            // Process asks (offers)
                            foreach (var ask in tickData.AskDepth)
                            {
                                if (ask != null && ask.Quantity > 0)
                                {
                                    l2Subscription.L2Callbacks.Keys[index].UpdateMarketDepth(
                                        MarketDataType.Ask, ask.Price, ask.Quantity, Operation.Update, now, l2Subscription.L2Callbacks.Values[index]);
                                }
                            }

                            // Process bids
                            foreach (var bid in tickData.BidDepth)
                            {
                                if (bid != null && bid.Quantity > 0)
                                {
                                    l2Subscription.L2Callbacks.Keys[index].UpdateMarketDepth(
                                        MarketDataType.Bid, bid.Price, bid.Quantity, Operation.Update, now, l2Subscription.L2Callbacks.Values[index]);
                                }
                            }
                        }
                    }
                }
                finally
                {
                    // POOL FIX: Always return tickData to pool after processing depth data
                    ZerodhaTickDataPool.Return(tickData);
                }
            }
            catch (Exception ex)
            {
                NinjaTrader.NinjaScript.NinjaScript.Log($"[DEPTH PACKET] Exception: {ex.Message}",
                    NinjaTrader.Cbi.LogLevel.Error);
            }
        }

        /// <summary>
        /// Starts a task to monitor the exit condition
        /// </summary>
        /// <param name="webSocketConnectionFunc">The WebSocket connection function</param>
        /// <param name="cts">The cancellation token source</param>
        private void StartExitMonitoringTask(WebSocketConnectionFunc webSocketConnectionFunc, CancellationTokenSource cts)
        {
            // Fire-and-forget task to detect chart-close
            Task.Run(async () =>
            {
                while (!cts.IsCancellationRequested)
                {
                    if (webSocketConnectionFunc.ExitFunction())
                    {
                        cts.Cancel();
                        break;
                    }
                    await Task.Delay(500, cts.Token);
                }
            });
        }

        /// <summary>
        /// Logs tick information
        /// </summary>
        /// <param name="nativeSymbolName">The native symbol name</param>
        /// <param name="lastPrice">The last traded price</param>
        /// <param name="lastQuantity">The last traded quantity</param>
        /// <param name="volume">The volume</param>
        /// <param name="timestamp">The timestamp</param>
        /// <param name="receivedTime">The time the message was received</param>
        private async Task LogTickInformationAsync(string nativeSymbolName, double lastPrice, int lastQuantity, int volume, DateTime timestamp, DateTime receivedTime)
        {
            await Task.Run(() =>
            {
                try
                {
                    // Format the log message
                    string logMessage = string.Format(
                        "{0:HH:mm:ss.fff},{1},{2:HH:mm:ss.fff},{3:HH:mm:ss.fff},{4:HH:mm:ss.fff},{5},{6},{7}",
                        receivedTime, // System Time (when received by adapter)
                        nativeSymbolName,
                        receivedTime, // Placeholder for original received time before parsing, if available
                        timestamp,    // ExchangeTime (from tick data)
                        DateTime.Now, // ParsedTime (current time, assuming parsing is quick)
                        lastPrice,
                        lastQuantity,
                        volume);

                    // Log to NinjaTrader's log (consider if this is needed or too verbose)
                    // NinjaTrader.NinjaScript.NinjaScript.Log($"[TICK-LOG] {logMessage}", NinjaTrader.Cbi.LogLevel.Information);

                    // Append to CSV - Ensure this path is configurable and accessible
                    // string logFilePath = System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "NinjaTrader 8", "log", "TickDataLog.csv");
                    // System.IO.File.AppendAllText(logFilePath, logMessage + Environment.NewLine);
                }
                catch (Exception ex)
                {
                    NinjaTrader.NinjaScript.NinjaScript.Log($"[LOG-TICK-ERROR] Failed to log tick: {ex.Message}", NinjaTrader.Cbi.LogLevel.Error);
                }
            });
        }

        /// <summary>
        /// Gets the current time in Indian Standard Time
        /// </summary>
        /// <param name="dateTime">The date time</param>
        /// <returns>The date time in Indian Standard Time</returns>
        private DateTime GetIndianTime(DateTime dateTime)
        {
            // OPTIMIZATION: Use cached TimeZone to avoid FindSystemTimeZoneById
            return TimeZoneInfo.ConvertTime(dateTime, Constants.IndianTimeZone);
        }

        /// <summary>
        /// Converts a Unix timestamp to local time
        /// </summary>
        /// <param name="unixTimestamp">The Unix timestamp</param>
        /// <returns>The local DateTime</returns>
        private DateTime UnixSecondsToLocalTime(int unixTimestamp)
        {
            // OPTIMIZATION: Use cached epoch to avoid repeated allocations
            return Constants.UnixEpoch.AddSeconds(unixTimestamp).ToLocalTime();
        }

        #region Shared WebSocket Subscription

        // Shared WebSocket service instance
        private SharedWebSocketService _sharedWebSocketService;
        private bool _sharedWebSocketInitialized = false;
        private readonly object _sharedWsLock = new object();

        /// <summary>
        /// Subscribes to ticks using the shared WebSocket connection.
        /// This is more efficient than creating one WebSocket per symbol.
        /// Uses SubscriptionTrackingService to manage reference counting.
        /// </summary>
        public async Task SubscribeToTicksShared(
            string nativeSymbolName,
            MarketType marketType,
            string symbol,
            ConcurrentDictionary<string, L1Subscription> l1Subscriptions,
            WebSocketConnectionFunc webSocketConnectionFunc)
        {
            Logger.Debug($"[TICK-SHARED] SubscribeToTicksShared: symbol={symbol}, nativeSymbol={nativeSymbolName}");

            try
            {
                // Generate a unique consumer ID for this subscription request
                string consumerId = $"{nativeSymbolName}_{Guid.NewGuid():N}";

                // Initialize shared WebSocket service if needed
                await EnsureSharedWebSocketInitializedAsync(l1Subscriptions);

                // Get instrument token
                int tokenInt = (int)_instrumentManager.GetInstrumentToken(symbol);
                Logger.Debug($"[TICK-SHARED] Got token {tokenInt} for symbol='{symbol}'");

                // Determine if this is an index
                bool isIndex = false;
                if (l1Subscriptions.TryGetValue(nativeSymbolName, out var sub))
                    isIndex = sub.IsIndex;

                // Add reference using SubscriptionTrackingService
                // STICKY=true: All subscriptions are sticky - once subscribed, stay subscribed for the session
                // This prevents NinjaTrader's aggressive UnsubscribeMarketData calls from killing WebSocket connections
                bool isNewSubscription = SubscriptionTrackingService.Instance.AddReference(symbol, consumerId, tokenInt, isIndex, isSticky: true);

                if (isNewSubscription)
                {
                    // This is the first reference - actually subscribe via WebSocket
                    bool subscribed = await _sharedWebSocketService.SubscribeAsync(symbol, tokenInt, isIndex);

                    if (subscribed)
                    {
                        Logger.Debug($"[TICK-SHARED] Successfully subscribed to {symbol} (token={tokenInt}, isIndex={isIndex})");
                    }
                    else
                    {
                        Logger.Error($"[TICK-SHARED] Failed to subscribe to {symbol}");
                        // Remove the reference since subscribe failed
                        SubscriptionTrackingService.Instance.RemoveReference(symbol, consumerId);
                    }
                }
                else
                {
                    // Already subscribed by another consumer - just log
                    int refCount = SubscriptionTrackingService.Instance.GetReferenceCount(symbol);
                    Logger.Debug($"[TICK-SHARED] Already subscribed to {symbol}, refCount={refCount}");
                }

                // Start exit monitoring (to cleanup when chart closes)
                if (webSocketConnectionFunc != null)
                {
                    StartExitMonitoringForSharedSubscription(symbol, consumerId, webSocketConnectionFunc);
                }
            }
            catch (Exception ex)
            {
                Logger.Error($"[TICK-SHARED] Exception subscribing to {symbol}: {ex.Message}", ex);
            }
        }

        /// <summary>
        /// Ensures the shared WebSocket service is initialized and connected.
        /// Uses WaitForConnectionAsync for more reliable connection establishment.
        /// </summary>
        private bool _connectionMilestoneLogged = false; // Only log milestone once per session
        private async Task EnsureSharedWebSocketInitializedAsync(ConcurrentDictionary<string, L1Subscription> l1Subscriptions)
        {
            if (_sharedWebSocketInitialized && _sharedWebSocketService?.IsConnected == true)
                return;

            lock (_sharedWsLock)
            {
                if (_sharedWebSocketService == null)
                {
                    _sharedWebSocketService = SharedWebSocketService.Instance;

                    // Wire up tick handler
                    _sharedWebSocketService.TickReceived += OnSharedWebSocketTickReceived;

                    // Subscribe to connection events for better reliability
                    _sharedWebSocketService.ConnectionReady += OnSharedWebSocketConnectionReady;
                    _sharedWebSocketService.ConnectionLost += OnSharedWebSocketConnectionLost;

                    Logger.Debug("[TICK-SHARED] SharedWebSocketService initialized and handlers connected");
                    StartupLogger.LogMarketDataServiceInit(true, "SharedWebSocketService handlers connected");
                }
            }

            // Use WaitForConnectionAsync for more reliable connection with proper timeout
            // This will initiate connection if needed and wait for it to complete
            bool connected = await _sharedWebSocketService.WaitForConnectionAsync(timeoutMs: 15000);
            _sharedWebSocketInitialized = connected;

            if (connected)
            {
                Logger.Info("[TICK-SHARED] SharedWebSocketService connected successfully");
                // Only log milestone once to avoid spam from concurrent subscription threads
                if (!_connectionMilestoneLogged)
                {
                    _connectionMilestoneLogged = true;
                    StartupLogger.LogMilestone("MarketDataService WebSocket connected - ready for subscriptions");
                }
            }
            else
            {
                Logger.Error("[TICK-SHARED] SharedWebSocketService failed to connect within timeout - subscriptions may be queued");
                StartupLogger.Warn("MarketDataService WebSocket connection timeout - subscriptions queued for retry");
                // Even if initial connection fails, subscriptions will be queued and processed when connection succeeds
            }
        }

        /// <summary>
        /// Called when WebSocket connection becomes ready - can trigger resubscriptions if needed
        /// </summary>
        private void OnSharedWebSocketConnectionReady()
        {
            Logger.Info("[TICK-SHARED] ConnectionReady event received - WebSocket is now ready for subscriptions");
            _sharedWebSocketInitialized = true;
        }

        /// <summary>
        /// Called when WebSocket connection is lost
        /// </summary>
        private void OnSharedWebSocketConnectionLost()
        {
            Logger.Warn("[TICK-SHARED] ConnectionLost event received - WebSocket disconnected, will auto-reconnect");
            _sharedWebSocketInitialized = false;
        }

        /// <summary>
        /// Handles ticks received from the shared WebSocket service
        /// </summary>
        private long _optionTickReceiveCounter = 0; // Diagnostic counter
        private bool _firstTickLogged = false; // Track if first tick has been logged for startup diagnostics

        private void OnSharedWebSocketTickReceived(string symbol, ZerodhaTickData tickData)
        {
            try
            {
                // STARTUP DIAGNOSTIC: Log first tick received as proof of data flow
                if (!_firstTickLogged && tickData?.LastTradePrice > 0)
                {
                    _firstTickLogged = true;
                    StartupLogger.LogFirstTickReceived(symbol, tickData.LastTradePrice);
                }

                // DIAGNOSTIC: Log option ticks at entry point
                if (symbol != null && (symbol.Contains("CE") || symbol.Contains("PE")) && !symbol.Contains("SENSEX") && !symbol.Contains("BANKNIFTY"))
                {
                    if (Interlocked.Increment(ref _optionTickReceiveCounter) % 100 == 1)
                    {
                        Logger.Debug($"[MDS-DIAG] Option tick RECEIVED: symbol={symbol}, ltp={tickData?.LastTradePrice}, vol={tickData?.TotalQtyTraded}");
                    }
                }

                if (string.IsNullOrEmpty(symbol) || tickData == null || tickData.LastTradePrice <= 0)
                {
                    // POOL FIX: Return tickData to pool if we're discarding it early
                    if (tickData != null)
                        ZerodhaTickDataPool.Return(tickData);
                    return;
                }

                // Process direct tick callbacks (for symbols without NT Instrument, e.g., NIFTY_I)
                ProcessDirectTickCallback(symbol, tickData);

                // Route to OptimizedTickProcessor (single processing path - no legacy fallback)
                // CRITICAL DEBUG: Log to diagnose tick flow to QueueTick
                var processor = TickProcessor;
                if (processor == null)
                {
                    Logger.Warn($"[MDS-TICK] TickProcessor is NULL for symbol {symbol}!");
                }

                bool queued = processor?.QueueTick(symbol, tickData) ?? false;
                if (!queued)
                {
                    // CRITICAL DEBUG: Log when tick is not queued
                    if ((symbol?.Contains("CE") == true || symbol?.Contains("PE") == true) && !symbol.Contains("SENSEX"))
                    {
                        Logger.Warn($"[MDS-TICK] Tick NOT QUEUED for {symbol}: processor={(processor != null ? "OK" : "NULL")}");
                    }
                    // Backpressure - return to pool and skip synthetic straddle processing
                    ZerodhaTickDataPool.Return(tickData);
                    return;
                }

                // Route to synthetic straddle service (only for options, not indices)
                // Skip NIFTY, SENSEX, BANKNIFTY spot symbols as they are not option legs
                if (!IsIndexSymbol(symbol))
                {
                    var connector = Connector.Instance;
                    if (connector != null)
                    {
                        ZerodhaAdapter ZerodhaAdapter = connector.GetAdapter() as ZerodhaAdapter;
                        ZerodhaAdapter?.ProcessSyntheticLegTick(
                            symbol,
                            tickData.LastTradePrice,
                            tickData.LastTradeQty,
                            tickData.ExchangeTimestamp != default ? tickData.ExchangeTimestamp : DateTime.Now,
                            ZerodhaDatafeedAdapter.SyntheticInstruments.TickType.Last,
                            tickData.BuyPrice,
                            tickData.SellPrice);
                    }
                }
            }
            catch (Exception ex)
            {
                // Only log first occurrence per symbol to reduce spam
                Logger.Debug($"[TICK-SHARED] OnSharedWebSocketTickReceived error for {symbol}: {ex.Message}");
            }
        }

        /// <summary>
        /// Checks if the symbol is an index symbol (NIFTY, SENSEX, BANKNIFTY, etc.)
        /// Indices should not be processed as synthetic straddle legs.
        /// </summary>
        private bool IsIndexSymbol(string symbol) => Helpers.SymbolHelper.IsIndexSymbol(symbol);

        /// <summary>
        /// Exit monitoring is DISABLED - all subscriptions are sticky by design.
        /// Once subscribed, connections stay active for the entire session.
        /// This prevents NinjaTrader's aggressive UnsubscribeMarketData calls from killing data feeds.
        /// </summary>
        private void StartExitMonitoringForSharedSubscription(string symbol, string consumerId, WebSocketConnectionFunc webSocketConnectionFunc)
        {
            // ALL subscriptions are sticky - no exit monitoring needed
            // Subscriptions persist for the entire session regardless of ExitFunction state
            Logger.Debug($"[TICK-SHARED] Exit monitoring DISABLED (all subscriptions are sticky): {symbol}");
        }

        /// <summary>
        /// Gets the shared WebSocket service status
        /// </summary>
        public string GetSharedWebSocketStatus()
        {
            if (_sharedWebSocketService == null)
                return "Not initialized";

            return $"Connected={_sharedWebSocketService.IsConnected}, Subscriptions={_sharedWebSocketService.SubscriptionCount}";
        }

        #endregion

        #region Direct Symbol Subscription (without NT Instrument)

        // Direct tick callbacks for symbols without NT Instrument objects (e.g., NIFTY_I/NIFTY Futures)
        private readonly ConcurrentDictionary<string, Action<double, long, DateTime>> _directTickCallbacks =
            new ConcurrentDictionary<string, Action<double, long, DateTime>>(StringComparer.OrdinalIgnoreCase);

        /// <summary>
        /// Subscribes to a symbol directly via WebSocket without requiring an NT Instrument object.
        /// This is useful for symbols like NIFTY Futures that are dynamically resolved.
        /// Ticks are delivered via the onTick callback.
        /// </summary>
        /// <param name="symbol">Symbol to subscribe to (e.g., NIFTY26JANFUT)</param>
        /// <param name="instrumentToken">Zerodha instrument token</param>
        /// <param name="isIndex">Whether this is an index symbol</param>
        /// <param name="onTick">Callback for tick data (price, volume, timestamp)</param>
        /// <returns>True if subscription succeeded</returns>
        public async Task<bool> SubscribeToSymbolDirectAsync(
            string symbol,
            int instrumentToken,
            bool isIndex,
            Action<double, long, DateTime> onTick)
        {
            try
            {
                Logger.Info($"[MDS-DIRECT] SubscribeToSymbolDirectAsync: symbol={symbol}, token={instrumentToken}, isIndex={isIndex}");

                // Register the callback
                _directTickCallbacks[symbol] = onTick;

                // Ensure WebSocket is initialized
                await EnsureSharedWebSocketInitializedAsync(new ConcurrentDictionary<string, L1Subscription>());

                // Subscribe via SharedWebSocketService
                bool subscribed = await _sharedWebSocketService.SubscribeAsync(symbol, instrumentToken, isIndex);

                if (subscribed)
                {
                    Logger.Info($"[MDS-DIRECT] Successfully subscribed to {symbol} (token={instrumentToken})");
                }
                else
                {
                    Logger.Error($"[MDS-DIRECT] Failed to subscribe to {symbol}");
                    _directTickCallbacks.TryRemove(symbol, out _);
                }

                return subscribed;
            }
            catch (Exception ex)
            {
                Logger.Error($"[MDS-DIRECT] Exception subscribing to {symbol}: {ex.Message}", ex);
                _directTickCallbacks.TryRemove(symbol, out _);
                return false;
            }
        }

        /// <summary>
        /// Processes direct tick callbacks for symbols registered via SubscribeToSymbolDirectAsync.
        /// Called from OnSharedWebSocketTickReceived.
        /// </summary>
        private void ProcessDirectTickCallback(string symbol, ZerodhaTickData tickData)
        {
            if (_directTickCallbacks.TryGetValue(symbol, out var callback))
            {
                try
                {
                    callback(tickData.LastTradePrice, tickData.TotalQtyTraded, tickData.ExchangeTimestamp);
                }
                catch (Exception ex)
                {
                    Logger.Error($"[MDS-DIRECT] Callback exception for {symbol}: {ex.Message}");
                }
            }
        }

        #endregion
    }
}
