using System;
using System.Buffers;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Net.WebSockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;
using ZerodhaDatafeedAdapter.Helpers;
using ZerodhaDatafeedAdapter.Models.MarketData;
using ZerodhaDatafeedAdapter.Services.Zerodha;

namespace ZerodhaDatafeedAdapter.Services.WebSocket
{
    /// <summary>
    /// Manages a single shared WebSocket connection for all symbol subscriptions.
    /// Orchestrates connection lifecycle, heartbeats, and subscription requests.
    /// Uses WebSocketManager for connection management (same as original).
    /// </summary>
    public class SharedWebSocketService : IDisposable
    {
        private static readonly Lazy<SharedWebSocketService> _instance = new Lazy<SharedWebSocketService>(() => new SharedWebSocketService());
        public static SharedWebSocketService Instance => _instance.Value;

        private readonly WebSocketManager _webSocketManager;
        private readonly ConcurrentDictionary<int, string> _tokenToSymbolMap = new ConcurrentDictionary<int, string>();
        private readonly ConcurrentDictionary<string, int> _symbolToTokenMap = new ConcurrentDictionary<string, int>();
        private readonly ConcurrentDictionary<string, SymbolSubscription> _subscriptions = new ConcurrentDictionary<string, SymbolSubscription>();
        private readonly ConcurrentQueue<(string symbol, int token)> _pendingSubscriptions = new ConcurrentQueue<(string, int)>();
        private readonly SemaphoreSlim _sendSemaphore = new SemaphoreSlim(1, 1);
        private readonly SemaphoreSlim _connectionSemaphore = new SemaphoreSlim(1, 1);

        private ClientWebSocket _webSocket;
        private CancellationTokenSource _cts;
        private Task _messageLoopTask;
        private int _connectionState = (int)WebSocketConnectionState.Disconnected;
        private int _isProcessingPending = 0;

        public event Action<string, ZerodhaTickData> TickReceived;
        public event Action ConnectionReady;
        public event Action ConnectionLost;

        public bool IsConnected => CurrentState == WebSocketConnectionState.Connected && _webSocket?.State == System.Net.WebSockets.WebSocketState.Open;
        public int SubscriptionCount => _subscriptions.Count;

        private SharedWebSocketService()
        {
            _webSocketManager = WebSocketManager.Instance;
            Logger.Info("[SharedWS] SharedWebSocketService initialized");
        }

        #region State Machine Helpers

        private WebSocketConnectionState CurrentState => (WebSocketConnectionState)Volatile.Read(ref _connectionState);

        private bool TryTransition(WebSocketConnectionState fromState, WebSocketConnectionState toState)
        {
            int result = Interlocked.CompareExchange(ref _connectionState, (int)toState, (int)fromState);
            bool success = result == (int)fromState;
            if (success)
            {
                Logger.Info($"[SharedWS] State: {fromState} -> {toState}");
            }
            return success;
        }

        private void ForceState(WebSocketConnectionState newState)
        {
            var oldState = (WebSocketConnectionState)Interlocked.Exchange(ref _connectionState, (int)newState);
            Logger.Info($"[SharedWS] State: {oldState} -> {newState} (forced)");
        }

        #endregion

        /// <summary>
        /// Ensures the WebSocket connection is established (uses WebSocketManager internally).
        /// </summary>
        public async Task<bool> EnsureConnectedAsync()
        {
            var state = CurrentState;

            // Fast path - already connected
            if (state == WebSocketConnectionState.Connected && _webSocket?.State == System.Net.WebSockets.WebSocketState.Open)
                return true;

            // If disposing, reject
            if (state == WebSocketConnectionState.Disposing)
                return false;

            // If already connecting/reconnecting, wait for it
            if (state == WebSocketConnectionState.Connecting || state == WebSocketConnectionState.Reconnecting)
            {
                for (int i = 0; i < 100; i++)
                {
                    await Task.Delay(100);
                    state = CurrentState;
                    if (state == WebSocketConnectionState.Connected && _webSocket?.State == System.Net.WebSockets.WebSocketState.Open)
                        return true;
                    if (state != WebSocketConnectionState.Connecting && state != WebSocketConnectionState.Reconnecting)
                        break;
                }
                return CurrentState == WebSocketConnectionState.Connected && _webSocket?.State == System.Net.WebSockets.WebSocketState.Open;
            }

            return await ConnectAsync();
        }

        public async Task<bool> WaitForConnectionAsync(int timeoutMs)
        {
            // First ensure connection is initiated
            var connectTask = EnsureConnectedAsync();

            // Wait for connection with timeout
            var completedTask = await Task.WhenAny(connectTask, Task.Delay(timeoutMs));

            return IsConnected;
        }

        private async Task<bool> ConnectAsync(bool isReconnect = false)
        {
            var targetState = isReconnect ? WebSocketConnectionState.Reconnecting : WebSocketConnectionState.Connecting;
            if (!TryTransition(WebSocketConnectionState.Disconnected, targetState))
            {
                var current = CurrentState;
                if (current == WebSocketConnectionState.Connected && _webSocket?.State == System.Net.WebSockets.WebSocketState.Open)
                    return true;
                if (current == WebSocketConnectionState.Connecting || current == WebSocketConnectionState.Reconnecting)
                {
                    Logger.Debug("[SharedWS] Connection already in progress, waiting...");
                    await _connectionSemaphore.WaitAsync();
                    _connectionSemaphore.Release();
                    return CurrentState == WebSocketConnectionState.Connected && _webSocket?.State == System.Net.WebSockets.WebSocketState.Open;
                }
                if (current == WebSocketConnectionState.Disposing)
                    return false;
            }

            await _connectionSemaphore.WaitAsync();

            try
            {
                if (CurrentState == WebSocketConnectionState.Connected && _webSocket?.State == System.Net.WebSockets.WebSocketState.Open)
                    return true;

                // Cleanup existing connection
                if (_webSocket != null)
                {
                    try
                    {
                        if (_webSocket.State == System.Net.WebSockets.WebSocketState.Open)
                        {
                            _webSocket.CloseAsync(WebSocketCloseStatus.NormalClosure, "Reconnecting", CancellationToken.None).Wait(1000);
                        }
                        _webSocket.Dispose();
                    }
                    catch { }
                    _webSocket = null;
                }

                if (_cts != null)
                {
                    _cts.Cancel();
                    _cts.Dispose();
                }
                _cts = new CancellationTokenSource();

                Logger.Info("[SharedWS] Connecting to Zerodha WebSocket...");

                // Use WebSocketManager which handles URL/credentials internally
                _webSocket = _webSocketManager.CreateWebSocketClient();
                await _webSocketManager.ConnectAsync(_webSocket);

                ForceState(WebSocketConnectionState.Connected);
                Logger.Info($"[SharedWS] Connected successfully. State={_webSocket.State}");

                ConnectionReady?.Invoke();

                // Start message processing loop
                _messageLoopTask = Task.Run(() => ProcessMessagesAsync(_cts.Token));

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
                if (CurrentState != WebSocketConnectionState.Connected || _webSocket?.State != System.Net.WebSockets.WebSocketState.Open)
                {
                    Logger.Info($"[SharedWS] Connection not ready (state={CurrentState}), queueing subscription for {symbol}");
                    _pendingSubscriptions.Enqueue((symbol, instrumentToken));
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

        private async Task SendSubscriptionAsync(List<int> tokens, string action)
        {
            if (_webSocket?.State != System.Net.WebSockets.WebSocketState.Open)
            {
                Logger.Warn($"[SharedWS] Cannot send {action} - WebSocket not open");
                return;
            }

            string message = $"{{\"a\":\"{action}\",\"v\":[{string.Join(",", tokens)}]}}";
            await SendTextMessageAsync(message);
            Logger.Debug($"[SharedWS] Sent {action} for {tokens.Count} tokens");
        }

        private async Task SendModeAsync(List<int> tokens, string mode)
        {
            if (_webSocket?.State != System.Net.WebSockets.WebSocketState.Open)
            {
                Logger.Warn($"[SharedWS] Cannot set mode - WebSocket not open");
                return;
            }

            string message = $"{{\"a\":\"mode\",\"v\":[\"{mode}\",[{string.Join(",", tokens)}]]}}";
            await SendTextMessageAsync(message);
            Logger.Debug($"[SharedWS] Set {mode} mode for {tokens.Count} tokens");
        }

        private async Task SendTextMessageAsync(string message)
        {
            await _sendSemaphore.WaitAsync(_cts.Token);
            try
            {
                if (_webSocket?.State != System.Net.WebSockets.WebSocketState.Open)
                {
                    Logger.Warn("[SharedWS] SendTextMessage: WebSocket not open, skipping send");
                    return;
                }

                byte[] messageBytes = Encoding.UTF8.GetBytes(message);
                await _webSocket.SendAsync(
                    new ArraySegment<byte>(messageBytes),
                    WebSocketMessageType.Text,
                    true,
                    _cts.Token);
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

        private async Task ResubscribeAllAsync()
        {
            var tokens = _subscriptions.Values.Select(s => s.Token).ToList();
            if (tokens.Count == 0) return;

            Logger.Info($"[SharedWS] Resubscribing to {tokens.Count} existing subscriptions");
            await SendSubscriptionAsync(tokens, "subscribe");
            await SendModeAsync(tokens, "quote");
        }

        private async Task ProcessPendingSubscriptionsAsync()
        {
            // Use atomic compare-exchange to prevent race condition
            if (Interlocked.CompareExchange(ref _isProcessingPending, 1, 0) != 0)
                return;

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
                Interlocked.Exchange(ref _isProcessingPending, 0);
            }
        }

        private async Task ProcessMessagesAsync(CancellationToken token)
        {
            Logger.Info("[SharedWS] Message processing loop started");
            byte[] buffer = ArrayPool<byte>.Shared.Rent(32768);

            try
            {
                while (_webSocket?.State == System.Net.WebSockets.WebSocketState.Open && !token.IsCancellationRequested)
                {
                    try
                    {
                        var result = await _webSocket.ReceiveAsync(new ArraySegment<byte>(buffer), token);

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

                if (CurrentState != WebSocketConnectionState.Disposing)
                {
                    ForceState(WebSocketConnectionState.Disconnected);
                }
                Logger.Info("[SharedWS] Message processing loop ended");
                ConnectionLost?.Invoke();

                // Attempt reconnection
                if (!token.IsCancellationRequested && CurrentState != WebSocketConnectionState.Disposing)
                {
                    Logger.Info("[SharedWS] Attempting reconnection in 2 seconds...");
                    await Task.Delay(2000);
                    _ = ConnectAsync(isReconnect: true);
                }
            }
        }

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

                    int instrumentToken = BinaryHelper.ReadInt32BE(data, offset);

                    if (_tokenToSymbolMap.TryGetValue(instrumentToken, out string symbol))
                    {
                        _subscriptions.TryGetValue(symbol, out var subscription);
                        bool isIndex = subscription?.IsIndex ?? false;

                        var tickData = ParseTickPacket(data, offset, packetLength, symbol, isIndex);
                        if (tickData != null)
                        {
                            var handler = TickReceived;
                            if (handler != null)
                            {
                                try
                                {
                                    handler.Invoke(symbol, tickData);
                                }
                                catch (Exception handlerEx)
                                {
                                    // Return tick to pool on handler exception to prevent pool exhaustion
                                    Logger.Error($"[SharedWS] TickReceived handler exception for {symbol}: {handlerEx.Message}");
                                    ZerodhaTickDataPool.Return(tickData);
                                }
                            }
                            else
                            {
                                ZerodhaTickDataPool.Return(tickData);
                            }
                        }
                    }

                    offset += packetLength;
                }
            }
            catch (Exception ex)
            {
                Logger.Error($"[SharedWS] ProcessBinaryMessage error: {ex.Message}");
            }
        }

        private ZerodhaTickData ParseTickPacket(byte[] data, int offset, int packetLength, string symbol, bool isIndex)
        {
            try
            {
                var tickData = ZerodhaTickDataPool.Rent();
                tickData.InstrumentToken = BinaryHelper.ReadInt32BE(data, offset);
                tickData.InstrumentIdentifier = symbol;
                tickData.IsIndex = isIndex;
                tickData.LastTradeTime = DateTime.Now;
                tickData.ExchangeTimestamp = DateTime.Now;

                if (packetLength == 8)
                {
                    tickData.LastTradePrice = BinaryHelper.ReadInt32BE(data, offset + 4) / 100.0;
                }
                else if (isIndex && (packetLength == 28 || packetLength == 32))
                {
                    tickData.LastTradePrice = BinaryHelper.ReadInt32BE(data, offset + 4) / 100.0;
                    if (offset + 12 <= data.Length) tickData.High = BinaryHelper.ReadInt32BE(data, offset + 8) / 100.0;
                    if (offset + 16 <= data.Length) tickData.Low = BinaryHelper.ReadInt32BE(data, offset + 12) / 100.0;
                    if (offset + 20 <= data.Length) tickData.Open = BinaryHelper.ReadInt32BE(data, offset + 16) / 100.0;
                    if (offset + 24 <= data.Length) tickData.Close = BinaryHelper.ReadInt32BE(data, offset + 20) / 100.0;
                }
                else if (packetLength >= 44)
                {
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

        public void Dispose()
        {
            ForceState(WebSocketConnectionState.Disposing);

            try
            {
                _cts?.Cancel();

                if (_webSocket != null)
                {
                    if (_webSocket.State == System.Net.WebSockets.WebSocketState.Open)
                    {
                        _webSocket.CloseAsync(WebSocketCloseStatus.NormalClosure, "Disposing", CancellationToken.None).Wait(1000);
                    }
                    _webSocket.Dispose();
                }

                _cts?.Dispose();
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
    /// Represents a symbol subscription with metadata
    /// </summary>
    public class SymbolSubscription
    {
        public string Symbol { get; set; }
        public int Token { get; set; }
        public bool IsIndex { get; set; }
        public DateTime SubscribedAt { get; set; }
    }

    /// <summary>
    /// WebSocket connection states (matching original implementation)
    /// </summary>
    public enum WebSocketConnectionState
    {
        Disconnected,
        Connecting,
        Connected,
        Reconnecting,
        Disposing
    }
}
