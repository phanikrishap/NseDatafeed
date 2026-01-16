using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using ZerodhaDatafeedAdapter.Services.Historical;

namespace TokenGeneratorTest
{
    /// <summary>
    /// Test class for Accelpix API historical tick data download
    /// Tests API connectivity and data retrieval for NIFTY and SENSEX options
    /// </summary>
    public static class AccelpixHistoricalDataTest
    {
        // Config file path
        private static readonly string CONFIG_PATH = Path.Combine(
            AppDomain.CurrentDomain.BaseDirectory, "..", "..", "config", "accelpixCred.json");

        // Cached master data to avoid multiple downloads
        private static List<AccelpixSymbolMaster> _cachedMasters = null;

        public static async Task RunTestAsync()
        {
            Console.WriteLine();
            Console.WriteLine("===========================================");
            Console.WriteLine("  Accelpix API Historical Data Test");
            Console.WriteLine("  NIFTY Strike: 26000, SENSEX Strike: 85000");
            Console.WriteLine("===========================================");
            Console.WriteLine();

            try
            {
                // Step 1: Load API credentials
                Console.WriteLine("[1] Loading Accelpix credentials...");
                string configPath = Path.GetFullPath(CONFIG_PATH);
                Console.WriteLine($"    Config path: {configPath}");

                if (!File.Exists(configPath))
                {
                    Console.WriteLine($"    ERROR: Config file not found!");
                    return;
                }

                var config = JObject.Parse(File.ReadAllText(configPath));
                string apiKey = config["apiKey"]?.ToString();
                string apiExpiry = config["apiExpiry"]?.ToString();

                if (string.IsNullOrEmpty(apiKey))
                {
                    Console.WriteLine("    ERROR: apiKey not found in config!");
                    return;
                }

                Console.WriteLine($"    API Key: {apiKey.Substring(0, Math.Min(10, apiKey.Length))}...");
                Console.WriteLine($"    API Expiry: {apiExpiry}");
                Console.WriteLine();

                using (var client = new AccelpixApiClient(apiKey, logger: (lvl, msg) => Console.WriteLine($"[{lvl}] {msg}")))
                {
                    // Step 2: Test connection
                    Console.WriteLine("[2] Testing API connection...");
                    bool connected = await client.TestConnectionAsync();
                    if (!connected)
                    {
                        Console.WriteLine("    WARNING: Connection test returned unexpected result.");
                        Console.WriteLine("    Continuing anyway...");
                    }
                    else
                    {
                        Console.WriteLine("    Connection successful!");
                    }
                    Console.WriteLine();

                    // Step 3: Load master data once
                    Console.WriteLine("[3] Loading symbol master data...");
                    await LoadMasterData(client);
                    Console.WriteLine();

                    // Step 4: Compare NIFTY weekly vs monthly expiry naming
                    Console.WriteLine("[4] NIFTY Options - Weekly vs Monthly naming patterns (Strike: 26000)...");
                    Console.WriteLine("    ---------------------------------------------------------------");
                    await CompareNiftyExpiryPatterns(client);
                    Console.WriteLine();

                    // Step 5: Compare SENSEX weekly vs monthly expiry naming
                    Console.WriteLine("[5] SENSEX Options - Weekly vs Monthly naming patterns (Strike: 85000)...");
                    Console.WriteLine("    ---------------------------------------------------------------");
                    await CompareSensexExpiryPatterns(client);
                    Console.WriteLine();

                    // Step 6: Test tick data for specific options
                    Console.WriteLine("[6] Testing tick data download for options...");
                    await TestTickDataForSpecificOptions(client);
                    Console.WriteLine();
                }

                Console.WriteLine("===========================================");
                Console.WriteLine("  Test completed!");
                Console.WriteLine("===========================================");
            }
            catch (Exception ex)
            {
                Console.WriteLine();
                Console.WriteLine("===========================================");
                Console.WriteLine("  TEST FAILED!");
                Console.WriteLine("===========================================");
                Console.WriteLine($"  Error: {ex.Message}");
                Console.WriteLine();
                Console.WriteLine("Stack Trace:");
                Console.WriteLine(ex.StackTrace);
            }
        }

        private static async Task LoadMasterData(AccelpixApiClient client)
        {
            if (_cachedMasters != null)
            {
                Console.WriteLine($"    Using cached master data ({_cachedMasters.Count:N0} symbols)");
                return;
            }

            _cachedMasters = await client.GetMasterDataAsync(includeLotSize: true);

            if (_cachedMasters == null || _cachedMasters.Count == 0)
            {
                Console.WriteLine("    ERROR: No master data received");
                return;
            }

            Console.WriteLine($"    Total symbols: {_cachedMasters?.Count:N0}");
        }

        private static async Task CompareNiftyExpiryPatterns(AccelpixApiClient client)
        {
            if (_cachedMasters == null) return;

            // NIFTY Weekly: 2026-01-13 (Tuesday - weekly expiry)
            // NIFTY Monthly: 2026-01-29 (Thursday - monthly expiry, last Thursday of Jan)
            var weeklyExpiry = "2026-01-13";
            var monthlyExpiry = "2026-01-29";
            decimal targetStrike = 26000;

            Console.WriteLine($"\n    Weekly Expiry: {weeklyExpiry}");
            Console.WriteLine($"    Monthly Expiry: {monthlyExpiry}");
            Console.WriteLine($"    Target Strike: {targetStrike}");

            // Find weekly options at strike 26000
            var weeklyOptions = _cachedMasters.Where(m =>
            {
                return m.Underlying == "NIFTY" &&
                       (m.ExpiryDate?.StartsWith(weeklyExpiry) ?? false) &&
                       m.StrikePrice == targetStrike;
            }).ToList();

            Console.WriteLine($"\n    WEEKLY ({weeklyExpiry}) options at strike {targetStrike}:");
            foreach (var opt in weeklyOptions)
            {
                Console.WriteLine($"      Ticker: {opt.Ticker,-25}");
            }

            // Find monthly options at strike 26000
            var monthlyOptions = _cachedMasters.Where(m =>
            {
                return m.Underlying == "NIFTY" &&
                       (m.ExpiryDate?.StartsWith(monthlyExpiry) ?? false) &&
                       m.StrikePrice == targetStrike;
            }).ToList();

            Console.WriteLine($"\n    MONTHLY ({monthlyExpiry}) options at strike {targetStrike}:");
            if (monthlyOptions.Count == 0)
            {
                Console.WriteLine("      No options found with YYMMDD format. Checking YYMMM format...");

                // Search for NIFTY26JAN format
                var monthlyYYMMM = _cachedMasters.Where(m =>
                {
                    return m.Underlying == "NIFTY" &&
                           (m.Ticker?.Contains("26JAN") ?? false) &&
                           m.StrikePrice == targetStrike;
                }).ToList();

                if (monthlyYYMMM.Count > 0)
                {
                    Console.WriteLine($"      Found {monthlyYYMMM.Count} options with YYMMM format:");
                    foreach (var opt in monthlyYYMMM)
                    {
                        Console.WriteLine($"        Ticker: {opt.Ticker,-25} Expiry: {opt.ExpiryDate}");
                    }
                }
            }
            else
            {
                foreach (var opt in monthlyOptions)
                {
                    Console.WriteLine($"      Ticker: {opt.Ticker,-25}");
                }
            }

            // Show all NIFTY expiries in January 2026 to understand the pattern
            Console.WriteLine("\n    All NIFTY expiries in January 2026:");
            var niftyJanExpiries = _cachedMasters
                .Where(m => m.Underlying == "NIFTY" && (m.ExpiryDate?.StartsWith("2026-01") ?? false))
                .Select(m => m.ExpiryDate)
                .Distinct()
                .OrderBy(e => e)
                .ToList();

            foreach (var exp in niftyJanExpiries)
            {
                Console.WriteLine($"      {exp}");
            }

            // Show naming pattern analysis
            Console.WriteLine("\n    NAMING PATTERN ANALYSIS:");
            if (weeklyOptions.Count > 0)
            {
                var sample = weeklyOptions.First().Ticker;
                Console.WriteLine($"      Weekly format:  {sample}");
                Console.WriteLine($"                      NIFTY + YYMMDD + Strike + CE/PE");
            }
            if (monthlyOptions.Count > 0)
            {
                var sample = monthlyOptions.First().Ticker;
                Console.WriteLine($"      Monthly format: {sample}");
                Console.WriteLine($"                      NIFTY + YYMMDD + Strike + CE/PE (same pattern)");
            }
        }

        private static async Task CompareSensexExpiryPatterns(AccelpixApiClient client)
        {
            if (_cachedMasters == null) return;

            // SENSEX Weekly: 2026-01-15 (Thursday - weekly expiry for SENSEX is on BSE)
            // SENSEX Monthly: 2026-01-29 (Thursday - monthly expiry)
            var weeklyExpiry = "2026-01-15";
            var monthlyExpiry = "2026-01-29";
            decimal targetStrike = 85000;

            Console.WriteLine($"\n    Weekly Expiry: {weeklyExpiry}");
            Console.WriteLine($"    Monthly Expiry: {monthlyExpiry}");
            Console.WriteLine($"    Target Strike: {targetStrike}");

            // First, let's find what underlying names are used for SENSEX
            var sensexUnderlyings = _cachedMasters
                .Where(m => (m.Ticker?.Contains("SENSEX") ?? false) || (m.Underlying?.Contains("SENSEX") ?? false))
                .Select(m => m.Underlying)
                .Distinct()
                .Take(5)
                .ToList();

            Console.WriteLine($"\n    SENSEX underlying names found: {string.Join(", ", sensexUnderlyings)}");

            // Find weekly options at strike 85000
            var weeklyOptions = _cachedMasters.Where(m =>
            {
                return (m.Underlying == "SENSEX" || m.Underlying == "BSE SENSEX") &&
                       (m.ExpiryDate?.StartsWith(weeklyExpiry) ?? false) &&
                       m.StrikePrice == targetStrike;
            }).ToList();

            Console.WriteLine($"\n    WEEKLY ({weeklyExpiry}) options at strike {targetStrike}:");
            if (weeklyOptions.Count == 0)
            {
                Console.WriteLine("      No options found at exact strike. Searching nearby strikes...");

                // Find nearby strikes
                var nearbyWeekly = _cachedMasters.Where(m =>
                {
                    return (m.Underlying == "SENSEX" || m.Underlying == "BSE SENSEX" || m.Underlying.Contains("SENSEX")) &&
                           m.ExpiryDate.StartsWith(weeklyExpiry) &&
                           m.StrikePrice >= 84000 && m.StrikePrice <= 86000;
                }).OrderBy(m => m.StrikePrice).Take(10).ToList();

                foreach (var opt in nearbyWeekly)
                {
                    Console.WriteLine($"      Ticker: {opt.Ticker,-30} Strike: {opt.StrikePrice}");
                }
            }
            else
            {
                foreach (var opt in weeklyOptions)
                {
                    Console.WriteLine($"      Ticker: {opt.Ticker,-25}");
                }
            }

            // Find monthly options at strike 85000
            var monthlyOptions = _cachedMasters.Where(m =>
            {
                return (m.Underlying == "SENSEX" || m.Underlying == "BSE SENSEX") &&
                       m.ExpiryDate.StartsWith(monthlyExpiry) &&
                       m.StrikePrice == targetStrike;
            }).ToList();

            Console.WriteLine($"\n    MONTHLY ({monthlyExpiry}) options at strike {targetStrike}:");
            if (monthlyOptions.Count == 0)
            {
                Console.WriteLine("      No options found at exact strike. Searching nearby strikes...");

                var nearbyMonthly = _cachedMasters.Where(m =>
                {
                    return (m.Underlying == "SENSEX" || m.Underlying == "BSE SENSEX" || m.Underlying.Contains("SENSEX")) &&
                           m.ExpiryDate.StartsWith(monthlyExpiry) &&
                           m.StrikePrice >= 84000 && m.StrikePrice <= 86000;
                }).OrderBy(m => m.StrikePrice).Take(10).ToList();

                foreach (var opt in nearbyMonthly)
                {
                    Console.WriteLine($"      Ticker: {opt.Ticker,-30} Strike: {opt.StrikePrice}");
                }
            }
            else
            {
                foreach (var opt in monthlyOptions)
                {
                    Console.WriteLine($"      Ticker: {opt.Ticker,-25}");
                }
            }

            // Show all SENSEX expiries in January 2026
            Console.WriteLine("\n    All SENSEX expiries in January 2026:");
            var sensexJanExpiries = _cachedMasters
                .Where(m => (m.Underlying == "SENSEX" || m.Underlying.Contains("SENSEX")) &&
                           m.ExpiryDate.StartsWith("2026-01"))
                .Select(m => m.ExpiryDate)
                .Distinct()
                .OrderBy(e => e)
                .ToList();

            foreach (var exp in sensexJanExpiries)
            {
                Console.WriteLine($"      {exp}");
            }
        }

        private static async Task TestTickDataForSpecificOptions(AccelpixApiClient client)
        {
            var testDate = new DateTime(2026, 1, 8);
            Console.WriteLine($"    Test date: {testDate:yyyy-MM-dd}");

            // Test NIFTY 26000 CE weekly
            var niftyWeeklyTickers = new[] { "NIFTY2611326000CE", "NIFTY2611326000PE" };

            // Test NIFTY 26000 CE monthly - try both YYMMDD and YYMMM formats
            var niftyMonthlyYYMMDD = new[] { "NIFTY2612926000CE", "NIFTY2612926000PE" };
            var niftyMonthlyYYMMM = new[] { "NIFTY26JAN26000CE", "NIFTY26JAN26000PE" };

            Console.WriteLine("\n    NIFTY Weekly (2026-01-13) tick data - YYMMDD format:");
            foreach (var ticker in niftyWeeklyTickers)
            {
                await TestSingleTicker(client, ticker, testDate);
            }

            Console.WriteLine("\n    NIFTY Monthly (2026-01-29) tick data - YYMMDD format:");
            foreach (var ticker in niftyMonthlyYYMMDD)
            {
                await TestSingleTicker(client, ticker, testDate);
            }

            Console.WriteLine("\n    NIFTY Monthly (2026-01-29) tick data - YYMMM format (like SENSEX):");
            foreach (var ticker in niftyMonthlyYYMMM)
            {
                await TestSingleTicker(client, ticker, testDate);
            }

            // For SENSEX, we need to discover the ticker format first
            if (_cachedMasters != null)
            {
                // Find a SENSEX option ticker to test
                var sensexOption = _cachedMasters.FirstOrDefault(m =>
                {
                    return m.Underlying.Contains("SENSEX") &&
                           m.ExpiryDate.StartsWith("2026-01") &&
                           m.StrikePrice >= 84000 && m.StrikePrice <= 86000;
                });

                if (sensexOption != null)
                {
                    var sensexTicker = sensexOption.Ticker;
                    Console.WriteLine($"\n    SENSEX option tick data (sample: {sensexTicker}):");
                    await TestSingleTicker(client, sensexTicker, testDate);
                }
            }
        }

        private static async Task TestSingleTicker(AccelpixApiClient client, string ticker, DateTime testDate)
        {
            Console.Write($"      {ticker,-30} -> ");

            var ticks = await client.GetTickDataAsync(ticker, testDate);

            if (ticks != null && ticks.Count > 0)
            {
                Console.WriteLine($"{ticks.Count:N0} ticks");
            }
            else
            {
                Console.WriteLine("No data");
            }
        }
    }
}
