using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Text;

namespace ZerodhaDatafeedAdapter.Helpers
{
    /// <summary>
    /// TOTP (Time-based One-Time Password) Generator
    /// RFC 6238 compliant implementation for two-factor authentication
    /// </summary>
    public static class TotpGenerator
    {
        private const int DIGITS = 6;
        private const int TIME_STEP = 30;
        private const string BASE32_ALPHABET = "ABCDEFGHIJKLMNOPQRSTUVWXYZ234567";

        /// <summary>
        /// Generate a TOTP code from a Base32 encoded secret
        /// </summary>
        public static string GenerateTotp(string base32Secret)
        {
            if (string.IsNullOrEmpty(base32Secret))
                return string.Empty;

            var key = Base32Decode(base32Secret);
            var counter = GetCurrentCounter();
            var hash = ComputeHmacSha1(key, GetCounterBytes(counter));
            var code = TruncateHash(hash);
            return code.ToString().PadLeft(DIGITS, '0');
        }

        private static long GetCurrentCounter()
        {
            var unixTime = (long)(DateTime.UtcNow - new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc)).TotalSeconds;
            return unixTime / TIME_STEP;
        }

        private static byte[] GetCounterBytes(long counter)
        {
            var bytes = BitConverter.GetBytes(counter);
            if (BitConverter.IsLittleEndian)
                Array.Reverse(bytes);
            return bytes;
        }

        private static byte[] ComputeHmacSha1(byte[] key, byte[] data)
        {
            using (var hmac = new HMACSHA1(key))
            {
                return hmac.ComputeHash(data);
            }
        }

        private static int TruncateHash(byte[] hash)
        {
            var offset = hash[hash.Length - 1] & 0x0F;
            var binary = ((hash[offset] & 0x7F) << 24)
                       | ((hash[offset + 1] & 0xFF) << 16)
                       | ((hash[offset + 2] & 0xFF) << 8)
                       | (hash[offset + 3] & 0xFF);
            return binary % (int)Math.Pow(10, DIGITS);
        }

        private static byte[] Base32Decode(string base32)
        {
            // Remove spaces, hyphens and padding, then convert to uppercase
            base32 = base32.Replace(" ", "").Replace("-", "").TrimEnd('=').ToUpperInvariant();

            if (string.IsNullOrEmpty(base32))
                return new byte[0];

            var output = new List<byte>();
            int buffer = 0;
            int bitsInBuffer = 0;

            foreach (var c in base32)
            {
                var index = BASE32_ALPHABET.IndexOf(c);
                if (index < 0)
                    continue; // Skip invalid characters

                buffer = (buffer << 5) | index;
                bitsInBuffer += 5;

                if (bitsInBuffer >= 8)
                {
                    bitsInBuffer -= 8;
                    output.Add((byte)(buffer >> bitsInBuffer));
                }
            }

            return output.ToArray();
        }
    }
}
