using System;
using System.Buffers;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Net.WebSockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using QANinjaAdapter.Models.MarketData;
using QANinjaAdapter.Services.Configuration;
using QANinjaAdapter.Services.Instruments;
using QANinjaAdapter.Services.Zerodha;

namespace QANinjaAdapter.Services.WebSocket
{
    /// <summary>
    /// Manages a single shared WebSocket connection for all symbol subscriptions.
    /// Zerodha allows 3 connections with up to 1000 symbols each.
    /// This service uses one connection for all symbols (typically &lt;100).
    /// </summary>
    public class SharedWebSocketService : IDisposable
    {
        private static SharedWebSocketService _instance;
        private static readonly object _instanceLock = new object();

        // Shared WebSocket connection
        private ClientWebSocket _sharedWebSocket;
        private CancellationTokenSource _connectionCts;
        private Task _messageLoopTask;
        private bool _isConnected = false;
        private readonly object _connectionLock = new object();

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

        /// <summary>
        /// Ensures the WebSocket connection is established
        /// </summary>
        public async Task<bool> EnsureConnectedAsync()
        {
            lock (_connectionLock)
            {
                if (_isConnected && _sharedWebSocket?.State == WebSocketState.Open)
                    return true;
            }

            return await ConnectAsync();
        }

        /// <summary>
        /// Establishes the WebSocket connection
        /// </summary>
        private async Task<bool> ConnectAsync()
        {
            try
            {
                lock (_connectionLock)
                {
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
                }

                Logger.Info("[SharedWS] Connecting to Zerodha WebSocket...");

                _sharedWebSocket = _webSocketManager.CreateWebSocketClient();
                await _webSocketManager.ConnectAsync(_sharedWebSocket);

                lock (_connectionLock)
                {
                    _isConnected = true;
                }

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
                lock (_connectionLock)
                {
                    _isConnected = false;
                }
                return false;
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
                if (!_isConnected || _sharedWebSocket?.State != WebSocketState.Open)
                {
                    Logger.Info($"[SharedWS] Connection not ready, queueing subscription for {symbol}");
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

                if (!_isConnected || _sharedWebSocket?.State != WebSocketState.Open)
                {
                    Logger.Info($"[SharedWS] Connection not ready, queueing {symbols.Count} subscriptions");
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
        /// Sends a text message over the WebSocket
        /// </summary>
        private async Task SendTextMessageAsync(string message)
        {
            try
            {
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
                lock (_connectionLock)
                {
                    _isConnected = false;
                }
                Logger.Info("[SharedWS] Message processing loop ended");

                // Attempt reconnection
                if (!token.IsCancellationRequested)
                {
                    Logger.Info("[SharedWS] Attempting reconnection in 2 seconds...");
                    await Task.Delay(2000);
                    _ = ConnectAsync();
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
                int packetCount = ReadInt16BE(data, offset);
                offset += 2;

                for (int i = 0; i < packetCount; i++)
                {
                    if (offset + 2 > length) break;

                    int packetLength = ReadInt16BE(data, offset);
                    offset += 2;

                    if (offset + packetLength > length) break;

                    // Get instrument token
                    int instrumentToken = ReadInt32BE(data, offset);

                    // Find the symbol for this token
                    if (_tokenToSymbolMap.TryGetValue(instrumentToken, out string symbol))
                    {
                        _subscriptions.TryGetValue(symbol, out var subscription);
                        bool isIndex = subscription?.IsIndex ?? false;

                        var tickData = ParseTickPacket(data, offset, packetLength, symbol, isIndex);
                        if (tickData != null)
                        {
                            // Fire tick event
                            TickReceived?.Invoke(symbol, tickData);
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

        /// <summary>
        /// Parses a single tick packet
        /// </summary>
        private ZerodhaTickData ParseTickPacket(byte[] data, int offset, int packetLength, string symbol, bool isIndex)
        {
            try
            {
                var tickData = new ZerodhaTickData
                {
                    InstrumentToken = ReadInt32BE(data, offset),
                    InstrumentIdentifier = symbol,
                    IsIndex = isIndex,
                    LastTradeTime = DateTime.Now,
                    ExchangeTimestamp = DateTime.Now
                };

                // Determine packet type by length
                // Index packets: 8 (LTP), 28 (Quote), 32 (Full)
                // Tradeable: 8 (LTP), 44 (Quote), 184 (Full)

                if (packetLength == 8)
                {
                    // LTP packet
                    tickData.LastTradePrice = ReadInt32BE(data, offset + 4) / 100.0;
                }
                else if (isIndex && (packetLength == 28 || packetLength == 32))
                {
                    // Index quote/full packet
                    tickData.LastTradePrice = ReadInt32BE(data, offset + 4) / 100.0;
                    if (offset + 12 <= data.Length) tickData.High = ReadInt32BE(data, offset + 8) / 100.0;
                    if (offset + 16 <= data.Length) tickData.Low = ReadInt32BE(data, offset + 12) / 100.0;
                    if (offset + 20 <= data.Length) tickData.Open = ReadInt32BE(data, offset + 16) / 100.0;
                    if (offset + 24 <= data.Length) tickData.Close = ReadInt32BE(data, offset + 20) / 100.0;
                }
                else if (packetLength >= 44)
                {
                    // Quote or full packet for tradeable instruments
                    tickData.LastTradePrice = ReadInt32BE(data, offset + 4) / 100.0;
                    if (offset + 12 <= data.Length) tickData.LastTradeQty = ReadInt32BE(data, offset + 8);
                    if (offset + 16 <= data.Length) tickData.AverageTradePrice = ReadInt32BE(data, offset + 12) / 100.0;
                    if (offset + 20 <= data.Length) tickData.TotalQtyTraded = ReadInt32BE(data, offset + 16);
                    if (offset + 24 <= data.Length) tickData.BuyQty = ReadInt32BE(data, offset + 20);
                    if (offset + 28 <= data.Length) tickData.SellQty = ReadInt32BE(data, offset + 24);
                    if (offset + 32 <= data.Length) tickData.Open = ReadInt32BE(data, offset + 28) / 100.0;
                    if (offset + 36 <= data.Length) tickData.High = ReadInt32BE(data, offset + 32) / 100.0;
                    if (offset + 40 <= data.Length) tickData.Low = ReadInt32BE(data, offset + 36) / 100.0;
                    if (offset + 44 <= data.Length) tickData.Close = ReadInt32BE(data, offset + 40) / 100.0;
                }

                return tickData;
            }
            catch (Exception ex)
            {
                Logger.Error($"[SharedWS] ParseTickPacket error for {symbol}: {ex.Message}");
                return null;
            }
        }

        /// <summary>
        /// Reads a 16-bit integer in big-endian format
        /// </summary>
        private static int ReadInt16BE(byte[] data, int offset)
        {
            return (data[offset] << 8) | data[offset + 1];
        }

        /// <summary>
        /// Reads a 32-bit integer in big-endian format
        /// </summary>
        private static int ReadInt32BE(byte[] data, int offset)
        {
            return (data[offset] << 24) | (data[offset + 1] << 16) | (data[offset + 2] << 8) | data[offset + 3];
        }

        /// <summary>
        /// Gets the current connection status
        /// </summary>
        public bool IsConnected => _isConnected && _sharedWebSocket?.State == WebSocketState.Open;

        /// <summary>
        /// Gets the number of active subscriptions
        /// </summary>
        public int SubscriptionCount => _subscriptions.Count;

        public void Dispose()
        {
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
