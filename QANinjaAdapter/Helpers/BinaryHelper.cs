using System;

namespace QANinjaAdapter.Helpers
{
    /// <summary>
    /// Binary data parsing utilities for big-endian network byte order.
    /// Used for parsing Zerodha WebSocket binary tick data.
    /// </summary>
    public static class BinaryHelper
    {
        /// <summary>
        /// Reads a 16-bit integer in big-endian format from byte array.
        /// </summary>
        /// <param name="data">Source byte array</param>
        /// <param name="offset">Starting offset in array</param>
        /// <returns>16-bit integer value</returns>
        public static int ReadInt16BE(byte[] data, int offset)
        {
            return (data[offset] << 8) | data[offset + 1];
        }

        /// <summary>
        /// Reads a 32-bit integer in big-endian format from byte array.
        /// </summary>
        /// <param name="data">Source byte array</param>
        /// <param name="offset">Starting offset in array</param>
        /// <returns>32-bit integer value</returns>
        public static int ReadInt32BE(byte[] data, int offset)
        {
            return (data[offset] << 24) | (data[offset + 1] << 16) | (data[offset + 2] << 8) | data[offset + 3];
        }

        /// <summary>
        /// Reads a 64-bit integer in big-endian format from byte array.
        /// </summary>
        /// <param name="data">Source byte array</param>
        /// <param name="offset">Starting offset in array</param>
        /// <returns>64-bit integer value</returns>
        public static long ReadInt64BE(byte[] data, int offset)
        {
            return ((long)data[offset] << 56) |
                   ((long)data[offset + 1] << 48) |
                   ((long)data[offset + 2] << 40) |
                   ((long)data[offset + 3] << 32) |
                   ((long)data[offset + 4] << 24) |
                   ((long)data[offset + 5] << 16) |
                   ((long)data[offset + 6] << 8) |
                   data[offset + 7];
        }

        /// <summary>
        /// Safely reads a 32-bit integer with bounds checking.
        /// Returns 0 if offset is out of bounds.
        /// </summary>
        public static int SafeReadInt32BE(byte[] data, int offset)
        {
            if (offset + 4 > data.Length) return 0;
            return ReadInt32BE(data, offset);
        }

        /// <summary>
        /// Safely reads a 16-bit integer with bounds checking.
        /// Returns 0 if offset is out of bounds.
        /// </summary>
        public static int SafeReadInt16BE(byte[] data, int offset)
        {
            if (offset + 2 > data.Length) return 0;
            return ReadInt16BE(data, offset);
        }
    }
}
