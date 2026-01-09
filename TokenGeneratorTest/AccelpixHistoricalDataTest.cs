using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

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
        private static List<JObject> _cachedMasters = null;

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

                using (var client = new AccelpixApiClient(apiKey))
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

            var masterJson = await client.GetMasterDataAsync(includeLotSize: true);

            if (string.IsNullOrEmpty(masterJson))
            {
                Console.WriteLine("    ERROR: No master data received");
                return;
            }

            Console.WriteLine($"    Received {masterJson.Length:N0} bytes");
            _cachedMasters = JsonConvert.DeserializeObject<List<JObject>>(masterJson);
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
                var underlying = m["utkr"]?.ToString() ?? "";
                var expiry = m["exp"]?.ToString() ?? "";
                var strike = m["sp"]?.Value<decimal>() ?? 0;

                return underlying == "NIFTY" &&
                       expiry.StartsWith(weeklyExpiry) &&
                       strike == targetStrike;
            }).ToList();

            Console.WriteLine($"\n    WEEKLY ({weeklyExpiry}) options at strike {targetStrike}:");
            foreach (var opt in weeklyOptions)
            {
                var ticker = opt["tkr"]?.ToString();
                var a3tkr = opt["a3tkr"]?.ToString();
                Console.WriteLine($"      Ticker: {ticker,-25} Alt: {a3tkr}");
            }

            // Find monthly options at strike 26000
            var monthlyOptions = _cachedMasters.Where(m =>
            {
                var underlying = m["utkr"]?.ToString() ?? "";
                var expiry = m["exp"]?.ToString() ?? "";
                var strike = m["sp"]?.Value<decimal>() ?? 0;

                return underlying == "NIFTY" &&
                       expiry.StartsWith(monthlyExpiry) &&
                       strike == targetStrike;
            }).ToList();

            Console.WriteLine($"\n    MONTHLY ({monthlyExpiry}) options at strike {targetStrike}:");
            if (monthlyOptions.Count == 0)
            {
                Console.WriteLine("      No options found with YYMMDD format. Checking YYMMM format...");

                // Search for NIFTY26JAN format
                var monthlyYYMMM = _cachedMasters.Where(m =>
                {
                    var ticker = m["tkr"]?.ToString() ?? "";
                    var underlying = m["utkr"]?.ToString() ?? "";
                    var strike = m["sp"]?.Value<decimal>() ?? 0;

                    return underlying == "NIFTY" &&
                           ticker.Contains("26JAN") &&
                           strike == targetStrike;
                }).ToList();

                if (monthlyYYMMM.Count > 0)
                {
                    Console.WriteLine($"      Found {monthlyYYMMM.Count} options with YYMMM format:");
                    foreach (var opt in monthlyYYMMM)
                    {
                        var ticker = opt["tkr"]?.ToString();
                        var a3tkr = opt["a3tkr"]?.ToString();
                        var expiry = opt["exp"]?.ToString();
                        Console.WriteLine($"        Ticker: {ticker,-25} Expiry: {expiry} Alt: {a3tkr}");
                    }
                }
            }
            else
            {
                foreach (var opt in monthlyOptions)
                {
                    var ticker = opt["tkr"]?.ToString();
                    var a3tkr = opt["a3tkr"]?.ToString();
                    Console.WriteLine($"      Ticker: {ticker,-25} Alt: {a3tkr}");
                }
            }

            // Show all NIFTY expiries in January 2026 to understand the pattern
            Console.WriteLine("\n    All NIFTY expiries in January 2026:");
            var niftyJanExpiries = _cachedMasters
                .Where(m =>
                {
                    var underlying = m["utkr"]?.ToString() ?? "";
                    var expiry = m["exp"]?.ToString() ?? "";
                    return underlying == "NIFTY" && expiry.StartsWith("2026-01");
                })
                .Select(m => m["exp"]?.ToString())
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
                var sample = weeklyOptions.First()["tkr"]?.ToString() ?? "";
                Console.WriteLine($"      Weekly format:  {sample}");
                Console.WriteLine($"                      NIFTY + YYMMDD + Strike + CE/PE");
            }
            if (monthlyOptions.Count > 0)
            {
                var sample = monthlyOptions.First()["tkr"]?.ToString() ?? "";
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
                .Where(m =>
                {
                    var ticker = m["tkr"]?.ToString() ?? "";
                    var underlying = m["utkr"]?.ToString() ?? "";
                    return ticker.Contains("SENSEX") || underlying.Contains("SENSEX");
                })
                .Select(m => m["utkr"]?.ToString())
                .Distinct()
                .Take(5)
                .ToList();

            Console.WriteLine($"\n    SENSEX underlying names found: {string.Join(", ", sensexUnderlyings)}");

            // Find weekly options at strike 85000
            var weeklyOptions = _cachedMasters.Where(m =>
            {
                var underlying = m["utkr"]?.ToString() ?? "";
                var expiry = m["exp"]?.ToString() ?? "";
                var strike = m["sp"]?.Value<decimal>() ?? 0;

                return (underlying == "SENSEX" || underlying == "BSE SENSEX") &&
                       expiry.StartsWith(weeklyExpiry) &&
                       strike == targetStrike;
            }).ToList();

            Console.WriteLine($"\n    WEEKLY ({weeklyExpiry}) options at strike {targetStrike}:");
            if (weeklyOptions.Count == 0)
            {
                Console.WriteLine("      No options found at exact strike. Searching nearby strikes...");

                // Find nearby strikes
                var nearbyWeekly = _cachedMasters.Where(m =>
                {
                    var underlying = m["utkr"]?.ToString() ?? "";
                    var expiry = m["exp"]?.ToString() ?? "";
                    var strike = m["sp"]?.Value<decimal>() ?? 0;

                    return (underlying == "SENSEX" || underlying == "BSE SENSEX" || underlying.Contains("SENSEX")) &&
                           expiry.StartsWith(weeklyExpiry) &&
                           strike >= 84000 && strike <= 86000;
                }).OrderBy(m => m["sp"]?.Value<decimal>() ?? 0).Take(10).ToList();

                foreach (var opt in nearbyWeekly)
                {
                    var ticker = opt["tkr"]?.ToString();
                    var strike = opt["sp"]?.Value<decimal>() ?? 0;
                    var a3tkr = opt["a3tkr"]?.ToString();
                    Console.WriteLine($"      Ticker: {ticker,-30} Strike: {strike} Alt: {a3tkr}");
                }
            }
            else
            {
                foreach (var opt in weeklyOptions)
                {
                    var ticker = opt["tkr"]?.ToString();
                    var a3tkr = opt["a3tkr"]?.ToString();
                    Console.WriteLine($"      Ticker: {ticker,-25} Alt: {a3tkr}");
                }
            }

            // Find monthly options at strike 85000
            var monthlyOptions = _cachedMasters.Where(m =>
            {
                var underlying = m["utkr"]?.ToString() ?? "";
                var expiry = m["exp"]?.ToString() ?? "";
                var strike = m["sp"]?.Value<decimal>() ?? 0;

                return (underlying == "SENSEX" || underlying == "BSE SENSEX") &&
                       expiry.StartsWith(monthlyExpiry) &&
                       strike == targetStrike;
            }).ToList();

            Console.WriteLine($"\n    MONTHLY ({monthlyExpiry}) options at strike {targetStrike}:");
            if (monthlyOptions.Count == 0)
            {
                Console.WriteLine("      No options found at exact strike. Searching nearby strikes...");

                var nearbyMonthly = _cachedMasters.Where(m =>
                {
                    var underlying = m["utkr"]?.ToString() ?? "";
                    var expiry = m["exp"]?.ToString() ?? "";
                    var strike = m["sp"]?.Value<decimal>() ?? 0;

                    return (underlying == "SENSEX" || underlying == "BSE SENSEX" || underlying.Contains("SENSEX")) &&
                           expiry.StartsWith(monthlyExpiry) &&
                           strike >= 84000 && strike <= 86000;
                }).OrderBy(m => m["sp"]?.Value<decimal>() ?? 0).Take(10).ToList();

                foreach (var opt in nearbyMonthly)
                {
                    var ticker = opt["tkr"]?.ToString();
                    var strike = opt["sp"]?.Value<decimal>() ?? 0;
                    var a3tkr = opt["a3tkr"]?.ToString();
                    Console.WriteLine($"      Ticker: {ticker,-30} Strike: {strike} Alt: {a3tkr}");
                }
            }
            else
            {
                foreach (var opt in monthlyOptions)
                {
                    var ticker = opt["tkr"]?.ToString();
                    var a3tkr = opt["a3tkr"]?.ToString();
                    Console.WriteLine($"      Ticker: {ticker,-25} Alt: {a3tkr}");
                }
            }

            // Show all SENSEX expiries in January 2026
            Console.WriteLine("\n    All SENSEX expiries in January 2026:");
            var sensexJanExpiries = _cachedMasters
                .Where(m =>
                {
                    var underlying = m["utkr"]?.ToString() ?? "";
                    var expiry = m["exp"]?.ToString() ?? "";
                    return (underlying == "SENSEX" || underlying.Contains("SENSEX")) &&
                           expiry.StartsWith("2026-01");
                })
                .Select(m => m["exp"]?.ToString())
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
                    var underlying = m["utkr"]?.ToString() ?? "";
                    var expiry = m["exp"]?.ToString() ?? "";
                    var strike = m["sp"]?.Value<decimal>() ?? 0;

                    return underlying.Contains("SENSEX") &&
                           expiry.StartsWith("2026-01") &&
                           strike >= 84000 && strike <= 86000;
                });

                if (sensexOption != null)
                {
                    var sensexTicker = sensexOption["tkr"]?.ToString();
                    Console.WriteLine($"\n    SENSEX option tick data (sample: {sensexTicker}):");
                    await TestSingleTicker(client, sensexTicker, testDate);
                }
            }
        }

        private static async Task TestSingleTicker(AccelpixApiClient client, string ticker, DateTime testDate)
        {
            Console.Write($"      {ticker,-30} -> ");

            var rawData = await client.GetTickDataRawAsync(ticker, testDate);

            if (!string.IsNullOrEmpty(rawData) && rawData != "[]")
            {
                try
                {
                    var ticks = JsonConvert.DeserializeObject<List<JObject>>(rawData);
                    if (ticks != null && ticks.Count > 0)
                    {
                        Console.WriteLine($"{ticks.Count:N0} ticks ({rawData.Length:N0} bytes)");
                    }
                    else
                    {
                        Console.WriteLine("Empty response");
                    }
                }
                catch
                {
                    Console.WriteLine($"{rawData.Length:N0} bytes (parse error)");
                }
            }
            else
            {
                Console.WriteLine("No data");
            }
        }
    }
}
