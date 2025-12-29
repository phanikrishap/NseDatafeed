using System;
using System.Buffers;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Net.WebSockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using ZerodhaDatafeedAdapter.Helpers;
using ZerodhaDatafeedAdapter.Models;
using ZerodhaDatafeedAdapter.Models.MarketData;
using ZerodhaDatafeedAdapter.Services.Configuration;
using ZerodhaDatafeedAdapter.Services.Instruments;
using ZerodhaDatafeedAdapter.Services.Zerodha;

namespace ZerodhaDatafeedAdapter.Services.WebSocket
{
    // Note: WebSocketConnectionState enum has been extracted to ZerodhaDatafeedAdapter.Models namespace

    /// <summary>
    /// Manages a single shared WebSocket connection for all symbol subscriptions.
    /// Zerodha allows 3 connections with up to 1000 symbols each.
    /// This service uses one connection for all symbols (typically &lt;100).
    /// Uses state machine pattern for reliable connection management.
    /// </summary>
    public class SharedWebSocketService : IDisposable
    {
        private static SharedWebSocketService _instance;
        private static readonly object _instanceLock = new object();

        // Shared WebSocket connection
        private ClientWebSocket _sharedWebSocket;
        private CancellationTokenSource _connectionCts;
        private Task _messageLoopTask;

        // State machine - replaces boolean flags for atomic transitions
        private int _connectionState = (int)WebSocketConnectionState.Disconnected;

        private readonly SemaphoreSlim _connectionSemaphore = new SemaphoreSlim(1, 1);

        // Semaphore to serialize WebSocket send operations (WebSocket only allows one outstanding SendAsync at a time)
        private readonly SemaphoreSlim _sendSemaphore = new SemaphoreSlim(1, 1);

        // Subscription management
        private readonly ConcurrentDictionary<int, string> _tokenToSymbolMap = new ConcurrentDictionary<int, string>();
        private readonly ConcurrentDictionary<string, int> _symbolToTokenMap = new ConcurrentDictionary<string, int>();
        private readonly ConcurrentDictionary<string, SymbolSubscription> _subscriptions = new ConcurrentDictionary<string, SymbolSubscription>();

        // Pending subscriptions (queued while connecting)
        private readonly ConcurrentQueue<(string symbol, int token)> _pendingSubscriptions = new ConcurrentQueue<(string, int)>();
        private bool _isProcessingPending = false;

        // Dependencies
        private readonly WebSocketManager _webSocketManager;
        private readonly InstrumentManager _instrumentManager;

        // Event for tick data
        public event Action<string, ZerodhaTickData> TickReceived;

        /// <summary>
        /// Gets the singleton instance
        /// </summary>
        public static SharedWebSocketService Instance
        {
            get
            {
                if (_instance == null)
                {
                    lock (_instanceLock)
                    {
                        if (_instance == null)
                            _instance = new SharedWebSocketService();
                    }
                }
                return _instance;
            }
        }

        private SharedWebSocketService()
        {
            _webSocketManager = WebSocketManager.Instance;
            _instrumentManager = InstrumentManager.Instance;
            Logger.Info("[SharedWS] SharedWebSocketService initialized");
        }

        #region State Machine Helpers

        /// <summary>
        /// Gets the current connection state (thread-safe read)
        /// </summary>
        private WebSocketConnectionState CurrentState => (WebSocketConnectionState)Volatile.Read(ref _connectionState);

        /// <summary>
        /// Attempts atomic state transition. Returns true if transition succeeded.
        /// </summary>
        private bool TryTransition(WebSocketConnectionState fromState, WebSocketConnectionState toState)
        {
            int result = Interlocked.CompareExchange(
                ref _connectionState,
                (int)toState,
                (int)fromState);
            bool success = result == (int)fromState;
            if (success)
            {
                Logger.Info($"[SharedWS] State: {fromState} -> {toState}");
            }
            return success;
        }

        /// <summary>
        /// Forces state transition (use carefully - mainly for cleanup)
        /// </summary>
        private void ForceState(WebSocketConnectionState newState)
        {
            var oldState = (WebSocketConnectionState)Interlocked.Exchange(ref _connectionState, (int)newState);
            Logger.Info($"[SharedWS] State: {oldState} -> {newState} (forced)");
        }

        /// <summary>
        /// Checks if currently in a connected or connecting state
        /// </summary>
        private bool IsConnectedOrConnecting => CurrentState == WebSocketConnectionState.Connected ||
                                                  CurrentState == WebSocketConnectionState.Connecting ||
                                                  CurrentState == WebSocketConnectionState.Reconnecting;

        #endregion

        /// <summary>
        /// Ensures the WebSocket connection is established
        /// </summary>
        public async Task<bool> EnsureConnectedAsync()
        {
            var state = CurrentState;

            // Fast path - already connected
            if (state == WebSocketConnectionState.Connected && _sharedWebSocket?.State == WebSocketState.Open)
                return true;

            // If disposing, reject
            if (state == WebSocketConnectionState.Disposing)
                return false;

            // If already connecting/reconnecting, wait for it
            if (state == WebSocketConnectionState.Connecting || state == WebSocketConnectionState.Reconnecting)
            {
                // Wait up to 10 seconds for connection to complete
                for (int i = 0; i < 100; i++)
                {
                    await Task.Delay(100);
                    state = CurrentState;
                    if (state == WebSocketConnectionState.Connected && _sharedWebSocket?.State == WebSocketState.Open)
                        return true;
                    if (state != WebSocketConnectionState.Connecting && state != WebSocketConnectionState.Reconnecting)
                        break;
                }
                return CurrentState == WebSocketConnectionState.Connected && _sharedWebSocket?.State == WebSocketState.Open;
            }

            return await ConnectAsync();
        }

        /// <summary>
        /// Establishes the WebSocket connection
        /// </summary>
        private async Task<bool> ConnectAsync(bool isReconnect = false)
        {
            // Attempt state transition: Disconnected -> Connecting (or Disconnected -> Reconnecting)
            var targetState = isReconnect ? WebSocketConnectionState.Reconnecting : WebSocketConnectionState.Connecting;
            if (!TryTransition(WebSocketConnectionState.Disconnected, targetState))
            {
                var current = CurrentState;
                // If already connected, return success
                if (current == WebSocketConnectionState.Connected && _sharedWebSocket?.State == WebSocketState.Open)
                    return true;
                // If already connecting, wait for it
                if (current == WebSocketConnectionState.Connecting || current == WebSocketConnectionState.Reconnecting)
                {
                    Logger.Debug("[SharedWS] Connection already in progress, waiting...");
                    await _connectionSemaphore.WaitAsync();
                    _connectionSemaphore.Release();
                    return CurrentState == WebSocketConnectionState.Connected && _sharedWebSocket?.State == WebSocketState.Open;
                }
                // If disposing, reject
                if (current == WebSocketConnectionState.Disposing)
                    return false;
            }

            // Use semaphore to prevent concurrent connection attempts
            await _connectionSemaphore.WaitAsync();

            try
            {
                // Double-check after acquiring semaphore
                if (CurrentState == WebSocketConnectionState.Connected && _sharedWebSocket?.State == WebSocketState.Open)
                {
                    return true;
                }

                // Cleanup existing connection
                if (_sharedWebSocket != null)
                {
                    try
                    {
                        if (_sharedWebSocket.State == WebSocketState.Open)
                        {
                            _sharedWebSocket.CloseAsync(WebSocketCloseStatus.NormalClosure, "Reconnecting", CancellationToken.None).Wait(1000);
                        }
                        _sharedWebSocket.Dispose();
                    }
                    catch { }
                    _sharedWebSocket = null;
                }

                if (_connectionCts != null)
                {
                    _connectionCts.Cancel();
                    _connectionCts.Dispose();
                }
                _connectionCts = new CancellationTokenSource();

                Logger.Info("[SharedWS] Connecting to Zerodha WebSocket...");

                _sharedWebSocket = _webSocketManager.CreateWebSocketClient();
                await _webSocketManager.ConnectAsync(_sharedWebSocket);

                // Transition to Connected state
                ForceState(WebSocketConnectionState.Connected);

                Logger.Info($"[SharedWS] Connected successfully. State={_sharedWebSocket.State}");

                // Start message processing loop
                _messageLoopTask = Task.Run(() => ProcessMessagesAsync(_connectionCts.Token));

                // Re-subscribe to any existing subscriptions
                await ResubscribeAllAsync();

                // Process any pending subscriptions
                await ProcessPendingSubscriptionsAsync();

                return true;
            }
            catch (Exception ex)
            {
                Logger.Error($"[SharedWS] Connection failed: {ex.Message}", ex);
                ForceState(WebSocketConnectionState.Disconnected);
                return false;
            }
            finally
            {
                _connectionSemaphore.Release();
            }
        }

        /// <summary>
        /// Subscribes to a symbol for tick data
        /// </summary>
        public async Task<bool> SubscribeAsync(string symbol, int instrumentToken, bool isIndex = false)
        {
            try
            {
                Logger.Info($"[SharedWS] Subscribe request: symbol={symbol}, token={instrumentToken}, isIndex={isIndex}");

                // Add to mappings
                _tokenToSymbolMap[instrumentToken] = symbol;
                _symbolToTokenMap[symbol] = instrumentToken;
                _subscriptions[symbol] = new SymbolSubscription
                {
                    Symbol = symbol,
                    Token = instrumentToken,
                    IsIndex = isIndex,
                    SubscribedAt = DateTime.UtcNow
                };

                // If not connected, queue for later
                if (CurrentState != WebSocketConnectionState.Connected || _sharedWebSocket?.State != WebSocketState.Open)
                {
                    Logger.Info($"[SharedWS] Connection not ready (state={CurrentState}), queueing subscription for {symbol}");
                    _pendingSubscriptions.Enqueue((symbol, instrumentToken));

                    // Try to connect
                    _ = EnsureConnectedAsync();
                    return true;
                }

                // Send subscription message
                await SendSubscriptionAsync(new List<int> { instrumentToken }, "subscribe");
                await SendModeAsync(new List<int> { instrumentToken }, "quote");

                Logger.Info($"[SharedWS] Subscribed to {symbol} (token={instrumentToken})");
                return true;
            }
            catch (Exception ex)
            {
                Logger.Error($"[SharedWS] Subscribe failed for {symbol}: {ex.Message}", ex);
                return false;
            }
        }

        /// <summary>
        /// Batch subscribes to multiple symbols
        /// </summary>
        public async Task<bool> BatchSubscribeAsync(List<(string symbol, int token, bool isIndex)> symbols)
        {
            try
            {
                Logger.Info($"[SharedWS] Batch subscribe request for {symbols.Count} symbols");

                var tokens = new List<int>();
                foreach (var (symbol, token, isIndex) in symbols)
                {
                    _tokenToSymbolMap[token] = symbol;
                    _symbolToTokenMap[symbol] = token;
                    _subscriptions[symbol] = new SymbolSubscription
                    {
                        Symbol = symbol,
                        Token = token,
                        IsIndex = isIndex,
                        SubscribedAt = DateTime.UtcNow
                    };
                    tokens.Add(token);
                }

                if (CurrentState != WebSocketConnectionState.Connected || _sharedWebSocket?.State != WebSocketState.Open)
                {
                    Logger.Info($"[SharedWS] Connection not ready (state={CurrentState}), queueing {symbols.Count} subscriptions");
                    foreach (var (symbol, token, _) in symbols)
                    {
                        _pendingSubscriptions.Enqueue((symbol, token));
                    }
                    _ = EnsureConnectedAsync();
                    return true;
                }

                // Send batch subscription
                await SendSubscriptionAsync(tokens, "subscribe");
                await SendModeAsync(tokens, "quote");

                Logger.Info($"[SharedWS] Batch subscribed to {tokens.Count} symbols");
                return true;
            }
            catch (Exception ex)
            {
                Logger.Error($"[SharedWS] Batch subscribe failed: {ex.Message}", ex);
                return false;
            }
        }

        /// <summary>
        /// Unsubscribes from a symbol
        /// </summary>
        public async Task UnsubscribeAsync(string symbol)
        {
            try
            {
                if (!_symbolToTokenMap.TryGetValue(symbol, out int token))
                {
                    Logger.Debug($"[SharedWS] UnsubscribeAsync: Symbol {symbol} not found in mappings");
                    return;
                }

                Logger.Info($"[SharedWS] ACTUAL UnsubscribeAsync called for {symbol} (token={token}) - Checking if this is unexpected!");

                // Remove from mappings
                _tokenToSymbolMap.TryRemove(token, out _);
                _symbolToTokenMap.TryRemove(symbol, out _);
                _subscriptions.TryRemove(symbol, out _);

                // Send unsubscribe message if connected
                if (CurrentState == WebSocketConnectionState.Connected && _sharedWebSocket?.State == WebSocketState.Open)
                {
                    await SendSubscriptionAsync(new List<int> { token }, "unsubscribe");
                    Logger.Info($"[SharedWS] Unsubscribed from {symbol} (token={token})");
                }
            }
            catch (Exception ex)
            {
                Logger.Error($"[SharedWS] Unsubscribe failed for {symbol}: {ex.Message}", ex);
            }
        }

        /// <summary>
        /// Sends subscription message for multiple tokens
        /// </summary>
        private async Task SendSubscriptionAsync(List<int> tokens, string action)
        {
            if (_sharedWebSocket?.State != WebSocketState.Open)
            {
                Logger.Warn($"[SharedWS] Cannot send {action} - WebSocket not open");
                return;
            }

            string message = $"{{\"a\":\"{action}\",\"v\":[{string.Join(",", tokens)}]}}";
            await SendTextMessageAsync(message);
            Logger.Debug($"[SharedWS] Sent {action} for {tokens.Count} tokens");
        }

        /// <summary>
        /// Sets the mode for multiple tokens
        /// </summary>
        private async Task SendModeAsync(List<int> tokens, string mode)
        {
            if (_sharedWebSocket?.State != WebSocketState.Open)
            {
                Logger.Warn($"[SharedWS] Cannot set mode - WebSocket not open");
                return;
            }

            string message = $"{{\"a\":\"mode\",\"v\":[\"{mode}\",[{string.Join(",", tokens)}]]}}";
            await SendTextMessageAsync(message);
            Logger.Debug($"[SharedWS] Set {mode} mode for {tokens.Count} tokens");
        }

        /// <summary>
        /// Sends a text message over the WebSocket (serialized via semaphore)
        /// </summary>
        private async Task SendTextMessageAsync(string message)
        {
            // WebSocket only allows one outstanding SendAsync at a time - serialize with semaphore
            await _sendSemaphore.WaitAsync(_connectionCts.Token);
            try
            {
                // Double-check WebSocket is still open after acquiring semaphore
                if (_sharedWebSocket?.State != WebSocketState.Open)
                {
                    Logger.Warn("[SharedWS] SendTextMessage: WebSocket not open, skipping send");
                    return;
                }

                byte[] messageBytes = Encoding.UTF8.GetBytes(message);
                await _sharedWebSocket.SendAsync(
                    new ArraySegment<byte>(messageBytes),
                    WebSocketMessageType.Text,
                    true,
                    _connectionCts.Token);
            }
            catch (Exception ex)
            {
                Logger.Error($"[SharedWS] SendTextMessage failed: {ex.Message}", ex);
                throw;
            }
            finally
            {
                _sendSemaphore.Release();
            }
        }

        /// <summary>
        /// Resubscribes to all existing subscriptions (after reconnect)
        /// </summary>
        private async Task ResubscribeAllAsync()
        {
            var tokens = _subscriptions.Values.Select(s => s.Token).ToList();
            if (tokens.Count == 0) return;

            Logger.Info($"[SharedWS] Resubscribing to {tokens.Count} existing subscriptions");
            await SendSubscriptionAsync(tokens, "subscribe");
            await SendModeAsync(tokens, "quote");
        }

        /// <summary>
        /// Processes queued subscriptions
        /// </summary>
        private async Task ProcessPendingSubscriptionsAsync()
        {
            if (_isProcessingPending) return;
            _isProcessingPending = true;

            try
            {
                var tokens = new List<int>();
                while (_pendingSubscriptions.TryDequeue(out var pending))
                {
                    tokens.Add(pending.token);
                }

                if (tokens.Count > 0)
                {
                    Logger.Info($"[SharedWS] Processing {tokens.Count} pending subscriptions");
                    await SendSubscriptionAsync(tokens, "subscribe");
                    await SendModeAsync(tokens, "quote");
                }
            }
            finally
            {
                _isProcessingPending = false;
            }
        }

        /// <summary>
        /// Main message processing loop
        /// </summary>
        private async Task ProcessMessagesAsync(CancellationToken token)
        {
            Logger.Info("[SharedWS] Message processing loop started");
            byte[] buffer = ArrayPool<byte>.Shared.Rent(32768);

            try
            {
                while (_sharedWebSocket?.State == WebSocketState.Open && !token.IsCancellationRequested)
                {
                    try
                    {
                        var result = await _sharedWebSocket.ReceiveAsync(new ArraySegment<byte>(buffer), token);

                        if (result.MessageType == WebSocketMessageType.Close)
                        {
                            Logger.Warn("[SharedWS] Server closed connection");
                            break;
                        }

                        if (result.MessageType == WebSocketMessageType.Binary && result.Count >= 2)
                        {
                            ProcessBinaryMessage(buffer, result.Count);
                        }
                    }
                    catch (OperationCanceledException)
                    {
                        break;
                    }
                    catch (Exception ex)
                    {
                        Logger.Error($"[SharedWS] Message receive error: {ex.Message}");
                        await Task.Delay(100, token);
                    }
                }
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(buffer);

                // Transition to Disconnected (if not already disposing)
                if (CurrentState != WebSocketConnectionState.Disposing)
                {
                    ForceState(WebSocketConnectionState.Disconnected);
                }
                Logger.Info("[SharedWS] Message processing loop ended");

                // Attempt reconnection (only if not disposing)
                if (!token.IsCancellationRequested && CurrentState != WebSocketConnectionState.Disposing)
                {
                    Logger.Info("[SharedWS] Attempting reconnection in 2 seconds...");
                    await Task.Delay(2000);
                    _ = ConnectAsync(isReconnect: true);
                }
            }
        }

        /// <summary>
        /// Processes a binary message containing one or more tick packets
        /// </summary>
        private void ProcessBinaryMessage(byte[] data, int length)
        {
            try
            {
                int offset = 0;
                int packetCount = BinaryHelper.ReadInt16BE(data, offset);
                offset += 2;

                for (int i = 0; i < packetCount; i++)
                {
                    if (offset + 2 > length) break;

                    int packetLength = BinaryHelper.ReadInt16BE(data, offset);
                    offset += 2;

                    if (offset + packetLength > length) break;

                    // Get instrument token
                    int instrumentToken = BinaryHelper.ReadInt32BE(data, offset);

                    // Find the symbol for this token
                    if (_tokenToSymbolMap.TryGetValue(instrumentToken, out string symbol))
                    {
                        _subscriptions.TryGetValue(symbol, out var subscription);
                        bool isIndex = subscription?.IsIndex ?? false;

                        // DEBUG: Heartbeat for specific options to trace data flow
                        if (!isIndex && symbol.Contains("SENSEX") && packetCount > 10) // Log occasionally
                        {
                             // Reduce spam via modulo if needed, or just log
                             // Logger.Info($"[SharedWS-HEARTBEAT] Received tick for {symbol}");
                        }

                        var tickData = ParseTickPacket(data, offset, packetLength, symbol, isIndex);
                        if (tickData != null)
                        {
                            // POOL FIX: Check if there are any listeners before invoking
                            // If no listeners, return tickData to pool immediately
                            var handler = TickReceived;
                            if (handler != null)
                            {
                                handler.Invoke(symbol, tickData);
                            }
                            else
                            {
                                // No listeners - return to pool to prevent leak
                                ZerodhaTickDataPool.Return(tickData);
                            }
                        }
                    }
                    else
                    {
                         // Token not found in map - vital debug info
                         if (instrumentToken > 0)
                            Logger.Debug($"[SharedWS-DROP] Token {instrumentToken} received but not in map!");
                    }

                    offset += packetLength;
                }
            }
            catch (Exception ex)
            {
                Logger.Error($"[SharedWS] ProcessBinaryMessage error: {ex.Message}");
            }
        }

        /// <summary>
        /// Parses a single tick packet
        /// </summary>
        private ZerodhaTickData ParseTickPacket(byte[] data, int offset, int packetLength, string symbol, bool isIndex)
        {
            try
            {
                // OPTIMIZATION: Use object pool to reduce GC pressure in hot path
                var tickData = ZerodhaTickDataPool.Rent();
                tickData.InstrumentToken = BinaryHelper.ReadInt32BE(data, offset);
                tickData.InstrumentIdentifier = symbol;
                tickData.IsIndex = isIndex;
                tickData.LastTradeTime = DateTime.Now;
                tickData.ExchangeTimestamp = DateTime.Now;

                // Determine packet type by length
                // Index packets: 8 (LTP), 28 (Quote), 32 (Full)
                // Tradeable: 8 (LTP), 44 (Quote), 184 (Full)

                if (packetLength == 8)
                {
                    // LTP packet
                    tickData.LastTradePrice = BinaryHelper.ReadInt32BE(data, offset + 4) / 100.0;
                }
                else if (isIndex && (packetLength == 28 || packetLength == 32))
                {
                    // Index quote/full packet
                    tickData.LastTradePrice = BinaryHelper.ReadInt32BE(data, offset + 4) / 100.0;
                    if (offset + 12 <= data.Length) tickData.High = BinaryHelper.ReadInt32BE(data, offset + 8) / 100.0;
                    if (offset + 16 <= data.Length) tickData.Low = BinaryHelper.ReadInt32BE(data, offset + 12) / 100.0;
                    if (offset + 20 <= data.Length) tickData.Open = BinaryHelper.ReadInt32BE(data, offset + 16) / 100.0;
                    if (offset + 24 <= data.Length) tickData.Close = BinaryHelper.ReadInt32BE(data, offset + 20) / 100.0;
                }
                else if (packetLength >= 44)
                {
                    // Quote or full packet for tradeable instruments
                    tickData.LastTradePrice = BinaryHelper.ReadInt32BE(data, offset + 4) / 100.0;
                    if (offset + 12 <= data.Length) tickData.LastTradeQty = BinaryHelper.ReadInt32BE(data, offset + 8);
                    if (offset + 16 <= data.Length) tickData.AverageTradePrice = BinaryHelper.ReadInt32BE(data, offset + 12) / 100.0;
                    if (offset + 20 <= data.Length) tickData.TotalQtyTraded = BinaryHelper.ReadInt32BE(data, offset + 16);
                    if (offset + 24 <= data.Length) tickData.BuyQty = BinaryHelper.ReadInt32BE(data, offset + 20);
                    if (offset + 28 <= data.Length) tickData.SellQty = BinaryHelper.ReadInt32BE(data, offset + 24);
                    if (offset + 32 <= data.Length) tickData.Open = BinaryHelper.ReadInt32BE(data, offset + 28) / 100.0;
                    if (offset + 36 <= data.Length) tickData.High = BinaryHelper.ReadInt32BE(data, offset + 32) / 100.0;
                    if (offset + 40 <= data.Length) tickData.Low = BinaryHelper.ReadInt32BE(data, offset + 36) / 100.0;
                    if (offset + 44 <= data.Length) tickData.Close = BinaryHelper.ReadInt32BE(data, offset + 40) / 100.0;
                }

                return tickData;
            }
            catch (Exception ex)
            {
                Logger.Error($"[SharedWS] ParseTickPacket error for {symbol}: {ex.Message}");
                return null;
            }
        }

        // Note: ReadInt16BE and ReadInt32BE have been extracted to BinaryHelper class

        /// <summary>
        /// Gets the current connection status
        /// </summary>
        public bool IsConnected => CurrentState == WebSocketConnectionState.Connected && _sharedWebSocket?.State == WebSocketState.Open;

        /// <summary>
        /// Gets the current connection state for diagnostics
        /// </summary>
        public WebSocketConnectionState ConnectionState => CurrentState;

        /// <summary>
        /// Gets the number of active subscriptions
        /// </summary>
        public int SubscriptionCount => _subscriptions.Count;

        public void Dispose()
        {
            // Set disposing state first to prevent reconnection attempts
            ForceState(WebSocketConnectionState.Disposing);

            try
            {
                _connectionCts?.Cancel();

                if (_sharedWebSocket != null)
                {
                    if (_sharedWebSocket.State == WebSocketState.Open)
                    {
                        _sharedWebSocket.CloseAsync(WebSocketCloseStatus.NormalClosure, "Disposing", CancellationToken.None).Wait(1000);
                    }
                    _sharedWebSocket.Dispose();
                }

                _connectionCts?.Dispose();
                _connectionSemaphore?.Dispose();
                _sendSemaphore?.Dispose();
                Logger.Info("[SharedWS] Disposed");
            }
            catch (Exception ex)
            {
                Logger.Error($"[SharedWS] Dispose error: {ex.Message}");
            }
        }
    }

    /// <summary>
    /// Represents a symbol subscription
    /// </summary>
    public class SymbolSubscription
    {
        public string Symbol { get; set; }
        public int Token { get; set; }
        public bool IsIndex { get; set; }
        public DateTime SubscribedAt { get; set; }
    }
}
