using System;
using System.IO;
using System.Threading.Tasks;
using System.Windows;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using QANinjaAdapter.Services.Auth;

namespace QANinjaAdapter.Services.Configuration
{
    /// <summary>
    /// Manages loading and accessing configuration settings for the adapter
    /// </summary>
    public class ConfigurationManager
    {
        // Configuration file path
        private const string CONFIG_FILE_PATH = "NinjaTrader 8\\QAAdapter\\config.json";
        
        // Singleton instance
        private static ConfigurationManager _instance;
        
        // Configuration data
        private JObject _config;
        
        // Active broker settings
        private string _activeWebSocketBroker;
        private string _activeHistoricalBroker;
        
        // Credentials
        private string _apiKey = string.Empty;
        private string _secretKey = string.Empty;
        private string _accessToken = string.Empty;

        // Logging configuration
        private bool _enableVerboseTickLogging = false; // Default to false

        /// <summary>
        /// Gets the singleton instance of the ConfigurationManager
        /// </summary>
        public static ConfigurationManager Instance
        {
            get
            {
                if (_instance == null)
                    _instance = new ConfigurationManager();
                return _instance;
            }
        }

        /// <summary>
        /// Gets the API key for the active broker
        /// </summary>
        public string ApiKey => _apiKey;

        /// <summary>
        /// Gets the secret key for the active broker
        /// </summary>
        public string SecretKey => _secretKey;

        /// <summary>
        /// Gets the access token for the active broker
        /// </summary>
        public string AccessToken => _accessToken;

        /// <summary>
        /// Gets the active WebSocket broker name
        /// </summary>
        public string ActiveWebSocketBroker => _activeWebSocketBroker;

        /// <summary>
        /// Gets the active historical data broker name
        /// </summary>
        public string ActiveHistoricalBroker => _activeHistoricalBroker;

        /// <summary>
        /// Gets whether verbose tick logging is enabled
        /// </summary>
        public bool EnableVerboseTickLogging => _enableVerboseTickLogging;

        /// <summary>
        /// Private constructor to enforce singleton pattern
        /// </summary>
        private ConfigurationManager()
        {
        }

        /// <summary>
        /// Loads configuration from the config file
        /// </summary>
        /// <returns>True if configuration was loaded successfully, false otherwise</returns>
        public bool LoadConfiguration()
        {
            try
            {
                // Get the user's Documents folder path
                string documentsPath = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
                string fullConfigPath = Path.Combine(documentsPath, CONFIG_FILE_PATH);

                // Check if config file exists
                if (!File.Exists(fullConfigPath))
                {
                    MessageBox.Show($"Configuration file not found at: {fullConfigPath}",
                        "Configuration Error", MessageBoxButton.OK, MessageBoxImage.Error);
                    return false;
                }

                // Read JSON configuration
                string jsonConfig = File.ReadAllText(fullConfigPath);
                _config = JObject.Parse(jsonConfig);

                // Get active broker configurations
                JObject activeBrokers = _config["Active"] as JObject;

                if (activeBrokers == null)
                {
                    MessageBox.Show("No active broker specified in the configuration file.",
                        "Configuration Error", MessageBoxButton.OK, MessageBoxImage.Error);
                    return false;
                }

                // Get websocket and historical broker names
                _activeWebSocketBroker = activeBrokers["Websocket"]?.ToString();
                _activeHistoricalBroker = activeBrokers["Historical"]?.ToString();

                if (string.IsNullOrEmpty(_activeWebSocketBroker) || string.IsNullOrEmpty(_activeHistoricalBroker))
                {
                    MessageBox.Show("Websocket or Historical broker not specified in Active configuration.",
                        "Configuration Error", MessageBoxButton.OK, MessageBoxImage.Error);
                    return false;
                }

                // Load websocket broker configuration
                JObject webSocketBrokerConfig = _config[_activeWebSocketBroker] as JObject;

                Logger.Info($"Loading configuration for websocket broker: {_activeWebSocketBroker}.");

                if (webSocketBrokerConfig != null)
                {
                    // Update API keys for websocket
                    LoadBrokerCredentials(webSocketBrokerConfig);
                }
                else
                {
                    MessageBox.Show($"Configuration for websocket broker '{_activeWebSocketBroker}' not found.",
                        "Configuration Error", MessageBoxButton.OK, MessageBoxImage.Error);
                    return false;
                }

                // If historical broker is different, load its configuration too
                if (_activeWebSocketBroker != _activeHistoricalBroker)
                {
                    JObject historicalBrokerConfig = _config[_activeHistoricalBroker] as JObject;
                    if (historicalBrokerConfig != null)
                    {
                        // You might need separate variables for historical broker
                        // This is just updating the same variables
                        LoadBrokerCredentials(historicalBrokerConfig);
                    }
                    else
                    {
                        MessageBox.Show($"Configuration for historical broker '{_activeHistoricalBroker}' not found.",
                            "Configuration Error", MessageBoxButton.OK, MessageBoxImage.Error);
                        return false;
                    }
                }

                // Load general settings (like logging)
                JObject generalSettings = _config["GeneralSettings"] as JObject;
                if (generalSettings != null)
                {
                    _enableVerboseTickLogging = generalSettings["EnableVerboseTickLogging"]?.ToObject<bool>() ?? false;
                }

                return true;
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Error loading configuration: {ex.Message}",
                    "Configuration Error", MessageBoxButton.OK, MessageBoxImage.Error);
                return false;
            }
        }

        /// <summary>
        /// Loads broker credentials from the specified broker configuration
        /// </summary>
        /// <param name="brokerConfig">The broker configuration object</param>
        private void LoadBrokerCredentials(JObject brokerConfig)
        {
            _apiKey = brokerConfig["Api"]?.ToString() ?? _apiKey;
            _secretKey = brokerConfig["Secret"]?.ToString() ?? _secretKey;
            _accessToken = brokerConfig["AccessToken"]?.ToString() ?? _accessToken;

            Logger.Info($"Broker API credentials have been processed.");
        }

        /// <summary>
        /// Checks if the access token is valid or needs regeneration
        /// </summary>
        /// <param name="brokerConfig">The broker configuration object</param>
        /// <returns>True if token is valid, false if expired or missing</returns>
        private bool IsTokenValid(JObject brokerConfig)
        {
            string accessToken = brokerConfig["AccessToken"]?.ToString();
            string generatedAtStr = brokerConfig["AccessTokenGeneratedAt"]?.ToString();

            // No token - invalid
            if (string.IsNullOrEmpty(accessToken))
            {
                Logger.Info("No access token found in configuration.");
                return false;
            }

            // No generation timestamp - invalid (token format may be old)
            if (string.IsNullOrEmpty(generatedAtStr))
            {
                Logger.Info("No token generation timestamp found - token may be stale.");
                return false;
            }

            // Parse and check expiry (tokens expire at midnight IST)
            if (DateTime.TryParse(generatedAtStr, out DateTime generatedAt))
            {
                if (ZerodhaTokenGenerator.IsTokenExpired(generatedAt))
                {
                    Logger.Info($"Access token has expired (generated: {generatedAt:yyyy-MM-dd HH:mm:ss}).");
                    return false;
                }

                Logger.Info($"Access token is valid (generated: {generatedAt:yyyy-MM-dd HH:mm:ss}).");
                return true;
            }

            Logger.Info("Could not parse token generation timestamp - treating as invalid.");
            return false;
        }

        /// <summary>
        /// Attempts to auto-generate a new access token for Zerodha
        /// </summary>
        /// <param name="brokerConfig">The broker configuration object</param>
        /// <returns>True if token was generated successfully</returns>
        public Task<bool> TryAutoGenerateTokenAsync(JObject brokerConfig)
        {
            try
            {
                // Check if AutoLogin is enabled
                bool autoLogin = brokerConfig["AutoLogin"]?.ToObject<bool>() ?? false;
                if (!autoLogin)
                {
                    Logger.Info("AutoLogin is disabled for this broker.");
                    return Task.FromResult(false);
                }

                // Get credentials for token generation
                string apiKey = brokerConfig["Api"]?.ToString();
                string apiSecret = brokerConfig["Secret"]?.ToString();
                string userId = brokerConfig["UserId"]?.ToString();
                string password = brokerConfig["Password"]?.ToString();
                string totpSecret = brokerConfig["TotpSecret"]?.ToString();
                string redirectUrl = brokerConfig["RedirectUrl"]?.ToString() ?? "http://127.0.0.1:8001/callback";

                // Validate required fields
                if (string.IsNullOrEmpty(apiKey) || string.IsNullOrEmpty(apiSecret) ||
                    string.IsNullOrEmpty(userId) || string.IsNullOrEmpty(password) ||
                    string.IsNullOrEmpty(totpSecret))
                {
                    Logger.Info("Missing credentials for auto token generation. Please configure UserId, Password, and TotpSecret.");
                    return Task.FromResult(false);
                }

                Logger.Info($"Starting automatic token generation for user: {userId}");

                // Create token generator and subscribe to status updates (log to file only, not NinjaTrader control panel)
                var generator = new ZerodhaTokenGenerator(apiKey, apiSecret, userId, password, totpSecret, redirectUrl);
                generator.StatusChanged += (sender, e) =>
                {
                    // Log all status updates to file only (not to NinjaTrader control panel - too verbose)
                    if (e.IsError)
                    {
                        Logger.Info($"[TokenGen ERROR] {e.Message}");
                    }
                    else
                    {
                        Logger.Info($"[TokenGen] {e.Message}");
                    }
                };

                // Use synchronous method on dedicated thread to avoid NinjaTrader sync context deadlocks
                Logger.Info("Starting synchronous token generation on dedicated thread...");

                var tokenData = generator.GenerateTokenSync();

                // Update config with new token
                brokerConfig["AccessToken"] = tokenData.AccessToken;
                brokerConfig["AccessTokenGeneratedAt"] = tokenData.GeneratedAt.ToString("yyyy-MM-dd HH:mm:ss");
                brokerConfig["AccessTokenExpiry"] = tokenData.GeneratedAt.Date.AddDays(1).ToString("yyyy-MM-dd") + " 00:00:00";

                // Update instance variables
                _accessToken = tokenData.AccessToken;

                // Save updated config to file
                SaveConfiguration();

                Logger.Info($"Token generated successfully at {tokenData.GeneratedAt:yyyy-MM-dd HH:mm:ss}");
                NinjaTrader.NinjaScript.NinjaScript.Log(
                    $"[QAAdapter] Zerodha token generated successfully! Expires at midnight IST.",
                    NinjaTrader.Cbi.LogLevel.Information);
                return Task.FromResult(true);
            }
            catch (Exception ex)
            {
                Logger.Info($"Auto token generation failed: {ex.Message}");
                NinjaTrader.NinjaScript.NinjaScript.Log(
                    $"[QAAdapter] Auto token generation FAILED: {ex.Message}",
                    NinjaTrader.Cbi.LogLevel.Error);
                return Task.FromResult(false);
            }
        }

        /// <summary>
        /// Saves the current configuration back to the config file
        /// </summary>
        private void SaveConfiguration()
        {
            try
            {
                string documentsPath = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
                string fullConfigPath = Path.Combine(documentsPath, CONFIG_FILE_PATH);

                string json = _config.ToString(Formatting.Indented);
                File.WriteAllText(fullConfigPath, json);

                Logger.Info("Configuration saved successfully.");
            }
            catch (Exception ex)
            {
                Logger.Info($"Failed to save configuration: {ex.Message}");
            }
        }

        /// <summary>
        /// Ensures a valid access token is available, generating one if necessary
        /// Call this during adapter initialization
        /// </summary>
        /// <returns>True if a valid token is available</returns>
        public async Task<bool> EnsureValidTokenAsync()
        {
            try
            {
                if (_config == null)
                {
                    Logger.Info("Configuration not loaded, cannot ensure valid token.");
                    return false;
                }

                // Only auto-generate for Zerodha for now
                if (_activeWebSocketBroker != "Zerodha")
                {
                    Logger.Info($"Auto token generation not supported for broker: {_activeWebSocketBroker}");
                    return !string.IsNullOrEmpty(_accessToken);
                }

                JObject brokerConfig = _config["Zerodha"] as JObject;
                if (brokerConfig == null)
                {
                    Logger.Info("Zerodha configuration not found.");
                    return false;
                }

                // Check if current token is valid
                if (IsTokenValid(brokerConfig))
                {
                    return true;
                }

                // Token is invalid or expired - try to generate a new one
                Logger.Info("Token is invalid or expired. Attempting auto-generation...");
                return await TryAutoGenerateTokenAsync(brokerConfig).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                Logger.Info($"Error ensuring valid token: {ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// Gets the credentials for a specific broker
        /// </summary>
        /// <param name="brokerName">The name of the broker</param>
        /// <returns>A tuple containing the API key, secret key, and access token</returns>
        public (string ApiKey, string SecretKey, string AccessToken) GetCredentialsForBroker(string brokerName)
        {
            try
            {
                if (_config == null)
                {
                    // If config is not loaded yet, try to load it
                    if (!LoadConfiguration())
                    {
                        return (_apiKey, _secretKey, _accessToken);
                    }
                }

                JObject brokerConfig = _config[brokerName] as JObject;
                if (brokerConfig == null)
                {
                    return (_apiKey, _secretKey, _accessToken);
                }

                string bApiKey = brokerConfig["Api"]?.ToString() ?? _apiKey;
                string bSecretKey = brokerConfig["Secret"]?.ToString() ?? _secretKey;
                string bAccessToken = brokerConfig["AccessToken"]?.ToString() ?? _accessToken;

                return (bApiKey, bSecretKey, bAccessToken);
            }
            catch
            {
                return (_apiKey, _secretKey, _accessToken);
            }
        }
    }
}
