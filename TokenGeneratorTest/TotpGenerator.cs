using System;
using System.Collections.Generic;
using System.Security.Cryptography;

namespace TokenGeneratorTest
{
    /// <summary>
    /// TOTP (Time-based One-Time Password) Generator
    /// Implements RFC 6238 for generating TOTP codes from Base32 secrets
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
            int offset = hash[hash.Length - 1] & 0x0F;
            int binary =
                ((hash[offset] & 0x7F) << 24) |
                ((hash[offset + 1] & 0xFF) << 16) |
                ((hash[offset + 2] & 0xFF) << 8) |
                (hash[offset + 3] & 0xFF);

            int otp = binary % (int)Math.Pow(10, DIGITS);
            return otp;
        }

        /// <summary>
        /// Decode a Base32-encoded string to bytes
        /// </summary>
        private static byte[] Base32Decode(string base32)
        {
            // Clean input: remove spaces, hyphens, and convert to uppercase
            base32 = base32.Replace(" ", "").Replace("-", "").ToUpperInvariant();

            // Remove any padding
            base32 = base32.TrimEnd('=');

            if (string.IsNullOrEmpty(base32))
                return new byte[0];

            // Calculate output length (5 bits per Base32 character)
            int outputLength = base32.Length * 5 / 8;
            var result = new byte[outputLength];

            int buffer = 0;
            int bitsInBuffer = 0;
            int resultIndex = 0;

            foreach (char c in base32)
            {
                int value = BASE32_ALPHABET.IndexOf(c);
                if (value < 0)
                    continue; // Skip invalid characters

                // Add 5 bits to buffer
                buffer = (buffer << 5) | value;
                bitsInBuffer += 5;

                // If we have 8 or more bits, extract a byte
                if (bitsInBuffer >= 8)
                {
                    bitsInBuffer -= 8;
                    if (resultIndex < outputLength)
                    {
                        result[resultIndex++] = (byte)(buffer >> bitsInBuffer);
                    }
                }
            }

            return result;
        }
    }
}
