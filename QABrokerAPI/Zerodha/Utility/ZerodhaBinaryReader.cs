using System;

namespace QABrokerAPI.Zerodha.Utility
{
    /// <summary>
    /// Utility for reading Zerodha binary packets using big-endian format.
    /// Uses manual byte manipulation to avoid System.Buffers.Binary dependency
    /// which causes assembly loading issues in NinjaTrader.
    /// </summary>
    public static class ZerodhaBinaryReader
    {
        /// <summary>
        /// Reads a 16-bit integer in big-endian format
        /// </summary>
        public static short ReadInt16BE(byte[] buffer, int offset)
        {
            return (short)((buffer[offset] << 8) | buffer[offset + 1]);
        }

        /// <summary>
        /// Reads a 32-bit integer in big-endian format
        /// </summary>
        public static int ReadInt32BE(byte[] buffer, int offset)
        {
            return (buffer[offset] << 24) | (buffer[offset + 1] << 16) | (buffer[offset + 2] << 8) | buffer[offset + 3];
        }

        /// <summary>
        /// Converts a Zerodha/Unix timestamp to local DateTime
        /// </summary>
        public static DateTime UnixSecondsToLocalTime(int unixTimestamp)
        {
            var epoch = new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc);
            DateTime utcTime = epoch.AddSeconds(unixTimestamp);
            return utcTime.ToLocalTime();
        }
    }
}
