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
using QABrokerAPI.Common.Enums;
using QABrokerAPI.Zerodha.Websockets;
using QABrokerAPI.Zerodha.Utility;
using QANinjaAdapter.Models;
using QANinjaAdapter.Models.MarketData;
using QANinjaAdapter.Services.Configuration;
using QANinjaAdapter.Services.Instruments;
using QANinjaAdapter.Services.WebSocket;
using QANinjaAdapter;
using QANinjaAdapter.Classes;

namespace QANinjaAdapter.Services.MarketData
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
        private bool _useOptimizedProcessor = true; // Feature flag for easy rollback

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
            if (_useOptimizedProcessor)
            {
                // Use TickProcessor property to ensure it's initialized before updating cache
                TickProcessor.UpdateSubscriptionCache(subscriptions);
            }
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

        /// <summary>
        /// Subscribes to real-time ticks for a symbol
        /// </summary>
        /// <param name="nativeSymbolName">The native symbol name</param>
        /// <param name="marketType">The market type</param>
        /// <param name="symbol">The symbol</param>
        /// <param name="l1Subscriptions">The L1 subscriptions dictionary</param>
        /// <param name="webSocketConnectionFunc">The WebSocket connection function</param>
        /// <returns>A task representing the asynchronous operation</returns>
        public async Task SubscribeToTicks(
            string nativeSymbolName,
            MarketType marketType,
            string symbol,
            ConcurrentDictionary<string, L1Subscription> l1Subscriptions,
            WebSocketConnectionFunc webSocketConnectionFunc)
        {
            Logger.Info($"[TICK-SUBSCRIBE] SubscribeToTicks called: nativeSymbol='{nativeSymbolName}', symbol='{symbol}', marketType={marketType}");

            if (string.IsNullOrEmpty(symbol) || webSocketConnectionFunc == null)
            {
                Logger.Error($"[TICK-SUBSCRIBE] Invalid parameters for {symbol} - symbol empty or webSocketFunc null");
                NinjaTrader.NinjaScript.NinjaScript.Log($"[TICK-SUBSCRIBE] Invalid parameters for {symbol}", NinjaTrader.Cbi.LogLevel.Error);
                return;
            }

            int retryCount = 0;
            const int maxRetries = 10;
            var cts = new CancellationTokenSource();

            // Start monitoring chart-close in separate task
            StartExitMonitoringTask(webSocketConnectionFunc, cts);
            Logger.Debug($"[TICK-SUBSCRIBE] Exit monitoring task started for {nativeSymbolName}");

            while (!cts.Token.IsCancellationRequested && retryCount < maxRetries)
            {
                ClientWebSocket ws = null;
                byte[] buffer = null;

                try
                {
                    // Exponential backoff for reconnection
                    if (retryCount > 0)
                    {
                        int delay = Math.Min(10000, (int)Math.Pow(2, retryCount) * 500);
                        await Task.Delay(delay, cts.Token);
                        Logger.Info($"[TICK-RECONNECT] Reconnecting {nativeSymbolName} (Attempt {retryCount}/{maxRetries}) after {delay}ms");
                        NinjaTrader.NinjaScript.NinjaScript.Log($"[TICK-RECONNECT] Reconnecting {nativeSymbolName} (Attempt {retryCount}/{maxRetries}) after {delay}ms", NinjaTrader.Cbi.LogLevel.Information);
                    }

                    Logger.Debug($"[TICK-SUBSCRIBE] Creating WebSocket client for {nativeSymbolName}...");
                    ws = _webSocketManager.CreateWebSocketClient();

                    Logger.Debug($"[TICK-SUBSCRIBE] Connecting WebSocket for {nativeSymbolName}...");
                    await _webSocketManager.ConnectAsync(ws);
                    Logger.Info($"[TICK-SUBSCRIBE] WebSocket connected for {nativeSymbolName}, state={ws.State}");

                    Logger.Debug($"[TICK-SUBSCRIBE] Getting instrument token for symbol='{symbol}'...");
                    int tokenInt = (int)(await _instrumentManager.GetInstrumentToken(symbol));
                    Logger.Info($"[TICK-SUBSCRIBE] Got token {tokenInt} for symbol='{symbol}'");

                    Logger.Debug($"[TICK-SUBSCRIBE] Subscribing to token {tokenInt} in 'quote' mode...");
                    await _webSocketManager.SubscribeAsync(ws, tokenInt, "quote");
                    Logger.Info($"[TICK-SUBSCRIBE] Subscribed to token {tokenInt} for {nativeSymbolName}");

                    // Get segment and index info
                    string segment = _instrumentManager.GetSegmentForToken(tokenInt);
                    // Relaxed check: match "MCX" or "MCX-OPT" or "MCX-FUT"
                    bool isMcxSegment = !string.IsNullOrEmpty(segment) && segment.ToUpperInvariant().Contains("MCX");

                    bool isIndex = false;
                    if (l1Subscriptions.TryGetValue(nativeSymbolName, out var sub))
                        isIndex = sub.IsIndex;

                    Logger.Info($"[TICK-SUBSCRIBE] {nativeSymbolName}: segment={segment}, isMcx={isMcxSegment}, isIndex={isIndex}, l1Subscriptions.Count={l1Subscriptions.Count}");

                    // Use ArrayPool for network buffer
                    buffer = ArrayPool<byte>.Shared.Rent(16384);
                    Logger.Debug($"[TICK-SUBSCRIBE] Entering receive loop for {nativeSymbolName}...");

                    while (ws.State == WebSocketState.Open && !cts.Token.IsCancellationRequested)
                    {
                        WebSocketReceiveResult result = await _webSocketManager.ReceiveMessageAsync(ws, buffer, cts.Token);

                        if (result.MessageType == WebSocketMessageType.Close) break;
                        if (result.MessageType == WebSocketMessageType.Text || result.Count < 2) continue;

                        var tickData = _webSocketManager.ParseBinaryMessage(buffer, tokenInt, nativeSymbolName, isMcxSegment, isIndex);

                        if (tickData != null)
                        {
                            if (_useOptimizedProcessor)
                            {
                                TickProcessor.QueueTick(nativeSymbolName, tickData);
                                // Note: Subscription cache update is handled by the caller or periodically
                            }
                            else
                            {
                                QAAdapter adapter = Connector.Instance.GetAdapter() as QAAdapter;
                                adapter?.ProcessParsedTick(nativeSymbolName, tickData);
                            }

                            // SYNTHETIC STRADDLE INTEGRATION:
                            // Always route ticks to the synthetic service if the adapter is available.
                            // The service will filter out non-leg instruments internally for performance.
                            QAAdapter qaAdapter = Connector.Instance.GetAdapter() as QAAdapter;
                            if (qaAdapter != null)
                            {
                                qaAdapter.ProcessSyntheticLegTick(
                                    nativeSymbolName, 
                                    tickData.LastTradePrice, 
                                    tickData.LastTradeQty, 
                                    tickData.ExchangeTimestamp, 
                                    QANinjaAdapter.SyntheticInstruments.TickType.Last,
                                    tickData.BuyPrice, 
                                    tickData.SellPrice);
                            }
                        }
                    }

                    // If we reach here, the connection was closed normally or a break occurred
                    retryCount = 0; // Reset retry count if we had a successful session
                }
                catch (OperationCanceledException) { break; }
                catch (Exception ex)
                {
                    retryCount++;
                    NinjaTrader.NinjaScript.NinjaScript.Log($"[TICK-SUBSCRIBE] Error for {nativeSymbolName}: {ex.Message}. Retry {retryCount}/{maxRetries}", NinjaTrader.Cbi.LogLevel.Warning);
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

                    int tokenInt = (int)(await _instrumentManager.GetInstrumentToken(symbol));
                    await _webSocketManager.SubscribeAsync(ws, tokenInt, "full");

                    buffer = ArrayPool<byte>.Shared.Rent(16384);

                    while (ws.State == WebSocketState.Open && !cts.Token.IsCancellationRequested)
                    {
                        WebSocketReceiveResult result = await _webSocketManager.ReceiveMessageAsync(ws, buffer, cts.Token);

                        if (result.MessageType == WebSocketMessageType.Close) break;
                        if (result.MessageType == WebSocketMessageType.Text || result.Count < 2) continue;

                        // Parse and queue depth data
                        var tickData = _webSocketManager.ParseBinaryMessage(buffer, tokenInt, nativeSymbolName, false, false);
                        if (tickData != null && tickData.HasMarketDepth)
                        {
                            if (_useOptimizedProcessor)
                            {
                                TickProcessor.UpdateL2SubscriptionCache(l2Subscriptions);
                                TickProcessor.QueueTick(nativeSymbolName, tickData);
                            }
                            else
                            {
                                ProcessDepthData(buffer, tokenInt, nativeSymbolName, l2Subscriptions);
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
            int packetCount = QABrokerAPI.Zerodha.Utility.ZerodhaBinaryReader.ReadInt16BE(data, offset);
            offset += 2;

            for (int i = 0; i < packetCount; i++)
            {
                // Check if we have enough data for packet length
                if (offset + 2 > data.Length)
                    break;

                int packetLength = QABrokerAPI.Zerodha.Utility.ZerodhaBinaryReader.ReadInt16BE(data, offset);
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
                int iToken = QABrokerAPI.Zerodha.Utility.ZerodhaBinaryReader.ReadInt32BE(data, offset);
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
                int iToken = QABrokerAPI.Zerodha.Utility.ZerodhaBinaryReader.ReadInt32BE(data, offset);

                // Get segment information for MCX check
                string segment = _instrumentManager.GetSegmentForToken(iToken);
                bool isMcxSegment = !string.IsNullOrEmpty(segment) && segment.Equals("MCX", StringComparison.OrdinalIgnoreCase);

                // Parse the binary message into a rich data structure
                // Note: Market depth packets are only for tradeable instruments (not indices), so isIndex is always false here
                var tickData = _webSocketManager.ParseBinaryMessage(data, iToken, nativeSymbolName, isMcxSegment, isIndex: false);
                
                if (tickData == null || !tickData.HasMarketDepth)
                {
                    return;
                }

                // Get the current time in Indian Standard Time
                DateTime now = TimeZoneInfo.ConvertTime(DateTime.Now, TimeZoneInfo.FindSystemTimeZoneById(Constants.IndianTimeZoneId));

                //NinjaTrader.NinjaScript.NinjaScript.Log(
                //    $"[DEPTH-TIME] Using time {now:HH:mm:ss.fff} with Kind={now.Kind} for market depth updates",
                //    NinjaTrader.Cbi.LogLevel.Information);

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
            return TimeZoneInfo.ConvertTime(dateTime, TimeZoneInfo.FindSystemTimeZoneById(Constants.IndianTimeZoneId));
        }

        /// <summary>
        /// Converts a Unix timestamp to local time
        /// </summary>
        /// <param name="unixTimestamp">The Unix timestamp</param>
        /// <returns>The local DateTime</returns>
        private DateTime UnixSecondsToLocalTime(int unixTimestamp)
        {
            var epoch = new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc);
            return epoch.AddSeconds(unixTimestamp).ToLocalTime();
        }
    }
}
