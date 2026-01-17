using System;
using System.Collections.Generic;
using System.IO;
using ZerodhaDatafeedAdapter.Logging;

namespace ZerodhaDatafeedAdapter.Services.Simulation
{
    /// <summary>
    /// Represents a single tick from NinjaTrader NCD file.
    /// </summary>
    public readonly struct NCDTick
    {
        public readonly DateTime Timestamp;
        public readonly double Price;
        public readonly double Bid;
        public readonly double Offer;
        public readonly ulong Volume;

        public NCDTick(double bid, double offer, double price, ulong volume, DateTime timestamp)
        {
            Bid = bid;
            Offer = offer;
            Price = price;
            Volume = volume;
            Timestamp = timestamp;
        }
    }

    /// <summary>
    /// Direct reader for NinjaTrader 8 NCD (tick data) binary files.
    /// Bypasses NinjaTrader's BarsRequest API for much faster data loading.
    ///
    /// Based on reverse-engineered format from:
    /// - https://github.com/bboyle1234/NTDFileReader
    /// - https://github.com/jrstokka/NinjaTraderNCDFiles
    /// </summary>
    public static class NCDFileReader
    {
        private static readonly ILoggerService _log = LoggerFactory.Simulation;

        /// <summary>
        /// Gets the NinjaTrader 8 database path for tick data.
        /// </summary>
        public static string GetTickDataPath()
        {
            string userDocs = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
            return Path.Combine(userDocs, "NinjaTrader 8", "db", "tick");
        }

        /// <summary>
        /// Gets the folder path for a specific symbol's tick data.
        /// </summary>
        public static string GetSymbolPath(string symbol)
        {
            return Path.Combine(GetTickDataPath(), symbol);
        }

        /// <summary>
        /// Finds all NCD files for a symbol that match the given date.
        /// NCD files are named like: 202601131600.Last.ncd (YYYYMMDDHHMM.Last.ncd)
        /// The timestamp in the filename represents the END time of the data in the file.
        /// </summary>
        public static List<string> FindNcdFilesForDate(string symbol, DateTime date)
        {
            var files = new List<string>();
            string symbolPath = GetSymbolPath(symbol);

            if (!Directory.Exists(symbolPath))
            {
                _log.Debug($"[NCDFileReader] Symbol folder not found: {symbolPath}");
                return files;
            }

            // Look for .ncd files
            foreach (string file in Directory.GetFiles(symbolPath, "*.ncd"))
            {
                string fileName = Path.GetFileName(file);

                // Parse the date from filename (YYYYMMDDHHMM.Last.ncd or similar)
                if (TryParseDateFromFileName(fileName, out DateTime fileDate))
                {
                    // The file date is the END time, so we need files that could contain our date
                    // A file dated 20260114 could contain data from 20260113-20260114
                    // We'll be conservative and include files from date-1 to date+1
                    if (fileDate.Date >= date.Date.AddDays(-1) && fileDate.Date <= date.Date.AddDays(1))
                    {
                        files.Add(file);
                    }
                }
            }

            files.Sort(); // Sort chronologically
            return files;
        }

        /// <summary>
        /// Parses the date from an NCD filename.
        /// Format: YYYYMMDDHHMM.Last.ncd or YYYYMMDDHHMM.ncd
        /// </summary>
        private static bool TryParseDateFromFileName(string fileName, out DateTime date)
        {
            date = DateTime.MinValue;

            // Extract the timestamp part (first 12 characters: YYYYMMDDHHMM)
            if (fileName.Length < 12)
                return false;

            string datePart = fileName.Substring(0, 12);

            if (DateTime.TryParseExact(datePart, "yyyyMMddHHmm",
                System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.None, out date))
            {
                return true;
            }

            return false;
        }

        /// <summary>
        /// Reads all ticks from an NCD file.
        /// Uses the NinjaTrader 8 NCD binary format.
        /// </summary>
        public static IEnumerable<NCDTick> ReadTicks(string filePath)
        {
            if (!File.Exists(filePath))
            {
                _log.Warn($"[NCDFileReader] File not found: {filePath}");
                yield break;
            }

            using (var stream = File.Open(filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
            using (var br = new BinaryReader(stream))
            {
                // Read header
                // First 4 bytes: unknown (possibly version or flags)
                br.ReadUInt32();

                // Next 8 bytes: increment size (tick size)
                var incrementSize = br.ReadDouble();

                // Next 8 bytes: initial price
                var price = br.ReadDouble();

                // Next 8 bytes: initial time (in .NET ticks)
                var timeTicks = br.ReadInt64();

                // Read tick records until end of file
                while (stream.Position < stream.Length)
                {
                    NCDTick? tick = null;
                    try
                    {
                        tick = ReadSingleTick(br, ref incrementSize, ref price, ref timeTicks);
                    }
                    catch (EndOfStreamException)
                    {
                        // End of file reached
                        yield break;
                    }
                    catch (Exception ex)
                    {
                        _log.Warn($"[NCDFileReader] Error reading tick at position {stream.Position}: {ex.Message}");
                        yield break;
                    }

                    if (tick.HasValue)
                    {
                        yield return tick.Value;
                    }
                }
            }
        }

        /// <summary>
        /// Reads a single tick from the binary stream.
        /// </summary>
        private static NCDTick ReadSingleTick(BinaryReader br, ref double incrementSize, ref double price, ref long timeTicks)
        {
            var byte1 = br.ReadByte();
            var byte2 = br.ReadByte();

            // Decode time delta (bits 0-2 of byte1)
            // Based on NinjaTrader NCD format:
            // 0b000 = 0 (no change)
            // 0b001 = 1 byte delta in ticks
            // 0b010 = 2 byte big-endian delta in ticks
            // 0b011 = 4 byte big-endian delta in ticks
            // 0b100 = 8 byte big-endian delta in ticks
            // 0b101 = 1 byte delta in seconds
            // 0b110 = 2 byte big-endian delta in seconds
            // 0b111 = 4 byte big-endian delta in seconds
            int timeFlags = byte1 & 0b111;
            long timeDelta = timeFlags switch
            {
                0b000 => 0,
                0b001 => br.ReadByte(),
                0b010 => ReadBigEndianLong(br, 2),
                0b011 => ReadBigEndianLong(br, 4),
                0b100 => ReadBigEndianLong(br, 8),
                0b101 => br.ReadByte() * TimeSpan.TicksPerSecond,
                0b110 => ReadBigEndianLong(br, 2) * TimeSpan.TicksPerSecond,
                0b111 => ReadBigEndianLong(br, 4) * TimeSpan.TicksPerSecond,
                _ => throw new InvalidOperationException($"Unknown time flag: {timeFlags}")
            };
            timeTicks += timeDelta;

            // Decode price delta (bits 6-7 of byte1)
            int priceFlags = byte1 >> 6;
            int priceDelta = priceFlags switch
            {
                0b00 => 0,
                0b01 => (byte2 & 0b11111) - (1 << 4),
                0b10 => br.ReadByte() - (1 << 7),
                0b11 => (int)(ReadBigEndianUInt(br, 4) - (1U << 31)),
                _ => throw new Exception("Unknown price flag"),
            };
            price = Increment(price, incrementSize, priceDelta);

            // Decode spread (bits 3-5 of byte1)
            int spreadFlags = (byte1 >> 3) & 0b111;
            int bidOffset = spreadFlags & 0b001;
            int askOffset = 1 - bidOffset;

            switch (spreadFlags)
            {
                case 0b110:
                    var x = br.ReadByte();
                    bidOffset = x >> 4;
                    askOffset = x & 0b1111;
                    break;

                case 0b111:
                    bidOffset = br.ReadByte();
                    askOffset = br.ReadByte();
                    break;

                default:
                    if ((spreadFlags & 0b010) > 0)
                    {
                        bidOffset *= 2;
                        askOffset *= 2;
                    }
                    else if ((spreadFlags & 0b100) > 0)
                    {
                        bidOffset *= 3;
                        askOffset *= 3;
                    }
                    break;
            }

            var bid = Increment(price, incrementSize, -bidOffset);
            var offer = Increment(price, incrementSize, askOffset);

            // Decode volume (bits 5-7 of byte2)
            int volumeFlags = byte2 >> 5;
            ulong volume = volumeFlags switch
            {
                0b000 => 0,
                0b001 => br.ReadByte(),
                0b010 => 100UL * br.ReadByte(),
                0b011 => 500UL * br.ReadByte(),
                0b100 => 1000UL * br.ReadByte(),
                0b101 => ReadBigEndianULong(br, 2),
                0b110 => ReadBigEndianULong(br, 4),
                0b111 => ReadBigEndianULong(br, 8),
                _ => throw new Exception("Unknown volume flag."),
            };

            return new NCDTick(bid, offer, price, volume, new DateTime(timeTicks, DateTimeKind.Local));
        }

        /// <summary>
        /// Increment a price by a number of tick increments.
        /// </summary>
        private static double Increment(double value, double increment, int numIncrements)
        {
            if (numIncrements == 0) return value;
            return (double)((decimal)increment * ((int)Math.Round(value / increment, MidpointRounding.AwayFromZero) + numIncrements));
        }

        /// <summary>
        /// Read a big-endian long from the stream.
        /// </summary>
        private static long ReadBigEndianLong(BinaryReader br, int byteCount)
        {
            long result = br.ReadByte();
            for (int i = 1; i < byteCount; i++)
            {
                result <<= 8;
                result += br.ReadByte();
            }
            return result;
        }

        /// <summary>
        /// Read a big-endian unsigned int from the stream.
        /// </summary>
        private static uint ReadBigEndianUInt(BinaryReader br, int byteCount)
        {
            uint result = br.ReadByte();
            for (int i = 1; i < byteCount; i++)
            {
                result <<= 8;
                result += br.ReadByte();
            }
            return result;
        }

        /// <summary>
        /// Read a big-endian unsigned long from the stream.
        /// </summary>
        private static ulong ReadBigEndianULong(BinaryReader br, int byteCount)
        {
            ulong result = br.ReadByte();
            for (int i = 1; i < byteCount; i++)
            {
                result <<= 8;
                result += br.ReadByte();
            }
            return result;
        }

        /// <summary>
        /// Loads all ticks for a symbol on a specific date, filtered by time range.
        /// This is the main entry point for simulation data loading.
        /// Returns ticks sorted by timestamp.
        /// </summary>
        public static List<NCDTick> LoadTicksForSymbol(string symbol, DateTime date, TimeSpan? fromTime = null, TimeSpan? toTime = null)
        {
            var allTicks = new List<NCDTick>();
            var startTime = DateTime.Now;

            // Find NCD files that might contain data for this date
            var ncdFiles = FindNcdFilesForDate(symbol, date);

            if (ncdFiles.Count == 0)
            {
                _log.Debug($"[NCDFileReader] No NCD files found for {symbol} on {date:yyyy-MM-dd}");
                return allTicks;
            }

            // Read ticks from all relevant files
            foreach (var file in ncdFiles)
            {
                foreach (var tick in ReadTicks(file))
                {
                    // Filter by date
                    if (tick.Timestamp.Date != date.Date)
                        continue;

                    // Filter by time range if specified
                    if (fromTime.HasValue && tick.Timestamp.TimeOfDay < fromTime.Value)
                        continue;
                    if (toTime.HasValue && tick.Timestamp.TimeOfDay > toTime.Value)
                        continue;

                    allTicks.Add(tick);
                }
            }

            // Sort by timestamp
            allTicks.Sort((a, b) => a.Timestamp.CompareTo(b.Timestamp));

            var elapsed = DateTime.Now - startTime;
            _log.Info($"[NCDFileReader] Loaded {allTicks.Count} ticks for {symbol} in {elapsed.TotalMilliseconds:F0}ms (direct file read)");

            return allTicks;
        }
    }
}
