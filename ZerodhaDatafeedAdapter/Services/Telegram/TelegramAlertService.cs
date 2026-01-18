using System;
using System.Collections.Concurrent;
using System.Linq;
using System.Net.Http;
using System.Reactive.Disposables;
using System.Reactive.Linq;
using System.Reactive.Subjects;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using ZerodhaDatafeedAdapter.Models;
using ZerodhaDatafeedAdapter.Services.Configuration;
using ZerodhaDatafeedAdapter.Services.Simulation;

namespace ZerodhaDatafeedAdapter.Services.Telegram
{
    /// <summary>
    /// Telegram alert types for categorization
    /// </summary>
    public enum TelegramAlertType
    {
        Startup,
        TokenValidation,
        OptionChain,
        TBSModule,
        TrancheEntry,
        TrancheExit,
        StoplossTriggered,
        TargetHit,
        ProfitProtection,
        Error,
        Info
    }

    /// <summary>
    /// Represents a Telegram alert message
    /// </summary>
    public class TelegramAlert
    {
        public TelegramAlertType Type { get; set; }
        public string Message { get; set; }
        public DateTime Timestamp { get; set; } = DateTime.Now;
        public bool IsUrgent { get; set; } = false;

        /// <summary>
        /// Formats the alert with timestamp prefix
        /// </summary>
        public string FormattedMessage => $"[{Timestamp:HH:mm:ss}] {Message}";
    }

    /// <summary>
    /// Bot command received from Telegram
    /// </summary>
    public class TelegramCommand
    {
        public string Command { get; set; }
        public string[] Arguments { get; set; }
        public long ChatId { get; set; }
        public int MessageId { get; set; }
        public DateTime ReceivedAt { get; set; } = DateTime.Now;
    }

    /// <summary>
    /// Lightweight Telegram Alert Service using direct HTTP API calls.
    /// No external Telegram.Bot dependency - uses HttpClient directly.
    /// Provides one-way alerts and two-way bot command handling for TBS control.
    /// </summary>
    public class TelegramAlertService : IDisposable
    {
        #region Singleton

        private static readonly Lazy<TelegramAlertService> _instance =
            new Lazy<TelegramAlertService>(() => new TelegramAlertService());
        public static TelegramAlertService Instance => _instance.Value;

        #endregion

        #region Constants

        private const string TELEGRAM_API_BASE = "https://api.telegram.org/bot";
        private const int POLLING_TIMEOUT_SECONDS = 30;
        private const int MIN_MESSAGE_INTERVAL_MS = 100;

        #endregion

        #region Fields

        private HttpClient _httpClient;
        private TelegramSettings _settings;
        private CancellationTokenSource _pollingCts;
        private Task _pollingTask;
        private long _lastUpdateId = 0;
        private bool _isInitialized = false;
        private bool _isDisposed = false;
        private string _botUsername;

        // Rx subjects for event-driven architecture
        private readonly Subject<TelegramAlert> _alertSubject = new Subject<TelegramAlert>();
        private readonly Subject<TelegramCommand> _commandSubject = new Subject<TelegramCommand>();
        private readonly CompositeDisposable _subscriptions = new CompositeDisposable();

        // Rate limiting
        private readonly SemaphoreSlim _sendSemaphore = new SemaphoreSlim(1, 1);

        #endregion

        #region Properties

        /// <summary>
        /// Whether the service is properly initialized and ready
        /// </summary>
        public bool IsReady => _isInitialized && _settings?.IsValid == true && _httpClient != null;

        /// <summary>
        /// Stream of incoming bot commands (for TBS control)
        /// </summary>
        public IObservable<TelegramCommand> CommandStream => _commandSubject.AsObservable();

        /// <summary>
        /// Current settings (read-only)
        /// </summary>
        public TelegramSettings Settings => _settings;

        /// <summary>
        /// Bot username (available after initialization)
        /// </summary>
        public string BotUsername => _botUsername;

        #endregion

        #region Constructor

        private TelegramAlertService()
        {
            Logger.Info("[TelegramAlertService] Instance created (lightweight HTTP implementation)");
        }

        #endregion

        #region Initialization

        /// <summary>
        /// Initialize the Telegram service with settings from ConfigurationManager.
        /// Call this during adapter startup after configuration is loaded.
        /// </summary>
        public async Task InitializeAsync()
        {
            if (_isInitialized)
            {
                Logger.Debug("[TelegramAlertService] Already initialized");
                return;
            }

            try
            {
                // Load settings from ConfigurationManager
                _settings = ConfigurationManager.Instance.TelegramSettings;

                if (_settings == null || !_settings.Enabled)
                {
                    Logger.Info("[TelegramAlertService] Telegram integration is disabled in config");
                    return;
                }

                if (!_settings.IsValid)
                {
                    Logger.Warn("[TelegramAlertService] Invalid Telegram settings - Token or ChatId missing");
                    return;
                }

                // Initialize HTTP client
                _httpClient = new HttpClient
                {
                    Timeout = TimeSpan.FromSeconds(POLLING_TIMEOUT_SECONDS + 10)
                };

                // Test connection by getting bot info
                var me = await GetMeAsync();
                if (me == null)
                {
                    Logger.Error("[TelegramAlertService] Failed to connect to Telegram API");
                    return;
                }

                _botUsername = me.Value<string>("username");
                Logger.Info($"[TelegramAlertService] Bot connected: @{_botUsername}");

                // Set bot commands menu
                await SetBotCommandsAsync();

                // Setup alert processing pipeline
                SetupAlertPipeline();

                // Start receiving commands if enabled
                if (_settings.EnableBotCommands)
                {
                    StartPolling();
                }

                _isInitialized = true;
                Logger.Info("[TelegramAlertService] Initialized successfully");

                // Send startup message
                if (_settings.SendStartupAlerts)
                {
                    await SendAlertAsync(new TelegramAlert
                    {
                        Type = TelegramAlertType.Startup,
                        Message = "Starting NinjaTrader"
                    });
                }
            }
            catch (Exception ex)
            {
                Logger.Error($"[TelegramAlertService] Initialization failed: {ex.Message}", ex);
                _isInitialized = false;
            }
        }

        /// <summary>
        /// Setup the Rx pipeline for processing alerts
        /// </summary>
        private void SetupAlertPipeline()
        {
            var alertSubscription = _alertSubject
                .Subscribe(
                    alert => Task.Run(async () => await ProcessAlertAsync(alert)),
                    ex => Logger.Error($"[TelegramAlertService] Alert pipeline error: {ex.Message}"));

            _subscriptions.Add(alertSubscription);
            Logger.Debug("[TelegramAlertService] Alert pipeline configured");
        }

        #endregion

        #region Telegram API Methods

        /// <summary>
        /// Get bot information (getMe API)
        /// </summary>
        private async Task<JObject> GetMeAsync()
        {
            try
            {
                var response = await _httpClient.GetStringAsync($"{TELEGRAM_API_BASE}{_settings.Token}/getMe");
                var json = JObject.Parse(response);
                if (json.Value<bool>("ok"))
                {
                    return json["result"] as JObject;
                }
                Logger.Error($"[TelegramAlertService] getMe failed: {json}");
                return null;
            }
            catch (Exception ex)
            {
                Logger.Error($"[TelegramAlertService] getMe exception: {ex.Message}");
                return null;
            }
        }

        /// <summary>
        /// Set the bot's command menu (setMyCommands API)
        /// </summary>
        private async Task<bool> SetBotCommandsAsync()
        {
            try
            {
                var commands = new[]
                {
                    new { command = "help", description = "Show available commands" },
                    new { command = "status", description = "Show current TBS status" },
                    new { command = "pnl", description = "Show P&L summary" },
                    new { command = "trailsl", description = "Set trailing SL (usage: /trailsl <id> <%)>" },
                    new { command = "squareoff", description = "Square off a tranche (usage: /squareoff <id>)" },
                    new { command = "squareoffall", description = "Square off all live tranches" },
                    new { command = "profitprotect", description = "Apply profit protection (usage: /profitprotect <threshold>)" }
                };

                var payload = new { commands = commands };

                var content = new StringContent(
                    JsonConvert.SerializeObject(payload),
                    Encoding.UTF8,
                    "application/json");

                var response = await _httpClient.PostAsync(
                    $"{TELEGRAM_API_BASE}{_settings.Token}/setMyCommands",
                    content);

                if (response.IsSuccessStatusCode)
                {
                    Logger.Info("[TelegramAlertService] Bot commands menu updated successfully");
                    return true;
                }

                var errorBody = await response.Content.ReadAsStringAsync();
                Logger.Warn($"[TelegramAlertService] setMyCommands failed: {response.StatusCode} - {errorBody}");
                return false;
            }
            catch (Exception ex)
            {
                Logger.Warn($"[TelegramAlertService] setMyCommands exception: {ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// Send a text message (sendMessage API)
        /// </summary>
        private async Task<bool> SendMessageAsync(string chatId, string text)
        {
            try
            {
                var payload = new
                {
                    chat_id = chatId,
                    text = text,
                    parse_mode = "HTML"
                };

                var content = new StringContent(
                    JsonConvert.SerializeObject(payload),
                    Encoding.UTF8,
                    "application/json");

                var response = await _httpClient.PostAsync(
                    $"{TELEGRAM_API_BASE}{_settings.Token}/sendMessage",
                    content);

                if (response.IsSuccessStatusCode)
                {
                    return true;
                }

                var errorBody = await response.Content.ReadAsStringAsync();
                Logger.Error($"[TelegramAlertService] sendMessage failed: {response.StatusCode} - {errorBody}");
                return false;
            }
            catch (Exception ex)
            {
                Logger.Error($"[TelegramAlertService] sendMessage exception: {ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// Get updates (long polling)
        /// </summary>
        private async Task<JArray> GetUpdatesAsync(CancellationToken ct)
        {
            try
            {
                var url = $"{TELEGRAM_API_BASE}{_settings.Token}/getUpdates?timeout={POLLING_TIMEOUT_SECONDS}&offset={_lastUpdateId + 1}";
                var response = await _httpClient.GetStringAsync(url);
                var json = JObject.Parse(response);

                if (json.Value<bool>("ok"))
                {
                    return json["result"] as JArray;
                }

                Logger.Error($"[TelegramAlertService] getUpdates failed: {json}");
                return null;
            }
            catch (TaskCanceledException)
            {
                // Normal cancellation
                return null;
            }
            catch (Exception ex)
            {
                Logger.Error($"[TelegramAlertService] getUpdates exception: {ex.Message}");
                return null;
            }
        }

        #endregion

        #region Polling

        /// <summary>
        /// Start long polling for incoming commands
        /// </summary>
        private void StartPolling()
        {
            _pollingCts = new CancellationTokenSource();
            _pollingTask = Task.Run(async () =>
            {
                // Clear any existing webhook/polling connection to avoid 409 Conflict
                await ClearExistingConnectionAsync();
                await PollForUpdatesAsync(_pollingCts.Token);
            });
            Logger.Info("[TelegramAlertService] Started polling for commands");
        }

        /// <summary>
        /// Clear any existing webhook or polling connection to prevent 409 Conflict errors.
        /// This is necessary when another instance may still be polling or a webhook is set.
        /// </summary>
        private async Task ClearExistingConnectionAsync()
        {
            try
            {
                // Call deleteWebhook with drop_pending_updates to clear any existing connection
                var payload = new { drop_pending_updates = true };
                var content = new StringContent(
                    JsonConvert.SerializeObject(payload),
                    Encoding.UTF8,
                    "application/json");

                var response = await _httpClient.PostAsync(
                    $"{TELEGRAM_API_BASE}{_settings.Token}/deleteWebhook",
                    content);

                if (response.IsSuccessStatusCode)
                {
                    Logger.Info("[TelegramAlertService] Cleared existing webhook/polling connection");
                }
                else
                {
                    var errorBody = await response.Content.ReadAsStringAsync();
                    Logger.Warn($"[TelegramAlertService] deleteWebhook returned: {response.StatusCode} - {errorBody}");
                }

                // Small delay to ensure Telegram API registers the change
                await Task.Delay(500);
            }
            catch (Exception ex)
            {
                Logger.Warn($"[TelegramAlertService] Failed to clear existing connection: {ex.Message}");
            }
        }

        /// <summary>
        /// Poll for updates continuously
        /// </summary>
        private async Task PollForUpdatesAsync(CancellationToken ct)
        {
            while (!ct.IsCancellationRequested)
            {
                try
                {
                    var updates = await GetUpdatesAsync(ct);
                    if (updates != null)
                    {
                        foreach (var update in updates)
                        {
                            await ProcessUpdateAsync(update as JObject);
                        }
                    }
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch (Exception ex)
                {
                    Logger.Error($"[TelegramAlertService] Polling error: {ex.Message}");
                    await Task.Delay(5000, ct); // Wait before retry
                }
            }

            Logger.Info("[TelegramAlertService] Polling stopped");
        }

        /// <summary>
        /// Process a single update from Telegram
        /// </summary>
        private async Task ProcessUpdateAsync(JObject update)
        {
            if (update == null) return;

            try
            {
                _lastUpdateId = update.Value<long>("update_id");

                var message = update["message"] as JObject;
                if (message == null) return;

                var text = message.Value<string>("text");
                if (string.IsNullOrEmpty(text)) return;

                var chat = message["chat"] as JObject;
                var chatId = chat?.Value<long>("id") ?? 0;
                var messageId = message.Value<int>("message_id");

                // Only process commands (messages starting with /)
                if (!text.StartsWith("/")) return;

                // Parse command
                var parts = text.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
                var command = parts[0].ToLowerInvariant();

                // Remove @botname suffix if present
                if (command.Contains("@"))
                {
                    command = command.Split('@')[0];
                }

                var args = parts.Length > 1 ? new string[parts.Length - 1] : Array.Empty<string>();
                if (args.Length > 0) Array.Copy(parts, 1, args, 0, args.Length);

                Logger.Info($"[TelegramAlertService] Received command: {command} from chat {chatId}");

                // Create command object and publish to stream
                var cmd = new TelegramCommand
                {
                    Command = command,
                    Arguments = args,
                    ChatId = chatId,
                    MessageId = messageId
                };

                _commandSubject.OnNext(cmd);

                // Handle built-in commands
                await HandleBuiltInCommandAsync(cmd);
            }
            catch (Exception ex)
            {
                Logger.Error($"[TelegramAlertService] Error processing update: {ex.Message}");
            }
        }

        #endregion

        #region Alert Sending

        /// <summary>
        /// Queue an alert to be sent to Telegram
        /// </summary>
        public void SendAlert(TelegramAlert alert)
        {
            if (!IsReady) return;

            // Suppress alerts during simulation mode to avoid flooding the channel
            if (SimulationTimeHelper.IsSimulationActive)
            {
                Logger.Debug($"[TelegramAlertService] Alert suppressed (simulation mode): {alert.Message}");
                return;
            }

            // Check if this alert type should be sent based on settings
            if (!ShouldSendAlertType(alert.Type)) return;

            _alertSubject.OnNext(alert);
        }

        /// <summary>
        /// Queue an alert to be sent to Telegram (async version)
        /// </summary>
        public async Task SendAlertAsync(TelegramAlert alert)
        {
            if (!IsReady)
            {
                Logger.Debug($"[TelegramAlertService] Not ready, skipping alert: {alert.Message}");
                return;
            }

            // Suppress alerts during simulation mode to avoid flooding the channel
            if (SimulationTimeHelper.IsSimulationActive)
            {
                Logger.Debug($"[TelegramAlertService] Alert suppressed (simulation mode): {alert.Message}");
                return;
            }

            if (!ShouldSendAlertType(alert.Type)) return;

            await ProcessAlertAsync(alert);
        }

        /// <summary>
        /// Send a simple text message
        /// </summary>
        public void SendMessage(string message, TelegramAlertType type = TelegramAlertType.Info)
        {
            SendAlert(new TelegramAlert
            {
                Type = type,
                Message = message
            });
        }

        /// <summary>
        /// Process and send an alert to Telegram
        /// </summary>
        private async Task ProcessAlertAsync(TelegramAlert alert)
        {
            try
            {
                await _sendSemaphore.WaitAsync();
                try
                {
                    var success = await SendMessageAsync(_settings.ChatId, alert.FormattedMessage);
                    if (success)
                    {
                        Logger.Debug($"[TelegramAlertService] Sent: {alert.FormattedMessage}");
                    }

                    // Rate limiting
                    await Task.Delay(MIN_MESSAGE_INTERVAL_MS);
                }
                finally
                {
                    _sendSemaphore.Release();
                }
            }
            catch (Exception ex)
            {
                Logger.Error($"[TelegramAlertService] Failed to send alert: {ex.Message}");
            }
        }

        /// <summary>
        /// Check if an alert type should be sent based on settings
        /// </summary>
        private bool ShouldSendAlertType(TelegramAlertType type)
        {
            switch (type)
            {
                case TelegramAlertType.Startup:
                case TelegramAlertType.TokenValidation:
                case TelegramAlertType.OptionChain:
                case TelegramAlertType.TBSModule:
                    return _settings.SendStartupAlerts;

                case TelegramAlertType.TrancheEntry:
                case TelegramAlertType.TrancheExit:
                case TelegramAlertType.TargetHit:
                    return _settings.SendTrancheAlerts;

                case TelegramAlertType.StoplossTriggered:
                case TelegramAlertType.ProfitProtection:
                    return _settings.SendStoplossAlerts;

                case TelegramAlertType.Error:
                case TelegramAlertType.Info:
                default:
                    return true;
            }
        }

        #endregion

        #region Convenience Alert Methods

        public void SendStartupAlert(string message)
        {
            SendAlert(new TelegramAlert { Type = TelegramAlertType.Startup, Message = message });
        }

        public void SendTokenValidationAlert(bool success, string details = null)
        {
            var message = success
                ? "Zerodha Token Validation: SUCCESS"
                : $"Zerodha Token Validation: FAILED{(details != null ? $" - {details}" : "")}";

            SendAlert(new TelegramAlert
            {
                Type = TelegramAlertType.TokenValidation,
                Message = message,
                IsUrgent = !success
            });
        }

        public void SendOptionChainAlert(string underlying, int dte, double atmStrike)
        {
            SendAlert(new TelegramAlert
            {
                Type = TelegramAlertType.OptionChain,
                Message = $"Option Chain Generated - {underlying}, DTE: {dte}, ATM: {atmStrike:F0}"
            });
        }

        public void SendTBSModuleLoadedAlert(string underlying, int dte, int trancheCount)
        {
            SendAlert(new TelegramAlert
            {
                Type = TelegramAlertType.TBSModule,
                Message = $"TBS Module loaded - Underlying: {underlying}, DTE: {dte}, Tranches: {trancheCount}"
            });
        }

        public void SendTrancheEntryAlert(int trancheId, decimal strike, decimal cePremium, decimal pePremium)
        {
            SendAlert(new TelegramAlert
            {
                Type = TelegramAlertType.TrancheEntry,
                Message = $"Tranche #{trancheId} ENTERED at strike {strike:F0} - CE: {cePremium:F2}, PE: {pePremium:F2}"
            });
        }

        public void SendTrancheExitAlert(int trancheId, decimal pnl, string reason)
        {
            var pnlText = pnl >= 0 ? $"+{pnl:F2}" : $"{pnl:F2}";
            SendAlert(new TelegramAlert
            {
                Type = TelegramAlertType.TrancheExit,
                Message = $"Tranche #{trancheId} EXITED - P&L: {pnlText}, Reason: {reason}"
            });
        }

        public void SendStoplossAlert(int trancheId, string legType, decimal triggerPrice)
        {
            SendAlert(new TelegramAlert
            {
                Type = TelegramAlertType.StoplossTriggered,
                Message = $"🚨 SL TRIGGERED - Tranche #{trancheId} {legType} @ {triggerPrice:F2}",
                IsUrgent = true
            });
        }

        public void SendCombinedSLAlert(int trancheId, decimal premium)
        {
            SendAlert(new TelegramAlert
            {
                Type = TelegramAlertType.StoplossTriggered,
                Message = $"🚨 COMBINED SL TRIGGERED - Tranche #{trancheId} @ premium {premium:F2}",
                IsUrgent = true
            });
        }

        public void SendTargetHitAlert(int trancheId, decimal pnl)
        {
            SendAlert(new TelegramAlert
            {
                Type = TelegramAlertType.TargetHit,
                Message = $"🎯 TARGET HIT - Tranche #{trancheId} P&L: +{pnl:F2}"
            });
        }

        public void SendProfitProtectionAlert(int trancheId, string action)
        {
            SendAlert(new TelegramAlert
            {
                Type = TelegramAlertType.ProfitProtection,
                Message = $"🛡️ Profit Protection - Tranche #{trancheId}: {action}"
            });
        }

        public void SendStoxxoAlert(int trancheId, string portfolioName, string status)
        {
            SendAlert(new TelegramAlert
            {
                Type = TelegramAlertType.Info,
                Message = $"Stoxxo: Tranche #{trancheId} Portfolio '{portfolioName}' - {status}"
            });
        }

        #endregion

        #region Command Handling

        /// <summary>
        /// Handle built-in bot commands
        /// </summary>
        private async Task HandleBuiltInCommandAsync(TelegramCommand cmd)
        {
            string response = null;

            switch (cmd.Command)
            {
                case "/help":
                    response = GetHelpText();
                    break;

                case "/status":
                    response = GetStatusText();
                    break;

                case "/pnl":
                    response = GetPnLSummary();
                    break;

                case "/trailsl":
                    response = HandleTrailSLCommand(cmd.Arguments);
                    break;

                case "/squareoff":
                    response = HandleSquareOffCommand(cmd.Arguments);
                    break;

                case "/squareoffall":
                    response = HandleSquareOffAllCommand();
                    break;

                case "/profitprotect":
                    response = HandleProfitProtectCommand(cmd.Arguments);
                    break;

                default:
                    // Unknown command - ignore
                    break;
            }

            if (!string.IsNullOrEmpty(response))
            {
                await SendMessageAsync(cmd.ChatId.ToString(), response);
            }
        }

        private string GetHelpText()
        {
            return @"<b>Available Commands:</b>

/help - Show this help message
/status - Show current TBS status
/pnl - Show P&L summary

<b>Trading Commands:</b>
/trailsl &lt;tranche_id&gt; &lt;percent&gt; - Set trailing SL
/squareoff &lt;tranche_id&gt; - Square off a tranche
/squareoffall - Square off all live tranches
/profitprotect &lt;threshold&gt; - Apply profit protection";
        }

        private string GetStatusText()
        {
            try
            {
                var tbs = TBSExecutionService.Instance;
                var now = DateTime.Now;

                return $@"<b>TBS Status</b> ({now:HH:mm:ss})
Live: {tbs.LiveCount}
Monitoring: {tbs.MonitoringCount}
Total P&L: {tbs.TotalPnL:F2}
Option Chain Ready: {tbs.IsOptionChainReady}";
            }
            catch (Exception ex)
            {
                return $"Error getting status: {ex.Message}";
            }
        }

        private string GetPnLSummary()
        {
            try
            {
                var tbs = TBSExecutionService.Instance;
                var summary = $"<b>Total P&L:</b> {tbs.TotalPnL:F2}\n\n";

                foreach (var state in tbs.ExecutionStates)
                {
                    if (state.Status == TBSExecutionStatus.Live || state.Status == TBSExecutionStatus.SquaredOff)
                    {
                        var pnlText = state.CombinedPnL >= 0 ? $"+{state.CombinedPnL:F2}" : $"{state.CombinedPnL:F2}";
                        summary += $"Tranche #{state.TrancheId}: {pnlText} [{state.Status}]\n";
                    }
                }

                return summary.TrimEnd();
            }
            catch (Exception ex)
            {
                return $"Error getting P&L: {ex.Message}";
            }
        }

        private string HandleTrailSLCommand(string[] args)
        {
            if (args.Length < 2)
                return "Usage: /trailsl &lt;tranche_id&gt; &lt;percent&gt;";

            if (!int.TryParse(args[0], out int trancheId))
                return "Invalid tranche ID";

            if (!decimal.TryParse(args[1], out decimal percent))
                return "Invalid percentage";

            try
            {
                var tbs = TBSExecutionService.Instance;
                var state = tbs.ExecutionStates.FirstOrDefault(s => s.TrancheId == trancheId);

                if (state == null)
                    return $"Tranche #{trancheId} not found";

                if (state.Status != TBSExecutionStatus.Live)
                    return $"Tranche #{trancheId} is not live (status: {state.Status})";

                foreach (var leg in state.Legs.Where(l => l.Status == TBSLegStatus.Active))
                {
                    var profitPerUnit = leg.EntryPrice - leg.CurrentPrice;
                    if (profitPerUnit > 0)
                    {
                        var trailAmount = profitPerUnit * (percent / 100);
                        leg.SLPrice = leg.EntryPrice - trailAmount;
                    }
                }

                state.Message = $"Trailing SL applied @ {percent}%";
                return $"✅ Trailing SL applied to Tranche #{trancheId} at {percent}% of profit";
            }
            catch (Exception ex)
            {
                return $"Error: {ex.Message}";
            }
        }

        private string HandleSquareOffCommand(string[] args)
        {
            if (args.Length < 1)
                return "Usage: /squareoff &lt;tranche_id&gt;";

            if (!int.TryParse(args[0], out int trancheId))
                return "Invalid tranche ID";

            try
            {
                var tbs = TBSExecutionService.Instance;
                var state = tbs.ExecutionStates.FirstOrDefault(s => s.TrancheId == trancheId);

                if (state == null)
                    return $"Tranche #{trancheId} not found";

                if (state.Status != TBSExecutionStatus.Live)
                    return $"Tranche #{trancheId} is not live (status: {state.Status})";

                state.Status = TBSExecutionStatus.SquaredOff;
                state.Message = "Manual square off via Telegram";
                state.ExitTime = DateTime.Now;

                foreach (var leg in state.Legs.Where(l => l.Status == TBSLegStatus.Active))
                {
                    leg.ExitPrice = leg.CurrentPrice;
                    leg.ExitTime = DateTime.Now;
                    leg.ExitReason = "Manual Telegram command";
                    leg.Status = TBSLegStatus.Exited;
                }

                return $"✅ Tranche #{trancheId} marked for square off";
            }
            catch (Exception ex)
            {
                return $"Error: {ex.Message}";
            }
        }

        private string HandleSquareOffAllCommand()
        {
            try
            {
                var tbs = TBSExecutionService.Instance;
                int count = 0;

                foreach (var state in tbs.ExecutionStates.Where(s => s.Status == TBSExecutionStatus.Live))
                {
                    state.Status = TBSExecutionStatus.SquaredOff;
                    state.Message = "Manual square off all via Telegram";
                    state.ExitTime = DateTime.Now;

                    foreach (var leg in state.Legs.Where(l => l.Status == TBSLegStatus.Active))
                    {
                        leg.ExitPrice = leg.CurrentPrice;
                        leg.ExitTime = DateTime.Now;
                        leg.ExitReason = "Manual Telegram command (all)";
                        leg.Status = TBSLegStatus.Exited;
                    }

                    count++;
                }

                return count > 0
                    ? $"✅ Marked {count} tranches for square off"
                    : "No live tranches to square off";
            }
            catch (Exception ex)
            {
                return $"Error: {ex.Message}";
            }
        }

        private string HandleProfitProtectCommand(string[] args)
        {
            if (args.Length < 1)
                return "Usage: /profitprotect &lt;threshold&gt;";

            if (!decimal.TryParse(args[0], out decimal threshold))
                return "Invalid threshold";

            try
            {
                var tbs = TBSExecutionService.Instance;

                if (tbs.TotalPnL < threshold)
                    return $"Current P&L ({tbs.TotalPnL:F2}) is below threshold ({threshold:F2})";

                int count = 0;
                foreach (var state in tbs.ExecutionStates.Where(s => s.Status == TBSExecutionStatus.Live))
                {
                    foreach (var leg in state.Legs.Where(l => l.Status == TBSLegStatus.Active))
                    {
                        if (!state.SLToCostApplied)
                        {
                            leg.SLPrice = leg.EntryPrice;
                        }
                    }
                    state.SLToCostApplied = true;
                    state.Message = "Profit protection applied";
                    count++;
                }

                return count > 0
                    ? $"✅ Profit protection applied to {count} live tranches (SL moved to cost)"
                    : "No live tranches for profit protection";
            }
            catch (Exception ex)
            {
                return $"Error: {ex.Message}";
            }
        }

        #endregion

        #region IDisposable

        public void Dispose()
        {
            if (_isDisposed) return;
            _isDisposed = true;

            Logger.Info("[TelegramAlertService] Disposing...");

            _pollingCts?.Cancel();
            try { _pollingTask?.Wait(5000); } catch { }
            _pollingCts?.Dispose();

            _subscriptions.Dispose();
            _alertSubject.Dispose();
            _commandSubject.Dispose();
            _sendSemaphore.Dispose();
            _httpClient?.Dispose();

            Logger.Info("[TelegramAlertService] Disposed");
        }

        #endregion
    }
}
