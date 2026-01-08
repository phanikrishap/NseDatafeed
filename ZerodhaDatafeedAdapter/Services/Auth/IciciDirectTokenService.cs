using System;
using System.Diagnostics;
using System.IO;
using System.Reactive.Linq;
using System.Reactive.Subjects;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using ZerodhaDatafeedAdapter.Classes;
using ZerodhaDatafeedAdapter.Logging;

namespace ZerodhaDatafeedAdapter.Services.Auth
{
    /// <summary>
    /// Service for managing ICICI Direct Breeze authentication in a non-blocking manner.
    /// This service runs token validation/generation in the background and signals
    /// availability via Rx observables. Failures are non-blocking and don't affect
    /// the main trading operations (which use Zerodha).
    /// </summary>
    public class IciciDirectTokenService : IDisposable
    {
        private static readonly Lazy<IciciDirectTokenService> _instance =
            new Lazy<IciciDirectTokenService>(() => new IciciDirectTokenService());

        public static IciciDirectTokenService Instance => _instance.Value;

        // Rx subjects for status updates
        private readonly BehaviorSubject<IciciBrokerStatus> _brokerStatusSubject;
        private readonly Subject<string> _statusMessageSubject;

        // Current state
        private string _sessionKey;
        private DateTime? _sessionGeneratedAt;
        private bool _isAvailable;
        private bool _isInitializing;

        // Configuration
        private JObject _iciciConfig;

        /// <summary>
        /// Observable that emits the current broker status (available/unavailable)
        /// </summary>
        public IObservable<IciciBrokerStatus> BrokerStatus => _brokerStatusSubject.AsObservable();

        /// <summary>
        /// Observable that emits status messages during initialization/token generation
        /// </summary>
        public IObservable<string> StatusMessages => _statusMessageSubject.AsObservable();

        /// <summary>
        /// Gets whether ICICI Direct broker is currently available
        /// </summary>
        public bool IsAvailable => _isAvailable;

        /// <summary>
        /// Gets the current session key (if available)
        /// </summary>
        public string SessionKey => _sessionKey;

        /// <summary>
        /// Gets when the session was generated
        /// </summary>
        public DateTime? SessionGeneratedAt => _sessionGeneratedAt;

        private IciciDirectTokenService()
        {
            _brokerStatusSubject = new BehaviorSubject<IciciBrokerStatus>(
                new IciciBrokerStatus { IsAvailable = false, Message = "Not initialized" });
            _statusMessageSubject = new Subject<string>();
            _isAvailable = false;
            _isInitializing = false;
        }

        /// <summary>
        /// Initialize the service and attempt to validate/generate token.
        /// This method is non-blocking - it starts the process in the background.
        /// Subscribe to BrokerStatus to know when ICICI becomes available.
        /// </summary>
        public void Initialize()
        {
            if (_isInitializing)
            {
                Logger.Info("[ICICI] Already initializing, skipping duplicate call");
                return;
            }

            _isInitializing = true;

            // Run initialization in background thread pool
            ThreadPool.QueueUserWorkItem(_ => InitializeAsync());
        }

        private async void InitializeAsync()
        {
            var stopwatch = Stopwatch.StartNew();

            try
            {
                StartupLogger.LogIciciInitStart();
                EmitStatus("Starting ICICI Direct broker initialization...");

                // Load configuration
                if (!LoadConfiguration())
                {
                    StartupLogger.LogIciciConfiguration(false, false, false);
                    EmitStatus("ICICI configuration not found or invalid", false);
                    StartupLogger.LogIciciInitComplete(false, stopwatch.ElapsedMilliseconds);
                    _isInitializing = false;
                    return;
                }

                // Check if AutoLogin is enabled
                bool autoLogin = _iciciConfig["AutoLogin"]?.ToObject<bool>() ?? false;
                string login = _iciciConfig["Login"]?.ToString();
                string password = _iciciConfig["Password"]?.ToString();
                string totpSecret = _iciciConfig["TotpSecret"]?.ToString();
                bool credentialsComplete = !string.IsNullOrEmpty(login) &&
                                          !string.IsNullOrEmpty(password) &&
                                          !string.IsNullOrEmpty(totpSecret);

                StartupLogger.LogIciciConfiguration(true, autoLogin, credentialsComplete);

                if (!autoLogin)
                {
                    EmitStatus("ICICI AutoLogin is disabled", false);
                    StartupLogger.LogIciciInitComplete(false, stopwatch.ElapsedMilliseconds);
                    _isInitializing = false;
                    return;
                }

                // Get existing session key
                _sessionKey = _iciciConfig["SessionKey"]?.ToString();
                string sessionGeneratedAtStr = _iciciConfig["SessionKeyGeneratedAt"]?.ToString();

                if (!string.IsNullOrEmpty(sessionGeneratedAtStr) && DateTime.TryParse(sessionGeneratedAtStr, out DateTime generatedAt))
                {
                    _sessionGeneratedAt = generatedAt;
                }

                // Check if we have a valid session
                bool hasExistingSession = !string.IsNullOrEmpty(_sessionKey) && !IciciDirectTokenGenerator.IsSessionExpired(_sessionGeneratedAt);
                StartupLogger.LogIciciSessionValidation(hasExistingSession, _sessionKey);

                if (hasExistingSession)
                {
                    // Validate the session
                    EmitStatus("Validating existing ICICI session...");
                    bool isValid = await ValidateExistingSessionAsync();
                    StartupLogger.LogIciciSessionValidationResult(isValid,
                        isValid ? $"Generated: {_sessionGeneratedAt:yyyy-MM-dd HH:mm}" : "Session expired or invalid");

                    if (isValid)
                    {
                        SetAvailable(true, "ICICI Direct broker is available (existing session valid)");
                        StartupLogger.LogIciciBrokerStatus(true, "Existing session validated");
                        StartupLogger.LogIciciInitComplete(true, stopwatch.ElapsedMilliseconds);
                        _isInitializing = false;
                        return;
                    }
                }

                // Need to generate new token
                StartupLogger.LogIciciTokenGenerationStart();
                EmitStatus("Attempting to generate new ICICI session token...");
                await TryGenerateNewTokenAsync();

                StartupLogger.LogIciciInitComplete(_isAvailable, stopwatch.ElapsedMilliseconds);
            }
            catch (Exception ex)
            {
                Logger.Info($"[ICICI] Initialization error: {ex.Message}");
                StartupLogger.LogIciciBrokerStatus(false, $"Error: {ex.Message}");
                EmitStatus($"ICICI initialization failed: {ex.Message}", false);
                StartupLogger.LogIciciInitComplete(false, stopwatch.ElapsedMilliseconds);
            }
            finally
            {
                _isInitializing = false;
            }
        }

        private bool LoadConfiguration()
        {
            try
            {
                string documentsPath = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
                string configPath = Path.Combine(documentsPath, Constants.BaseDataFolder, Constants.ConfigFileName);

                if (!File.Exists(configPath))
                {
                    Logger.Info($"[ICICI] Config file not found: {configPath}");
                    return false;
                }

                string json = File.ReadAllText(configPath);
                var config = JObject.Parse(json);

                _iciciConfig = config["IciciDirect"] as JObject;

                if (_iciciConfig == null)
                {
                    Logger.Info("[ICICI] No IciciDirect section in config");
                    return false;
                }

                // Validate required fields
                string apiKey = _iciciConfig["ApiKey"]?.ToString();
                string apiSecret = _iciciConfig["ApiSecret"]?.ToString();

                if (string.IsNullOrEmpty(apiKey) || string.IsNullOrEmpty(apiSecret))
                {
                    Logger.Info("[ICICI] Missing ApiKey or ApiSecret");
                    return false;
                }

                return true;
            }
            catch (Exception ex)
            {
                Logger.Info($"[ICICI] Error loading config: {ex.Message}");
                return false;
            }
        }

        private async Task<bool> ValidateExistingSessionAsync()
        {
            try
            {
                string apiKey = _iciciConfig["ApiKey"]?.ToString();
                string apiSecret = _iciciConfig["ApiSecret"]?.ToString();
                string login = _iciciConfig["Login"]?.ToString();
                string password = _iciciConfig["Password"]?.ToString();
                string totpSecret = _iciciConfig["TotpSecret"]?.ToString();

                // Create generator just for validation
                using (var generator = new IciciDirectTokenGenerator(
                    apiKey, apiSecret, login ?? "", password ?? "", totpSecret ?? ""))
                {
                    generator.StatusChanged += (s, e) =>
                    {
                        Logger.Info($"[ICICI] {e.Message}");
                    };

                    return await generator.ValidateSessionAsync(_sessionKey);
                }
            }
            catch (Exception ex)
            {
                Logger.Info($"[ICICI] Session validation error: {ex.Message}");
                return false;
            }
        }

        private async Task TryGenerateNewTokenAsync()
        {
            try
            {
                string apiKey = _iciciConfig["ApiKey"]?.ToString();
                string apiSecret = _iciciConfig["ApiSecret"]?.ToString();
                string login = _iciciConfig["Login"]?.ToString();
                string password = _iciciConfig["Password"]?.ToString();
                string totpSecret = _iciciConfig["TotpSecret"]?.ToString();

                // Validate credentials for token generation
                if (string.IsNullOrEmpty(login) || string.IsNullOrEmpty(password) || string.IsNullOrEmpty(totpSecret))
                {
                    EmitStatus("ICICI credentials incomplete for auto-login", false);
                    StartupLogger.LogIciciTokenGenerationResult(false, null, "Missing Login, Password, or TotpSecret");
                    return;
                }

                using (var generator = new IciciDirectTokenGenerator(apiKey, apiSecret, login, password, totpSecret))
                {
                    generator.StatusChanged += (s, e) =>
                    {
                        Logger.Info($"[ICICI TokenGen] {e.Message}");
                        StartupLogger.LogIciciTokenGenerationStep("TokenGen", e.Message);
                        EmitStatus($"ICICI: {e.Message}");
                    };

                    // Run synchronously on this thread (we're already in background)
                    var result = await generator.GenerateTokenAsync();

                    if (result.Success)
                    {
                        _sessionKey = result.SessionToken;
                        _sessionGeneratedAt = result.GeneratedAt;

                        // Save to config
                        SaveSessionToConfig(result.SessionToken, result.GeneratedAt);

                        SetAvailable(true, "ICICI Direct broker is available (new token generated)");
                        StartupLogger.LogIciciTokenGenerationResult(true, result.SessionToken);
                        StartupLogger.LogIciciBrokerStatus(true, "New token generated successfully");

                        NinjaTrader.NinjaScript.NinjaScript.Log(
                            "[ZerodhaAdapter] ICICI Direct broker is now available (historical data)",
                            NinjaTrader.Cbi.LogLevel.Information);
                    }
                    else
                    {
                        SetAvailable(false, $"ICICI token generation failed: {result.Error}");
                        StartupLogger.LogIciciTokenGenerationResult(false, null, result.Error);
                        StartupLogger.LogIciciBrokerStatus(false, result.Error);

                        // This is non-blocking - just log it
                        NinjaTrader.NinjaScript.NinjaScript.Log(
                            $"[ZerodhaAdapter] ICICI Direct auto-login failed (non-critical): {result.Error}",
                            NinjaTrader.Cbi.LogLevel.Warning);
                    }
                }
            }
            catch (Exception ex)
            {
                SetAvailable(false, $"ICICI token generation error: {ex.Message}");
                StartupLogger.LogIciciTokenGenerationResult(false, null, ex.Message);
                StartupLogger.LogIciciBrokerStatus(false, $"Exception: {ex.Message}");
            }
        }

        private void SaveSessionToConfig(string sessionToken, DateTime generatedAt)
        {
            try
            {
                string documentsPath = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
                string configPath = Path.Combine(documentsPath, Constants.BaseDataFolder, Constants.ConfigFileName);

                string json = File.ReadAllText(configPath);
                var config = JObject.Parse(json);

                var iciciConfig = config["IciciDirect"] as JObject;
                if (iciciConfig != null)
                {
                    iciciConfig["SessionKey"] = sessionToken;
                    iciciConfig["SessionKeyGeneratedAt"] = generatedAt.ToString("yyyy-MM-ddTHH:mm:ss");

                    File.WriteAllText(configPath, config.ToString(Formatting.Indented));
                    Logger.Info("[ICICI] Session saved to config");
                }
            }
            catch (Exception ex)
            {
                Logger.Info($"[ICICI] Failed to save session to config: {ex.Message}");
            }
        }

        private void SetAvailable(bool isAvailable, string message)
        {
            _isAvailable = isAvailable;
            _brokerStatusSubject.OnNext(new IciciBrokerStatus
            {
                IsAvailable = isAvailable,
                Message = message,
                SessionKey = _sessionKey,
                SessionGeneratedAt = _sessionGeneratedAt
            });
        }

        private void EmitStatus(string message, bool available)
        {
            _statusMessageSubject.OnNext(message);
            SetAvailable(available, message);
        }

        private void EmitStatus(string message)
        {
            _statusMessageSubject.OnNext(message);
        }

        /// <summary>
        /// Manually trigger a token refresh (for future use)
        /// </summary>
        public void RefreshToken()
        {
            if (!_isInitializing)
            {
                _isInitializing = true;
                ThreadPool.QueueUserWorkItem(_ =>
                {
                    try
                    {
                        TryGenerateNewTokenAsync().Wait();
                    }
                    finally
                    {
                        _isInitializing = false;
                    }
                });
            }
        }

        public void Dispose()
        {
            _brokerStatusSubject?.Dispose();
            _statusMessageSubject?.Dispose();
        }
    }

    /// <summary>
    /// Status of ICICI Direct broker availability
    /// </summary>
    public class IciciBrokerStatus
    {
        public bool IsAvailable { get; set; }
        public string Message { get; set; }
        public string SessionKey { get; set; }
        public DateTime? SessionGeneratedAt { get; set; }
    }
}
