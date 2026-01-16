using System;
using System.IO;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using ZerodhaDatafeedAdapter.Services.Auth;

using ZerodhaDatafeedAdapter.Helpers;
using ZerodhaDatafeedAdapter.Services.Historical;

namespace TokenGeneratorTest
{
    /// <summary>
    /// Standalone test application for Zerodha Token Generator and ICICI Breeze API
    /// Tests the automated OAuth flow before integrating into NinjaTrader
    /// Also supports downloading instruments and testing Breeze historical data
    /// </summary>
    class Program
    {
        // Config file path (same as NinjaTrader adapter uses)
        private static readonly string CONFIG_PATH = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
            "NinjaTrader 8", "ZerodhaAdapter", "config.json");

        // Instruments file path
        private static readonly string INSTRUMENTS_PATH = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
            "NinjaTrader 8", "ZerodhaAdapter", "mapped_instruments.json");

        // SQLite database path
        private static readonly string DB_PATH = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
            "NinjaTrader 8", "ZerodhaAdapter", "InstrumentMasters.db");

        static async Task Main(string[] args)
        {
            // Check for Breeze test mode
            if (args.Length > 0 && args[0].ToLower() == "breeze")
            {
                await BreezeHistoricalDataTest.RunTestAsync();
                WaitForExit();
                return;
            }

            Console.WriteLine("===========================================");
            Console.WriteLine("  Token Generator & API Test Application");
            Console.WriteLine("===========================================");
            Console.WriteLine();
            Console.WriteLine("Select mode:");
            Console.WriteLine("  1. Zerodha Token Generator & Instruments");
            Console.WriteLine("  2. ICICI Breeze Historical Data Test");
            Console.WriteLine("  3. ICICI Breeze Token Generation Test (HTTP-based)");
            Console.WriteLine("  4. Accelpix Historical Data Test");
            Console.WriteLine();
            Console.Write("Enter choice (1, 2, 3, or 4): ");

            string choice = Console.ReadLine()?.Trim();

            if (choice == "2")
            {
                await BreezeHistoricalDataTest.RunTestAsync();
                WaitForExit();
                return;
            }

            if (choice == "3")
            {
                await RunBreezeTokenGenerationTest();
                WaitForExit();
                return;
            }

            if (choice == "4")
            {
                await AccelpixHistoricalDataTest.RunTestAsync();
                WaitForExit();
                return;
            }

            // Default: Zerodha mode
            Console.WriteLine();
            Console.WriteLine("===========================================");
            Console.WriteLine("  Zerodha Token Generator & Instrument Downloader");
            Console.WriteLine("===========================================");
            Console.WriteLine();

            try
            {
                // Step 1: Load configuration
                Console.WriteLine($"[1] Loading configuration from: {CONFIG_PATH}");
                var config = LoadConfiguration();
                if (config == null)
                {
                    Console.WriteLine("Failed to load configuration. Exiting.");
                    return;
                }
                Console.WriteLine("    Configuration loaded successfully.");
                Console.WriteLine();

                // Step 2: Extract Zerodha credentials
                Console.WriteLine("[2] Extracting Zerodha credentials...");
                var zerodhaConfig = config["Zerodha"] as JObject;
                if (zerodhaConfig == null)
                {
                    Console.WriteLine("    ERROR: No Zerodha configuration found in config.json");
                    return;
                }

                string apiKey = zerodhaConfig["Api"]?.ToString();
                string apiSecret = zerodhaConfig["Secret"]?.ToString();
                string userId = zerodhaConfig["UserId"]?.ToString();
                string password = zerodhaConfig["Password"]?.ToString();
                string totpSecret = zerodhaConfig["TotpSecret"]?.ToString();
                string redirectUrl = zerodhaConfig["RedirectUrl"]?.ToString() ?? "http://127.0.0.1:8001/callback";

                // Validate credentials
                if (string.IsNullOrEmpty(apiKey) || string.IsNullOrEmpty(apiSecret) ||
                    string.IsNullOrEmpty(userId) || string.IsNullOrEmpty(password) ||
                    string.IsNullOrEmpty(totpSecret))
                {
                    Console.WriteLine("    ERROR: Missing required credentials in config.json");
                    Console.WriteLine($"    ApiKey: {(string.IsNullOrEmpty(apiKey) ? "MISSING" : "OK")}");
                    Console.WriteLine($"    ApiSecret: {(string.IsNullOrEmpty(apiSecret) ? "MISSING" : "OK")}");
                    Console.WriteLine($"    UserId: {(string.IsNullOrEmpty(userId) ? "MISSING" : "OK")}");
                    Console.WriteLine($"    Password: {(string.IsNullOrEmpty(password) ? "MISSING" : "OK")}");
                    Console.WriteLine($"    TotpSecret: {(string.IsNullOrEmpty(totpSecret) ? "MISSING" : "OK")}");
                    return;
                }

                Console.WriteLine($"    UserId: {userId}");
                Console.WriteLine($"    ApiKey: {apiKey.Substring(0, 4)}...{apiKey.Substring(apiKey.Length - 4)}");
                Console.WriteLine($"    RedirectUrl: {redirectUrl}");
                Console.WriteLine();

                // Step 3: Check if existing token is valid
                Console.WriteLine("[3] Checking existing token...");
                string existingToken = zerodhaConfig["AccessToken"]?.ToString();
                string tokenGeneratedAt = zerodhaConfig["AccessTokenGeneratedAt"]?.ToString();
                string accessToken = existingToken;
                bool tokenWasGenerated = false;

                bool tokenIsValid = false;
                if (!string.IsNullOrEmpty(existingToken) && !string.IsNullOrEmpty(tokenGeneratedAt))
                {
                    if (DateTime.TryParse(tokenGeneratedAt, out DateTime generatedAt))
                    {
                        if (!ZerodhaTokenGenerator.IsTokenExpired(generatedAt))
                        {
                            Console.WriteLine($"    Existing token is still valid (generated: {tokenGeneratedAt})");
                            Console.WriteLine($"    Token: {existingToken.Substring(0, Math.Min(10, existingToken.Length))}...");
                            tokenIsValid = true;
                        }
                        else
                        {
                            Console.WriteLine($"    Existing token has expired (was generated: {tokenGeneratedAt})");
                        }
                    }
                }
                else
                {
                    Console.WriteLine("    No existing token found.");
                }
                Console.WriteLine();

                // Step 4: Generate new token if needed
                if (!tokenIsValid)
                {
                    Console.WriteLine("[4] Initializing token generator...");
                    var generator = new ZerodhaTokenGenerator(
                        apiKey, apiSecret, userId, password, totpSecret, redirectUrl);

                    // Subscribe to status updates
                    generator.StatusChanged += (sender, e) =>
                    {
                        var timestamp = e.Timestamp.ToString("HH:mm:ss.fff");
                        if (e.IsError)
                            Console.WriteLine($"    [{timestamp}] ERROR: {e.Message}");
                        else
                            Console.WriteLine($"    [{timestamp}] {e.Message}");
                    };
                    Console.WriteLine();

                    Console.WriteLine("[5] Starting token generation...");
                    Console.WriteLine("    (This will perform automated login using TOTP)");
                    Console.WriteLine();

                    var tokenData = await generator.GenerateTokenAsync();

                    Console.WriteLine();
                    Console.WriteLine("===========================================");
                    Console.WriteLine("  TOKEN GENERATION SUCCESSFUL!");
                    Console.WriteLine("===========================================");
                    Console.WriteLine($"  User ID:      {tokenData.UserId}");
                    Console.WriteLine($"  User Name:    {tokenData.UserName}");
                    Console.WriteLine($"  Email:        {tokenData.Email}");
                    Console.WriteLine($"  Broker:       {tokenData.Broker}");
                    Console.WriteLine($"  Generated At: {tokenData.GeneratedAt}");
                    Console.WriteLine($"  Access Token: {tokenData.AccessToken.Substring(0, 15)}...{tokenData.AccessToken.Substring(tokenData.AccessToken.Length - 5)}");
                    Console.WriteLine();

                    // Save token to config
                    Console.WriteLine("[6] Saving token to configuration...");
                    SaveTokenToConfig(config, tokenData);
                    Console.WriteLine("    Token saved successfully to config.json");
                    Console.WriteLine();

                    accessToken = tokenData.AccessToken;
                    tokenWasGenerated = true;
                }
                else
                {
                    Console.WriteLine("[4-6] Skipping token generation - existing token is valid.");
                    Console.WriteLine();
                }

                // Step 7: Check if instrument download is needed
                Console.WriteLine("[7] Checking if instrument download is needed...");
                bool needsDownload = ShouldDownloadInstruments(config);

                if (needsDownload)
                {
                    Console.WriteLine("    Instrument download required.");
                    Console.WriteLine();
                    Console.WriteLine("[8] Downloading instruments from Zerodha API...");
                    await DownloadInstruments(config, apiKey, accessToken);
                    Console.WriteLine();
                }
                else
                {
                    Console.WriteLine("    Instruments are up to date. Skipping download.");
                    Console.WriteLine();
                }

                Console.WriteLine("===========================================");
                Console.WriteLine("  Completed successfully!");
                Console.WriteLine("===========================================");
            }
            catch (Exception ex)
            {
                Console.WriteLine();
                Console.WriteLine("===========================================");
                Console.WriteLine("  OPERATION FAILED!");
                Console.WriteLine("===========================================");
                Console.WriteLine($"  Error: {ex.Message}");
                Console.WriteLine();
                Console.WriteLine("Stack Trace:");
                Console.WriteLine(ex.StackTrace);
            }

            WaitForExit();
        }

        private static void WaitForExit()
        {
            Console.WriteLine();
            Console.WriteLine("Press Enter to exit...");
            try
            {
                Console.ReadLine();
            }
            catch
            {
                // Ignore if console input is not available (non-interactive mode)
            }
        }

        /// <summary>
        /// Checks if instrument download is needed based on last download date in config
        /// </summary>
        private static bool ShouldDownloadInstruments(JObject config)
        {
            try
            {
                // Get general settings
                var generalSettings = config["GeneralSettings"] as JObject;
                if (generalSettings == null)
                {
                    Console.WriteLine("    No GeneralSettings found in config - download needed.");
                    return true;
                }

                // Check last download date
                string lastDownloadStr = generalSettings["InstrumentMastersLastDownload"]?.ToString();
                if (string.IsNullOrEmpty(lastDownloadStr))
                {
                    Console.WriteLine("    No previous download record found - download needed.");
                    return true;
                }

                if (!DateTime.TryParse(lastDownloadStr, out DateTime lastDownload))
                {
                    Console.WriteLine("    Could not parse last download date - download needed.");
                    return true;
                }

                // Check if download was today (instruments update daily)
                DateTime today = DateTime.Today;
                if (lastDownload.Date >= today)
                {
                    Console.WriteLine($"    Last download: {lastDownload:yyyy-MM-dd HH:mm:ss} (today)");
                    return false;
                }

                Console.WriteLine($"    Last download: {lastDownload:yyyy-MM-dd HH:mm:ss} (needs update)");
                return true;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"    Error checking download status: {ex.Message} - download needed.");
                return true;
            }
        }

        /// <summary>
        /// Load configuration from JSON file
        /// </summary>
        private static JObject LoadConfiguration()
        {
            try
            {
                if (!File.Exists(CONFIG_PATH))
                {
                    Console.WriteLine($"    ERROR: Configuration file not found at: {CONFIG_PATH}");
                    return null;
                }

                string json = File.ReadAllText(CONFIG_PATH);
                return JObject.Parse(json);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"    ERROR: Failed to load configuration: {ex.Message}");
                return null;
            }
        }

        /// <summary>
        /// Downloads instruments from Zerodha API and saves to mapped_instruments.json and InstrumentMasters.db
        /// </summary>
        private static async Task DownloadInstruments(JObject config, string apiKey, string accessToken)
        {
            try
            {
                // Download ALL instruments (no exchange filter) - needed for INDICES segment (GIFT NIFTY, NIFTY 50, SENSEX)
                // The downloader will automatically add indicator mappings to mapped_instruments.json
                var downloader = new InstrumentDownloader(apiKey, accessToken, INSTRUMENTS_PATH, DB_PATH);
                int count = await downloader.DownloadAndSaveInstrumentsAsync(null); // null = all exchanges

                Console.WriteLine($"    Downloaded {count} instruments");
                Console.WriteLine($"    JSON: {INSTRUMENTS_PATH}");
                Console.WriteLine($"    SQLite: {DB_PATH}");

                // Update config with download timestamp
                UpdateDownloadTimestamp(config);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"    WARNING: Failed to download instruments: {ex.Message}");
                Console.WriteLine("    You may need to manually create mapped_instruments.json");
            }
        }

        /// <summary>
        /// Updates the config file with the instrument download timestamp
        /// </summary>
        private static void UpdateDownloadTimestamp(JObject config)
        {
            try
            {
                var generalSettings = config["GeneralSettings"] as JObject;
                if (generalSettings == null)
                {
                    generalSettings = new JObject();
                    config["GeneralSettings"] = generalSettings;
                }

                generalSettings["InstrumentMastersLastDownload"] = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");

                // Write back to file
                string json = config.ToString(Formatting.Indented);
                File.WriteAllText(CONFIG_PATH, json);

                Console.WriteLine($"    Updated download timestamp in config.json");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"    WARNING: Failed to update download timestamp: {ex.Message}");
            }
        }

        /// <summary>
        /// Save generated token back to configuration file
        /// </summary>
        private static void SaveTokenToConfig(JObject config, TokenData tokenData)
        {
            try
            {
                var zerodhaConfig = config["Zerodha"] as JObject;
                if (zerodhaConfig == null)
                {
                    zerodhaConfig = new JObject();
                    config["Zerodha"] = zerodhaConfig;
                }

                // Update token fields
                zerodhaConfig["AccessToken"] = tokenData.AccessToken;

                // Save generation date and time separately for clarity
                zerodhaConfig["AccessTokenGeneratedAt"] = tokenData.GeneratedAt.ToString("yyyy-MM-dd HH:mm:ss");
                zerodhaConfig["AccessTokenExpiry"] = tokenData.GeneratedAt.Date.AddDays(1).ToString("yyyy-MM-dd") + " 00:00:00"; // Expires at midnight IST

                Console.WriteLine($"    Token generated at: {tokenData.GeneratedAt:yyyy-MM-dd HH:mm:ss}");
                Console.WriteLine($"    Token expires at: {tokenData.GeneratedAt.Date.AddDays(1):yyyy-MM-dd} 00:00:00 (midnight IST)");

                // Write back to file with formatting
                string json = config.ToString(Formatting.Indented);
                File.WriteAllText(CONFIG_PATH, json);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"    ERROR: Failed to save token: {ex.Message}");
            }
        }

        /// <summary>
        /// Test ICICI Breeze HTTP-based token generation (similar to Zerodha approach)
        /// </summary>
        private static async Task RunBreezeTokenGenerationTest()
        {
            Console.WriteLine();
            Console.WriteLine("===========================================");
            Console.WriteLine("  ICICI Breeze Token Generation Test");
            Console.WriteLine("  (HTTP-based, no Selenium)");
            Console.WriteLine("===========================================");
            Console.WriteLine();

            try
            {
                // Load Breeze configuration from local config folder
                string configPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "..", "..", "config", "icici_config.json");
                configPath = Path.GetFullPath(configPath);
                Console.WriteLine($"[1] Loading Breeze config from: {configPath}");

                if (!File.Exists(configPath))
                {
                    Console.WriteLine($"    ERROR: Config file not found at: {configPath}");
                    return;
                }

                string json = File.ReadAllText(configPath);
                var config = JObject.Parse(json);

                string apiKey = config["apiKey"]?.ToString();
                string apiSecret = config["apiSecret"]?.ToString();
                string login = config["login"]?.ToString();
                string password = config["password"]?.ToString();
                string totpKey = config["totpKey"]?.ToString();

                Console.WriteLine($"    API Key: {apiKey?.Substring(0, Math.Min(8, apiKey?.Length ?? 0))}...");
                Console.WriteLine($"    Login: {login}");
                Console.WriteLine($"    TOTP Key Present: {!string.IsNullOrEmpty(totpKey)}");
                Console.WriteLine();

                // Validate credentials
                if (string.IsNullOrEmpty(apiKey) || string.IsNullOrEmpty(apiSecret) ||
                    string.IsNullOrEmpty(login) || string.IsNullOrEmpty(password) ||
                    string.IsNullOrEmpty(totpKey))
                {
                    Console.WriteLine("    ERROR: Missing required credentials");
                    Console.WriteLine($"    apiKey: {(string.IsNullOrEmpty(apiKey) ? "MISSING" : "OK")}");
                    Console.WriteLine($"    apiSecret: {(string.IsNullOrEmpty(apiSecret) ? "MISSING" : "OK")}");
                    Console.WriteLine($"    login: {(string.IsNullOrEmpty(login) ? "MISSING" : "OK")}");
                    Console.WriteLine($"    password: {(string.IsNullOrEmpty(password) ? "MISSING" : "OK")}");
                    Console.WriteLine($"    totpKey: {(string.IsNullOrEmpty(totpKey) ? "MISSING" : "OK")}");
                    return;
                }

                // Test TOTP generation first
                Console.WriteLine("[2] Testing TOTP generation...");
                string totp = TotpGenerator.GenerateTotp(totpKey);
                Console.WriteLine($"    Generated TOTP: {totp}");
                Console.WriteLine();

                // Initialize Breeze token generator
                Console.WriteLine("[3] Initializing Breeze Token Generator...");
                var generator = new BreezeTokenGenerator(apiKey, apiSecret, login, password, totpKey);

                // Subscribe to status updates
                generator.StatusChanged += (sender, e) =>
                {
                    var timestamp = e.Timestamp.ToString("HH:mm:ss.fff");
                    if (e.IsError)
                        Console.WriteLine($"    [{timestamp}] ERROR: {e.Message}");
                    else
                        Console.WriteLine($"    [{timestamp}] {e.Message}");
                };
                Console.WriteLine();

                // Attempt token generation
                Console.WriteLine("[4] Starting HTTP-based token generation...");
                Console.WriteLine("    NOTE: This is an experimental approach.");
                Console.WriteLine("    ICICI may require Selenium for full login flow.");
                Console.WriteLine();

                var result = await generator.GenerateTokenAsync();

                Console.WriteLine();
                if (result.Success)
                {
                    Console.WriteLine("===========================================");
                    Console.WriteLine("  TOKEN GENERATION SUCCESSFUL!");
                    Console.WriteLine("===========================================");
                    Console.WriteLine($"  Session Token: {result.SessionToken}");
                    Console.WriteLine();

                    // Save to config
                    config["sessionKey"] = result.SessionToken;
                    config["sessionKeyGeneratedAt"] = DateTime.Now.ToString("yyyy-MM-ddTHH:mm:ss.ffffff");
                    File.WriteAllText(configPath, config.ToString(Formatting.Indented));
                    Console.WriteLine("  Token saved to config file.");
                }
                else
                {
                    Console.WriteLine("===========================================");
                    Console.WriteLine("  TOKEN GENERATION FAILED");
                    Console.WriteLine("===========================================");
                    Console.WriteLine($"  Error: {result.Error}");
                    Console.WriteLine();
                    Console.WriteLine("  ICICI's login flow may require Selenium-based automation.");
                    Console.WriteLine("  Unlike Zerodha which has JSON API endpoints (/api/login, /api/twofa),");
                    Console.WriteLine("  ICICI uses form-based login with server-side rendering.");
                }
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
    }
}
