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

                // Step 4: Test date range bug (today's data with same vs next day to_date)
                // await TestTodayDateRangeBug(client);

                // Console.WriteLine();

                // Step 4b: Test Jan 7th data retrieval
                await TestJan7thDataRetrieval(client);

                Console.WriteLine();

                // Step 5: Test historical data fetch
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

        /// <summary>
        /// Test for date range bug: when fetching data for a specific date, does the API require
        /// to_date to be the next day instead of same day?
        /// Testing with SENSEX (BSESEN) options as specified.
        /// </summary>
        private static async Task TestTodayDateRangeBug(BreezeApiClient client)
        {
            Console.WriteLine("[4] Testing DATE RANGE BUG (SENSEX options - same vs next day to_date)...");
            Console.WriteLine();

            // Use exact parameters specified by user:
            // SENSEX options: underlying=BSESEN, strike=84500, optionType=CE, expiry=2025-01-08
            string symbol = "BSESEN";           // SENSEX underlying for ICICI API
            int strikePrice = 84500;            // Strike price
            string optionType = "CE";           // Call option
            DateTime expiry = new DateTime(2025, 1, 8);  // 8th January 2025 expiry
            DateTime targetDate = new DateTime(2025, 1, 8);  // Same date as expiry

            Console.WriteLine($"    Symbol: {symbol} (SENSEX)");
            Console.WriteLine($"    Strike: {strikePrice}");
            Console.WriteLine($"    Option Type: {optionType}");
            Console.WriteLine($"    Expiry: {expiry:yyyy-MM-dd}");
            Console.WriteLine($"    Target Date: {targetDate:yyyy-MM-dd}");
            Console.WriteLine();

            // ========================================
            // TEST 1: Same day from/to (from=2025-01-08, to=2025-01-08)
            // ========================================
            Console.WriteLine("    TEST 1: Same-day from/to dates (2025-01-08 to 2025-01-08)");
            Console.WriteLine("    ---------------------------------------------------------");

            DateTime fromDate1 = new DateTime(2025, 1, 8, 9, 15, 0);   // 9:15 AM
            DateTime toDate1 = new DateTime(2025, 1, 8, 9, 20, 0);     // 9:20 AM same day

            Console.WriteLine($"    From: {fromDate1:yyyy-MM-dd HH:mm:ss}");
            Console.WriteLine($"    To:   {toDate1:yyyy-MM-dd HH:mm:ss}");

            var response1 = await client.GetOptionsHistoricalDataAsync(
                symbol, strikePrice, optionType, expiry,
                fromDate1, toDate1, "1second", "BFO");  // BFO exchange for SENSEX options

            if (response1.Success)
            {
                Console.WriteLine($"    RESULT: SUCCESS - Got {response1.Data.Count} records");
                if (response1.Data.Count > 0)
                {
                    Console.WriteLine($"    First: {response1.Data[0]}");
                    Console.WriteLine($"    Last:  {response1.Data[response1.Data.Count - 1]}");
                }
            }
            else
            {
                Console.WriteLine($"    RESULT: FAILED - {response1.Error}");
            }
            Console.WriteLine();

            // ========================================
            // TEST 2: To date as next day (from=2025-01-08, to=2025-01-09)
            // ========================================
            Console.WriteLine("    TEST 2: To date as next day (2025-01-08 to 2025-01-09)");
            Console.WriteLine("    ------------------------------------------------------");

            DateTime fromDate2 = new DateTime(2025, 1, 8, 9, 15, 0);   // 9:15 AM
            DateTime toDate2 = new DateTime(2025, 1, 9, 9, 20, 0);     // 9:20 AM NEXT day

            Console.WriteLine($"    From: {fromDate2:yyyy-MM-dd HH:mm:ss}");
            Console.WriteLine($"    To:   {toDate2:yyyy-MM-dd HH:mm:ss}");

            var response2 = await client.GetOptionsHistoricalDataAsync(
                symbol, strikePrice, optionType, expiry,
                fromDate2, toDate2, "1second", "BFO");  // BFO exchange for SENSEX options

            if (response2.Success)
            {
                Console.WriteLine($"    RESULT: SUCCESS - Got {response2.Data.Count} records");
                if (response2.Data.Count > 0)
                {
                    Console.WriteLine($"    First: {response2.Data[0]}");
                    Console.WriteLine($"    Last:  {response2.Data[response2.Data.Count - 1]}");
                }
            }
            else
            {
                Console.WriteLine($"    RESULT: FAILED - {response2.Error}");
            }
            Console.WriteLine();

            // ========================================
            // TEST 3: Prior day reference (2025-01-07) - should work if data exists
            // ========================================
            Console.WriteLine("    TEST 3: Prior day reference (2025-01-07 same day to_date)");
            Console.WriteLine("    ---------------------------------------------------------");

            DateTime fromDate3 = new DateTime(2025, 1, 7, 9, 15, 0);   // 9:15 AM
            DateTime toDate3 = new DateTime(2025, 1, 7, 9, 20, 0);     // 9:20 AM same day

            Console.WriteLine($"    From: {fromDate3:yyyy-MM-dd HH:mm:ss}");
            Console.WriteLine($"    To:   {toDate3:yyyy-MM-dd HH:mm:ss}");

            var response3 = await client.GetOptionsHistoricalDataAsync(
                symbol, strikePrice, optionType, expiry,
                fromDate3, toDate3, "1second", "BFO");  // BFO exchange for SENSEX options

            if (response3.Success)
            {
                Console.WriteLine($"    RESULT: SUCCESS - Got {response3.Data.Count} records");
                if (response3.Data.Count > 0)
                {
                    Console.WriteLine($"    First: {response3.Data[0]}");
                    Console.WriteLine($"    Last:  {response3.Data[response3.Data.Count - 1]}");
                }
            }
            else
            {
                Console.WriteLine($"    RESULT: FAILED - {response3.Error}");
            }
            Console.WriteLine();

            // ========================================
            // SUMMARY
            // ========================================
            Console.WriteLine("    ========================================");
            Console.WriteLine("    SUMMARY:");
            Console.WriteLine("    ========================================");
            Console.WriteLine($"    Test 1 (same-day to_date 01-08):  {(response1.Success ? $"SUCCESS ({response1.Data.Count} records)" : $"FAILED: {response1.Error}")}");
            Console.WriteLine($"    Test 2 (next-day to_date 01-09):  {(response2.Success ? $"SUCCESS ({response2.Data.Count} records)" : $"FAILED: {response2.Error}")}");
            Console.WriteLine($"    Test 3 (prior-day 01-07 ref):     {(response3.Success ? $"SUCCESS ({response3.Data.Count} records)" : $"FAILED: {response3.Error}")}");
            Console.WriteLine();

            if (!response1.Success && response2.Success)
            {
                Console.WriteLine("    ** BUG CONFIRMED: Same-day to_date fails, but next-day to_date works! **");
                Console.WriteLine("    ** FIX: Use to_date = next day when fetching data for a specific date **");
            }
            else if (response1.Success && response2.Success)
            {
                Console.WriteLine("    Both approaches work - no date range bug detected.");
            }
            else if (!response1.Success && !response2.Success)
            {
                Console.WriteLine("    Both approaches failed - may be data not available or other issue.");
            }
            else if (response1.Success && !response2.Success)
            {
                Console.WriteLine("    Same-day works but next-day fails - unexpected behavior.");
            }
        }

        private static async Task TestOptionsDataFetch(BreezeApiClient client)
        {
            Console.WriteLine("[5] Testing OPTIONS data fetch (1-second interval)...");
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
            Console.WriteLine("[6] Testing FUTURES data fetch (1-minute interval)...");
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
        /// Test Jan 7th data retrieval for SENSEX options with Jan 8th expiry.
        /// This verifies we can get prior day data from ICICI API.
        /// </summary>
        private static async Task TestJan7thDataRetrieval(BreezeApiClient client)
        {
            Console.WriteLine("[4] Testing JAN 7th DATA RETRIEVAL (SENSEX options, Jan 8th expiry)...");
            Console.WriteLine();

            // SENSEX options: symbol=SENSEX (maps to BSESEN), expiry=Jan 8, 2026
            // Testing if we can retrieve data for Jan 7th (prior day)
            string symbol = "SENSEX";           // Will map to BSESEN stock_code
            int strikePrice = 78500;            // ATM-ish strike for SENSEX
            string optionType = "CE";           // Call option
            DateTime expiry = new DateTime(2026, 1, 8);  // Jan 8th 2026 expiry

            Console.WriteLine($"    Symbol: {symbol} (stock_code: BSESEN)");
            Console.WriteLine($"    Strike: {strikePrice}");
            Console.WriteLine($"    Option Type: {optionType}");
            Console.WriteLine($"    Expiry: {expiry:yyyy-MM-dd}");
            Console.WriteLine($"    Exchange: BFO");
            Console.WriteLine();

            // ========================================
            // TEST 1: Jan 7th data (prior day) with same-day to_date
            // ========================================
            Console.WriteLine("    TEST 1: Jan 7th data (same-day from/to)");
            Console.WriteLine("    --------------------------------------");

            DateTime fromDate1 = new DateTime(2026, 1, 7, 9, 15, 0);   // 9:15 AM Jan 7
            DateTime toDate1 = new DateTime(2026, 1, 7, 9, 20, 0);     // 9:20 AM Jan 7

            Console.WriteLine($"    From: {fromDate1:yyyy-MM-dd HH:mm:ss}");
            Console.WriteLine($"    To:   {toDate1:yyyy-MM-dd HH:mm:ss}");

            var response1 = await client.GetOptionsHistoricalDataAsync(
                symbol, strikePrice, optionType, expiry,
                fromDate1, toDate1, "1second", "BFO");

            if (response1.Success)
            {
                Console.WriteLine($"    RESULT: SUCCESS - Got {response1.Data.Count} records");
                if (response1.Data.Count > 0)
                {
                    Console.WriteLine($"    First: {response1.Data[0]}");
                    Console.WriteLine($"    Last:  {response1.Data[response1.Data.Count - 1]}");
                }
            }
            else
            {
                Console.WriteLine($"    RESULT: FAILED - {response1.Error}");
            }
            Console.WriteLine();

            // ========================================
            // TEST 2: Jan 7th data with next-day to_date (Jan 8)
            // ========================================
            Console.WriteLine("    TEST 2: Jan 7th data (to_date = Jan 8th)");
            Console.WriteLine("    ----------------------------------------");

            DateTime fromDate2 = new DateTime(2026, 1, 7, 9, 15, 0);   // 9:15 AM Jan 7
            DateTime toDate2 = new DateTime(2026, 1, 8, 9, 20, 0);     // 9:20 AM Jan 8

            Console.WriteLine($"    From: {fromDate2:yyyy-MM-dd HH:mm:ss}");
            Console.WriteLine($"    To:   {toDate2:yyyy-MM-dd HH:mm:ss}");

            var response2 = await client.GetOptionsHistoricalDataAsync(
                symbol, strikePrice, optionType, expiry,
                fromDate2, toDate2, "1second", "BFO");

            if (response2.Success)
            {
                Console.WriteLine($"    RESULT: SUCCESS - Got {response2.Data.Count} records");
                if (response2.Data.Count > 0)
                {
                    Console.WriteLine($"    First: {response2.Data[0]}");
                    Console.WriteLine($"    Last:  {response2.Data[response2.Data.Count - 1]}");
                }
            }
            else
            {
                Console.WriteLine($"    RESULT: FAILED - {response2.Error}");
            }
            Console.WriteLine();

            // ========================================
            // TEST 3: Jan 8th data (expiry day) with same-day to_date
            // ========================================
            Console.WriteLine("    TEST 3: Jan 8th data (expiry day, same-day to_date)");
            Console.WriteLine("    ---------------------------------------------------");

            DateTime fromDate3 = new DateTime(2026, 1, 8, 9, 15, 0);   // 9:15 AM Jan 8
            DateTime toDate3 = new DateTime(2026, 1, 8, 9, 20, 0);     // 9:20 AM Jan 8

            Console.WriteLine($"    From: {fromDate3:yyyy-MM-dd HH:mm:ss}");
            Console.WriteLine($"    To:   {toDate3:yyyy-MM-dd HH:mm:ss}");

            var response3 = await client.GetOptionsHistoricalDataAsync(
                symbol, strikePrice, optionType, expiry,
                fromDate3, toDate3, "1second", "BFO");

            if (response3.Success)
            {
                Console.WriteLine($"    RESULT: SUCCESS - Got {response3.Data.Count} records");
                if (response3.Data.Count > 0)
                {
                    Console.WriteLine($"    First: {response3.Data[0]}");
                    Console.WriteLine($"    Last:  {response3.Data[response3.Data.Count - 1]}");
                }
            }
            else
            {
                Console.WriteLine($"    RESULT: FAILED - {response3.Error}");
            }
            Console.WriteLine();

            // ========================================
            // SUMMARY
            // ========================================
            Console.WriteLine("    ========================================");
            Console.WriteLine("    SUMMARY:");
            Console.WriteLine("    ========================================");
            Console.WriteLine($"    Test 1 (Jan 7 same-day to):  {(response1.Success ? $"SUCCESS ({response1.Data.Count} records)" : $"FAILED: {response1.Error}")}");
            Console.WriteLine($"    Test 2 (Jan 7 next-day to):  {(response2.Success ? $"SUCCESS ({response2.Data.Count} records)" : $"FAILED: {response2.Error}")}");
            Console.WriteLine($"    Test 3 (Jan 8 same-day to):  {(response3.Success ? $"SUCCESS ({response3.Data.Count} records)" : $"FAILED: {response3.Error}")}");
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
