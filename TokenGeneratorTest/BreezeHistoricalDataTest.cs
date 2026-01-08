using System;
using System.IO;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace TokenGeneratorTest
{
    /// <summary>
    /// Test application for ICICI Breeze Historical Data v2 API
    /// Fetches 1-second granularity data for futures and options
    /// </summary>
    public class BreezeHistoricalDataTest
    {
        // Config file path for Breeze credentials
        private static readonly string BREEZE_CONFIG_PATH = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
            "NinjaTrader 8", "ZerodhaAdapter", "breeze_config.json");

        /// <summary>
        /// Run the Breeze historical data test
        /// </summary>
        public static async Task RunTestAsync()
        {
            Console.WriteLine("===========================================");
            Console.WriteLine("  ICICI Breeze Historical Data v2 Test");
            Console.WriteLine("  (1-Second Granularity Support)");
            Console.WriteLine("===========================================");
            Console.WriteLine();

            try
            {
                // Step 1: Load configuration
                Console.WriteLine("[1] Loading Breeze configuration...");
                var config = LoadBreezeConfig();
                if (config == null)
                {
                    Console.WriteLine("    ERROR: Could not load Breeze configuration.");
                    Console.WriteLine($"    Expected config file at: {BREEZE_CONFIG_PATH}");
                    CreateSampleConfig();
                    return;
                }

                string apiKey = config["apiKey"]?.ToString();
                string apiSecret = config["apiSecret"]?.ToString();
                string sessionKey = config["sessionKey"]?.ToString();

                if (string.IsNullOrEmpty(apiKey) || string.IsNullOrEmpty(apiSecret))
                {
                    Console.WriteLine("    ERROR: Missing apiKey or apiSecret in config");
                    return;
                }

                Console.WriteLine($"    API Key: {apiKey.Substring(0, Math.Min(8, apiKey.Length))}...");
                Console.WriteLine($"    Session Key Present: {!string.IsNullOrEmpty(sessionKey)}");
                Console.WriteLine();

                // Step 2: Initialize API client
                Console.WriteLine("[2] Initializing Breeze API client...");
                var client = new BreezeApiClient(apiKey, apiSecret);

                // Step 3: Generate/Set session
                Console.WriteLine("[3] Setting up session...");

                if (!string.IsNullOrEmpty(sessionKey))
                {
                    // Try to generate session with existing session key
                    bool success = await client.GenerateSessionAsync(sessionKey);
                    if (!success)
                    {
                        Console.WriteLine("    Session generation failed. The session key may have expired.");
                        Console.WriteLine("    Please update the sessionKey in breeze_config.json");
                        Console.WriteLine("    You can get a new session key from your Python app or browser login.");
                        return;
                    }
                }
                else
                {
                    Console.WriteLine("    ERROR: No sessionKey found in config.");
                    Console.WriteLine("    Please add your session key to breeze_config.json");
                    Console.WriteLine("    You can get this from your Python app's config/icici_config.json");
                    return;
                }
                Console.WriteLine();

                // Step 4: Test historical data fetch
                await TestOptionsDataFetch(client);

                Console.WriteLine();
                await TestFuturesDataFetch(client);

                Console.WriteLine();
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

        private static async Task TestOptionsDataFetch(BreezeApiClient client)
        {
            Console.WriteLine("[4] Testing OPTIONS data fetch (1-second interval)...");
            Console.WriteLine();

            // Test parameters - NIFTY 26000 CE for 6th Jan 2026 (as specified)
            string symbol = "NIFTY";        // Maps to "NIFTY" stock code
            int strikePrice = 26000;        // 26000 strike (as specified)
            string optionType = "CE";       // Call option

            // 6th January 2026 expiry (as specified)
            DateTime expiry = new DateTime(2026, 1, 6);

            // Fetch data for 6th January 2026 - market hours (IST)
            // Using a small window (5 minutes = 300 seconds)
            DateTime fromDate = new DateTime(2026, 1, 6, 9, 15, 0);  // 9:15 AM IST
            DateTime toDate = new DateTime(2026, 1, 6, 9, 20, 0);    // 9:20 AM IST (5 min)

            Console.WriteLine($"    Symbol: {symbol} (stock_code: NIFTY)");
            Console.WriteLine($"    Strike: {strikePrice}");
            Console.WriteLine($"    Option Type: {optionType} (right: call)");
            Console.WriteLine($"    Expiry: {expiry:yyyy-MM-dd}");
            Console.WriteLine($"    Exchange: NFO");
            Console.WriteLine($"    From: {fromDate:yyyy-MM-dd HH:mm:ss}");
            Console.WriteLine($"    To: {toDate:yyyy-MM-dd HH:mm:ss}");
            Console.WriteLine();

            var response = await client.GetOptionsHistoricalDataAsync(
                symbol, strikePrice, optionType, expiry,
                fromDate, toDate, "1second");

            if (response.Success)
            {
                Console.WriteLine($"    SUCCESS! Got {response.Data.Count} records");
                Console.WriteLine();

                if (response.Data.Count > 0)
                {
                    Console.WriteLine("    Sample data (first 10 records):");
                    Console.WriteLine("    " + new string('-', 70));

                    int count = 0;
                    foreach (var candle in response.Data)
                    {
                        Console.WriteLine($"    {candle}");
                        if (++count >= 10) break;
                    }

                    if (response.Data.Count > 10)
                    {
                        Console.WriteLine($"    ... and {response.Data.Count - 10} more records");
                    }
                }
            }
            else
            {
                Console.WriteLine($"    FAILED: {response.Error}");
            }
        }

        private static async Task TestFuturesDataFetch(BreezeApiClient client)
        {
            Console.WriteLine("[5] Testing FUTURES data fetch (1-minute interval)...");
            Console.WriteLine();

            string symbol = "NIFTY";
            DateTime expiry = GetNearestMonthEndExpiry();

            DateTime fromDate = DateTime.Today.AddDays(-5);
            DateTime toDate = DateTime.Today;

            Console.WriteLine($"    Symbol: {symbol}");
            Console.WriteLine($"    Expiry: {expiry:yyyy-MM-dd}");
            Console.WriteLine($"    From: {fromDate:yyyy-MM-dd}");
            Console.WriteLine($"    To: {toDate:yyyy-MM-dd}");
            Console.WriteLine();

            var response = await client.GetFuturesHistoricalDataAsync(
                symbol, expiry, fromDate, toDate, "1minute");

            if (response.Success)
            {
                Console.WriteLine($"    SUCCESS! Got {response.Data.Count} records");
                Console.WriteLine();

                if (response.Data.Count > 0)
                {
                    Console.WriteLine("    Sample data (first 5 records):");
                    Console.WriteLine("    " + new string('-', 70));

                    int count = 0;
                    foreach (var candle in response.Data)
                    {
                        Console.WriteLine($"    {candle}");
                        if (++count >= 5) break;
                    }
                }
            }
            else
            {
                Console.WriteLine($"    FAILED: {response.Error}");
            }
        }

        private static JObject LoadBreezeConfig()
        {
            try
            {
                // Use Python project config directly (has valid session)
                string pythonConfigPath = @"D:\NinjaSignalBacktest\config\icici_config.json";
                if (File.Exists(pythonConfigPath))
                {
                    Console.WriteLine($"    Using Python config: {pythonConfigPath}");
                    string json = File.ReadAllText(pythonConfigPath);
                    return JObject.Parse(json);
                }

                // Fall back to dedicated breeze config
                if (File.Exists(BREEZE_CONFIG_PATH))
                {
                    string json = File.ReadAllText(BREEZE_CONFIG_PATH);
                    return JObject.Parse(json);
                }

                return null;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"    Error loading config: {ex.Message}");
                return null;
            }
        }

        private static void CreateSampleConfig()
        {
            Console.WriteLine();
            Console.WriteLine("Creating sample config file...");

            var sampleConfig = new JObject
            {
                ["apiKey"] = "YOUR_API_KEY_HERE",
                ["apiSecret"] = "YOUR_API_SECRET_HERE",
                ["sessionKey"] = "YOUR_SESSION_KEY_FROM_PYTHON_APP",
                ["totpKey"] = "YOUR_TOTP_SECRET (optional)",
                ["login"] = "YOUR_LOGIN_ID (optional)",
                ["password"] = "YOUR_PASSWORD (optional)"
            };

            try
            {
                string directory = Path.GetDirectoryName(BREEZE_CONFIG_PATH);
                if (!Directory.Exists(directory))
                    Directory.CreateDirectory(directory);

                File.WriteAllText(BREEZE_CONFIG_PATH, sampleConfig.ToString(Formatting.Indented));
                Console.WriteLine($"    Sample config created at: {BREEZE_CONFIG_PATH}");
                Console.WriteLine("    Please edit this file with your ICICI Breeze API credentials.");
                Console.WriteLine();
                Console.WriteLine("    You can copy the sessionKey from your Python app's config:");
                Console.WriteLine(@"    D:\NinjaSignalBacktest\config\icici_config.json");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"    Error creating sample config: {ex.Message}");
            }
        }

        /// <summary>
        /// Get nearest weekly expiry (Thursday)
        /// </summary>
        private static DateTime GetNearestExpiry()
        {
            var today = DateTime.Today;

            // Find next Thursday
            int daysUntilThursday = ((int)DayOfWeek.Thursday - (int)today.DayOfWeek + 7) % 7;
            if (daysUntilThursday == 0 && DateTime.Now.Hour >= 15) // After market close
                daysUntilThursday = 7;

            return today.AddDays(daysUntilThursday);
        }

        /// <summary>
        /// Get nearest month-end expiry (last Thursday of month)
        /// </summary>
        private static DateTime GetNearestMonthEndExpiry()
        {
            var today = DateTime.Today;

            // Find last Thursday of current month
            var lastDayOfMonth = new DateTime(today.Year, today.Month, DateTime.DaysInMonth(today.Year, today.Month));
            int daysUntilThursday = ((int)lastDayOfMonth.DayOfWeek - (int)DayOfWeek.Thursday + 7) % 7;
            var lastThursday = lastDayOfMonth.AddDays(-daysUntilThursday);

            // If already past, get next month's
            if (lastThursday <= today)
            {
                var nextMonth = today.AddMonths(1);
                lastDayOfMonth = new DateTime(nextMonth.Year, nextMonth.Month, DateTime.DaysInMonth(nextMonth.Year, nextMonth.Month));
                daysUntilThursday = ((int)lastDayOfMonth.DayOfWeek - (int)DayOfWeek.Thursday + 7) % 7;
                lastThursday = lastDayOfMonth.AddDays(-daysUntilThursday);
            }

            return lastThursday;
        }
    }
}
