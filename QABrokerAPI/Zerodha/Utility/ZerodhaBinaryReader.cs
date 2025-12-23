using System;
using System.Buffers.Binary;

namespace QABrokerAPI.Zerodha.Utility
{
    /// <summary>
    /// Utility for reading Zerodha binary packets using big-endian format
    /// </summary>
    public static class ZerodhaBinaryReader
    {
        public static short ReadInt16BE(byte[] buffer, int offset)
        {
            return BinaryPrimitives.ReadInt16BigEndian(buffer.AsSpan(offset));
        }

        public static int ReadInt32BE(byte[] buffer, int offset)
        {
            return BinaryPrimitives.ReadInt32BigEndian(buffer.AsSpan(offset));
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
