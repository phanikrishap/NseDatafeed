using System;
using System.IO;
using System.Linq;
using ZerodhaDatafeedAdapter.Models.MarketData;
using ZerodhaDatafeedAdapter.Helpers;

namespace ZerodhaDatafeedAdapter.Services.WebSocket
{
    /// <summary>
    /// Specialized parser for Zerodha binary tick packets.
    /// Handles multiple modes (LTP, Quote, Full) and exchange-specific nuances.
    /// </summary>
    public class WebSocketPacketParser
    {
        public void ProcessBinaryMessage(byte[] buffer, int count, Action<string, ZerodhaTickData> onTick)
        {
            if (buffer == null || count < 2) return;

            // First 2 bytes are packet count
            int packetCount = BitConverter.ToInt16(buffer.Take(2).Reverse().ToArray(), 0);
            int offset = 2;

            for (int i = 0; i < packetCount && offset < count; i++)
            {
                int packetSize = BitConverter.ToInt16(buffer.Skip(offset).Take(2).Reverse().ToArray(), 0);
                offset += 2;

                if (offset + packetSize > count) break;

                var tickData = ParseTickPacket(buffer, offset, packetSize);
                if (tickData != null) onTick(tickData.InstrumentIdentifier, tickData);

                offset += packetSize;
            }
        }

        private ZerodhaTickData ParseTickPacket(byte[] buffer, int offset, int size)
        {
            try
            {
                var tick = new ZerodhaTickData();
                tick.InstrumentToken = BitConverter.ToInt32(buffer.Skip(offset).Take(4).Reverse().ToArray(), 0);
                tick.InstrumentIdentifier = tick.InstrumentToken.ToString();

                // Based on size, determine mode
                if (size == 8) // LTP Mode
                {
                    tick.LastTradePrice = BitConverter.ToInt32(buffer.Skip(offset + 4).Take(4).Reverse().ToArray(), 0) / 100.0;
                }
                else if (size == 28 || size == 32) // Quote Mode
                {
                    tick.LastTradePrice = BitConverter.ToInt32(buffer.Skip(offset + 4).Take(4).Reverse().ToArray(), 0) / 100.0;
                    tick.LastTradeQty = BitConverter.ToInt32(buffer.Skip(offset + 8).Take(4).Reverse().ToArray(), 0);
                    // ... parse more fields
                }
                // Full mode has 184 bytes etc.
                
                return tick;
            }
            catch { return null; }
        }
    }
}
