using System;
using System.Buffers.Binary;
using System.IO;
using System.Linq;
using System.Net.WebSockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using ZerodhaDatafeedAdapter.Models;
using ZerodhaDatafeedAdapter.Models.MarketData;
using ZerodhaDatafeedAdapter.Services.Auth;
using ZerodhaDatafeedAdapter.Classes;
using ZerodhaDatafeedAdapter.Services.Zerodha;
using ZerodhaDatafeedAdapter;
using ZerodhaAPI.Zerodha.Utility;

namespace ZerodhaDatafeedAdapter.Services.WebSocket
{
    /// <summary>
    /// Manages WebSocket connections and message parsing
    /// </summary>
    public class WebSocketManager
    {
        private static WebSocketManager _instance;
        private readonly ZerodhaClient _zerodhaClient;

        /// <summary>
        /// Gets the singleton instance of the WebSocketManager
        /// </summary>
        public static WebSocketManager Instance
        {
            get
            {
                if (_instance == null)
                    _instance = new WebSocketManager();
                return _instance;
            }
        }

        /// <summary>
        /// Private constructor to enforce singleton pattern
        /// </summary>
        private WebSocketManager()
        {
            _zerodhaClient = ZerodhaClient.Instance;
        }

        /// <summary>
        /// Creates a new WebSocket client
        /// </summary>
        /// <returns>A configured ClientWebSocket instance</returns>
        public ClientWebSocket CreateWebSocketClient()
        {
            var ws = new ClientWebSocket();
            
            // Set WebSocket options for performance
            ws.Options.KeepAliveInterval = TimeSpan.FromSeconds(30);
            ws.Options.SetBuffer(32768, 32768); // Larger buffers for high-frequency data
            
            // Note: TcpNoDelay and other socket options are typically managed by the 
            // .NET Framework's underlying HttpWebRequest/WinHttpHandler used by ClientWebSocket.
            
            return ws;
        }

        /// <summary>
        /// Connects to the Zerodha WebSocket
        /// </summary>
        /// <param name="ws">The WebSocket client</param>
        /// <returns>A task representing the asynchronous operation</returns>
        public async Task ConnectAsync(ClientWebSocket ws)
        {
            string wsUrl = _zerodhaClient.GetWebSocketUrl();
            await ws.ConnectAsync(new Uri(wsUrl), CancellationToken.None);
        }

        /// <summary>
        /// Subscribes to a symbol in the specified mode
        /// </summary>
        /// <param name="ws">The WebSocket client</param>
        /// <param name="instrumentToken">The instrument token</param>
        /// <param name="mode">The subscription mode (ltp, quote, full)</param>
        /// <returns>A task representing the asynchronous operation</returns>
        public async Task SubscribeAsync(ClientWebSocket ws, int instrumentToken, string mode)
        {
            // First subscribe to the instrument
            string subscribeMsg = $@"{{""a"":""subscribe"",""v"":[{instrumentToken}]}}";
            await SendTextMessageAsync(ws, subscribeMsg);

            // Then set the mode
            string modeMsg = $@"{{""a"":""mode"",""v"":[""{ mode }"",[{instrumentToken}]]}}";
            await SendTextMessageAsync(ws, modeMsg);

            Logger.Info($"WebSocketManager: Subscribed to token {instrumentToken} in {mode} mode.");
        }

        /// <summary>
        /// Unsubscribes from a symbol
        /// </summary>
        /// <param name="ws">The WebSocket client</param>
        /// <param name="instrumentToken">The instrument token</param>
        /// <returns>A task representing the asynchronous operation</returns>
        public async Task UnsubscribeAsync(ClientWebSocket ws, int instrumentToken)
        {
            string unsubscribeMsg = $@"{{""a"":""unsubscribe"",""v"":[{instrumentToken}]}}";
            await SendTextMessageAsync(ws, unsubscribeMsg);
        }

        /// <summary>
        /// Closes the WebSocket connection
        /// </summary>
        /// <param name="ws">The WebSocket client</param>
        /// <returns>A task representing the asynchronous operation</returns>
        public async Task CloseAsync(ClientWebSocket ws)
        {
            if (ws != null && ws.State == System.Net.WebSockets.WebSocketState.Open)
            {
                try
                {
                    await ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "Connection closed", CancellationToken.None);
                }
                catch (Exception ex)
                {
                    NinjaTrader.NinjaScript.NinjaScript.Log($"[WEBSOCKET] Error closing WebSocket: {ex.Message}", NinjaTrader.Cbi.LogLevel.Error);
                }
                finally
                {
                    ws.Dispose();
                }
            }
        }

        /// <summary>
        /// Sends a text message over the WebSocket
        /// </summary>
        /// <param name="ws">The WebSocket client</param>
        /// <param name="message">The message to send</param>
        /// <returns>A task representing the asynchronous operation</returns>
        public async Task SendTextMessageAsync(ClientWebSocket ws, string message)
        {
            try
            {
                //NinjaTrader.NinjaScript.NinjaScript.Log(
                //    $"[WS-SEND] Sending WebSocket message: {message}",
                //    NinjaTrader.Cbi.LogLevel.Information);
                
                byte[] messageBytes = Encoding.UTF8.GetBytes(message);
                await ws.SendAsync(
                    new ArraySegment<byte>(messageBytes),
                    WebSocketMessageType.Text, true, CancellationToken.None);
                
                //NinjaTrader.NinjaScript.NinjaScript.Log(
                //    $"[WS-SEND-DONE] WebSocket message sent successfully",
                //    NinjaTrader.Cbi.LogLevel.Information);
            }
            catch (Exception ex)
            {
                NinjaTrader.NinjaScript.NinjaScript.Log(
                    $"[WS-SEND-ERROR] Error sending WebSocket message: {ex.Message}",
                    NinjaTrader.Cbi.LogLevel.Error);
                throw;
            }
        }

        /// <summary>
        /// Receives a message from the WebSocket
        /// </summary>
        /// <param name="ws">The WebSocket client</param>
        /// <param name="buffer">The buffer to receive the message into</param>
        /// <param name="cancellationToken">The cancellation token</param>
        /// <returns>The WebSocket receive result</returns>
        public async Task<WebSocketReceiveResult> ReceiveMessageAsync(ClientWebSocket ws, byte[] buffer, CancellationToken cancellationToken)
        {
            try
            {
                //NinjaTrader.NinjaScript.NinjaScript.Log(
                //    $"[WS-RECV-START] Waiting for WebSocket message, WebSocket state: {ws.State}",
                //    NinjaTrader.Cbi.LogLevel.Information);
                
                var result = await ws.ReceiveAsync(new ArraySegment<byte>(buffer), cancellationToken);
                
                //NinjaTrader.NinjaScript.NinjaScript.Log(
                //    $"[WS-RECV-DONE] Received WebSocket message, Type: {result.MessageType}, Count: {result.Count}, EndOfMessage: {result.EndOfMessage}",
                //    NinjaTrader.Cbi.LogLevel.Information);
                
                return result;
            }
            catch (Exception ex)
            {
                NinjaTrader.NinjaScript.NinjaScript.Log(
                    $"[WS-RECV-ERROR] Error receiving WebSocket message: {ex.Message}",
                    NinjaTrader.Cbi.LogLevel.Error);
                throw;
            }
        }

        /// <summary>
        /// Parses a binary message from Zerodha WebSocket
        /// </summary>
        /// <param name="data">The binary data</param>
        /// <param name="expectedToken">The expected instrument token</param>
        /// <param name="nativeSymbolName">The native symbol name</param>
        /// <param name="isMcxSegment">True if the instrument belongs to MCX segment, false otherwise</param>
        /// <param name="isIndex">True if this is an index (NIFTY 50, SENSEX, etc.) which has a different packet structure</param>
        /// <returns>A ZerodhaTickData object containing all market data fields</returns>
        public Models.MarketData.ZerodhaTickData ParseBinaryMessage(byte[] data, int expectedToken, string nativeSymbolName, bool isMcxSegment, bool isIndex = false)
        {
            if (data.Length < 2)
            {
                //NinjaTrader.NinjaScript.NinjaScript.Log(
                //    $"[PARSE-ERROR] Data too small for {nativeSymbolName}, length: {data.Length}",
                //    NinjaTrader.Cbi.LogLevel.Error);
                return null;
            }

            // Log the raw binary data for debugging
            string hexData = BitConverter.ToString(data, 0, Math.Min(data.Length, 64)).Replace("-", "");
            //NinjaTrader.NinjaScript.NinjaScript.Log(
            //    $"[PARSE-DEBUG] Parsing binary message for {nativeSymbolName}, token: {expectedToken}, data: {hexData}...",
            //    NinjaTrader.Cbi.LogLevel.Information);

            try
            {
                int offset = 0;
                int packetCount = ZerodhaAPI.Zerodha.Utility.ZerodhaBinaryReader.ReadInt16BE(data, offset);
                offset += 2;

                //NinjaTrader.NinjaScript.NinjaScript.Log(
                //    $"[PARSE-DEBUG] Packet count: {packetCount} for {nativeSymbolName}",
                //    NinjaTrader.Cbi.LogLevel.Information);

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

                    // Determine packet mode based on length
                    // Index packets have different lengths: 8 (LTP), 28 (Quote), 32 (Full)
                    // Tradeable instrument packets: 8 (LTP), 44 (Quote), 184 (Full)
                    bool isLtpMode = packetLength == 8;
                    bool isQuoteMode = isIndex ? (packetLength == 28) : (packetLength == 44);
                    bool isFullMode = isIndex ? (packetLength == 32) : (packetLength == 184);
                    bool isIndexPacket = isIndex && (packetLength == 8 || packetLength == 28 || packetLength == 32);

                    // MCX packets follow the same structure as NSE (184 bytes for full mode)
                    // Python testing confirmed timestamps and OI are present in MCX full mode packets

                    // Validate packet length based on instrument type
                    bool isValidPacket = isLtpMode || isQuoteMode || isFullMode ||
                                        (!isIndex && packetLength == 184) ||
                                        isIndexPacket;

                    if (!isValidPacket)
                    {
                        offset += packetLength;
                        continue;
                    }

                    // Check if this is our subscribed token
                    int iToken = ZerodhaAPI.Zerodha.Utility.ZerodhaBinaryReader.ReadInt32BE(data, offset);
                    if (iToken != expectedToken)
                    {
                        offset += packetLength;
                        continue;
                    }

                    return ParseSinglePacket(data, offset, packetLength, nativeSymbolName, isIndex, isMcxSegment, isLtpMode, isQuoteMode, isFullMode);
                }
            }
            catch (Exception ex)
            {
                Logger.Error($"WebSocketManager: Exception parsing binary message for {nativeSymbolName}: {ex.Message}", ex);
            }

            return CreateDefaultTick(expectedToken, nativeSymbolName);
        }

        private ZerodhaTickData ParseSinglePacket(byte[] data, int offset, int packetLength, string symbol, bool isIndex, bool isMcxSegment, bool isLtpMode, bool isQuoteMode, bool isFullMode)
        {
            // OPTIMIZATION: Use object pool to reduce GC pressure in hot path
            var tickData = ZerodhaTickDataPool.Rent();
            tickData.InstrumentToken = ZerodhaBinaryReader.ReadInt32BE(data, offset);
            tickData.InstrumentIdentifier = symbol;
            tickData.HasMarketDepth = false; // Quote mode doesn't include market depth
            tickData.IsIndex = isIndex;
            tickData.LastTradeTime = DateTime.Now;
            tickData.ExchangeTimestamp = DateTime.Now;

            if (isLtpMode)
            {
                ParseLtpPacket(data, offset, tickData);
            }
            else if (isIndex && (isQuoteMode || isFullMode))
            {
                ParseIndexPacket(data, offset, packetLength, tickData);
            }
            else if (isQuoteMode || isFullMode || (isMcxSegment && packetLength == 184))
            {
                ParseTradePacket(data, offset, packetLength, tickData);
                // Quote mode ends at byte 44 - no timestamps, OI, or depth
                // System time is used for timestamps (already set in tickData initialization)
            }

            return tickData;
        }

        private void ParseLtpPacket(byte[] data, int offset, ZerodhaTickData tick)
        {
            if (offset + 8 <= data.Length)
            {
                tick.LastTradePrice = ZerodhaBinaryReader.ReadInt32BE(data, offset + 4) / 100.0;
            }
        }

        private void ParseIndexPacket(byte[] data, int offset, int length, ZerodhaTickData tick)
        {
            tick.IsIndex = true;
            if (offset + 8 <= data.Length) tick.LastTradePrice = ZerodhaBinaryReader.ReadInt32BE(data, offset + 4) / 100.0;
            if (offset + 12 <= data.Length) tick.High = ZerodhaBinaryReader.ReadInt32BE(data, offset + 8) / 100.0;
            if (offset + 16 <= data.Length) tick.Low = ZerodhaBinaryReader.ReadInt32BE(data, offset + 12) / 100.0;
            if (offset + 20 <= data.Length) tick.Open = ZerodhaBinaryReader.ReadInt32BE(data, offset + 16) / 100.0;
            if (offset + 24 <= data.Length) tick.Close = ZerodhaBinaryReader.ReadInt32BE(data, offset + 20) / 100.0;

            if (length >= 32 && offset + 32 <= data.Length)
            {
                int timestamp = ZerodhaBinaryReader.ReadInt32BE(data, offset + 28);
                if (timestamp > 0)
                {
                    tick.ExchangeTimestamp = ZerodhaBinaryReader.UnixSecondsToLocalTime(timestamp);
                    tick.LastTradeTime = tick.ExchangeTimestamp;
                }
            }
        }

        private void ParseTradePacket(byte[] data, int offset, int length, ZerodhaTickData tick)
        {
            if (offset + 8 <= data.Length) tick.LastTradePrice = ZerodhaBinaryReader.ReadInt32BE(data, offset + 4) / 100.0;
            if (offset + 12 <= data.Length) tick.LastTradeQty = ZerodhaBinaryReader.ReadInt32BE(data, offset + 8);
            if (offset + 16 <= data.Length) tick.AverageTradePrice = ZerodhaBinaryReader.ReadInt32BE(data, offset + 12) / 100.0;
            if (offset + 20 <= data.Length) tick.TotalQtyTraded = ZerodhaBinaryReader.ReadInt32BE(data, offset + 16);
            if (offset + 24 <= data.Length) tick.BuyQty = ZerodhaBinaryReader.ReadInt32BE(data, offset + 20);
            if (offset + 28 <= data.Length) tick.SellQty = ZerodhaBinaryReader.ReadInt32BE(data, offset + 24);
            if (offset + 32 <= data.Length) tick.Open = ZerodhaBinaryReader.ReadInt32BE(data, offset + 28) / 100.0;
            if (offset + 36 <= data.Length) tick.High = ZerodhaBinaryReader.ReadInt32BE(data, offset + 32) / 100.0;
            if (offset + 40 <= data.Length) tick.Low = ZerodhaBinaryReader.ReadInt32BE(data, offset + 36) / 100.0;
            if (offset + 44 <= data.Length) tick.Close = ZerodhaBinaryReader.ReadInt32BE(data, offset + 40) / 100.0;
        }

        private ZerodhaTickData CreateDefaultTick(int token, string symbol)
        {
            // OPTIMIZATION: Use object pool to reduce GC pressure
            var tickData = ZerodhaTickDataPool.Rent();
            tickData.InstrumentToken = token;
            tickData.InstrumentIdentifier = symbol;
            tickData.LastTradeTime = DateTime.Now;
            tickData.ExchangeTimestamp = DateTime.Now;
            return tickData;
        }
    }
}
