using System;
using System.Collections.Concurrent;
using System.Linq;
using System.Reactive.Disposables;
using System.Reactive.Linq;
using System.Reactive.Subjects;
using System.Threading;
using System.Threading.Tasks;
using Telegram.Bot;
using Telegram.Bot.Polling;
using Telegram.Bot.Types;
using Telegram.Bot.Types.Enums;
using ZerodhaDatafeedAdapter.Models;
using ZerodhaDatafeedAdapter.Services.Configuration;

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
        public DateTime ReceivedAt { get; set; } = DateTime.Now;
    }

    /// <summary>
    /// Telegram Alert Service - Event-driven Rx-based Telegram integration.
    /// Provides one-way alerts and two-way bot command handling for TBS control.
    /// </summary>
    public class TelegramAlertService : IDisposable
    {
        #region Singleton

        private static readonly Lazy<TelegramAlertService> _instance =
            new Lazy<TelegramAlertService>(() => new TelegramAlertService());
        public static TelegramAlertService Instance => _instance.Value;

        #endregion

        #region Fields

        private TelegramBotClient _botClient;
        private TelegramSettings _settings;
        private CancellationTokenSource _receivingCts;
        private bool _isInitialized = false;
        private bool _isDisposed = false;

        // Rx subjects for event-driven architecture
        private readonly Subject<TelegramAlert> _alertSubject = new Subject<TelegramAlert>();
        private readonly Subject<TelegramCommand> _commandSubject = new Subject<TelegramCommand>();
        private readonly CompositeDisposable _subscriptions = new CompositeDisposable();

        // Message queue for rate limiting (Telegram has limits)
        private readonly ConcurrentQueue<TelegramAlert> _messageQueue = new ConcurrentQueue<TelegramAlert>();
        private readonly SemaphoreSlim _sendSemaphore = new SemaphoreSlim(1, 1);
        private const int MIN_MESSAGE_INTERVAL_MS = 100; // Respect Telegram rate limits

        #endregion

        #region Properties

        /// <summary>
        /// Whether the service is properly initialized and ready
        /// </summary>
        public bool IsReady => _isInitialized && _settings?.IsValid == true && _botClient != null;

        /// <summary>
        /// Stream of incoming bot commands (for TBS control)
        /// </summary>
        public IObservable<TelegramCommand> CommandStream => _commandSubject.AsObservable();

        /// <summary>
        /// Current settings (read-only)
        /// </summary>
        public TelegramSettings Settings => _settings;

        #endregion

        #region Constructor

        private TelegramAlertService()
        {
            Logger.Info("[TelegramAlertService] Instance created");
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

                // Initialize bot client
                _botClient = new TelegramBotClient(_settings.Token);

                // Test connection by getting bot info
                var me = await _botClient.GetMe();
                Logger.Info($"[TelegramAlertService] Bot connected: @{me.Username}");

                // Setup alert processing pipeline
                SetupAlertPipeline();

                // Start receiving commands if enabled
                if (_settings.EnableBotCommands)
                {
                    StartReceivingCommands();
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
        /// Setup the Rx pipeline for processing alerts with rate limiting
        /// </summary>
        private void SetupAlertPipeline()
        {
            // Process alerts with rate limiting to respect Telegram API limits
            var alertSubscription = _alertSubject
                .Subscribe(
                    alert => Task.Run(async () => await ProcessAlertAsync(alert)),
                    ex => Logger.Error($"[TelegramAlertService] Alert pipeline error: {ex.Message}"));

            _subscriptions.Add(alertSubscription);
            Logger.Debug("[TelegramAlertService] Alert pipeline configured");
        }

        /// <summary>
        /// Start receiving bot commands (polling mode)
        /// </summary>
        private void StartReceivingCommands()
        {
            _receivingCts = new CancellationTokenSource();

            var receiverOptions = new ReceiverOptions
            {
                AllowedUpdates = new[] { UpdateType.Message }
            };

            _botClient.StartReceiving(
                updateHandler: HandleUpdateAsync,
                errorHandler: HandlePollingErrorAsync,
                receiverOptions: receiverOptions,
                cancellationToken: _receivingCts.Token);

            Logger.Info("[TelegramAlertService] Started receiving bot commands");
        }

        #endregion

        #region Alert Sending

        /// <summary>
        /// Queue an alert to be sent to Telegram
        /// </summary>
        public void SendAlert(TelegramAlert alert)
        {
            if (!IsReady) return;

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
                    var chatId = new ChatId(long.Parse(_settings.ChatId));
                    await _botClient.SendMessage(
                        chatId: chatId,
                        text: alert.FormattedMessage);

                    Logger.Debug($"[TelegramAlertService] Sent: {alert.FormattedMessage}");

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

        /// <summary>
        /// Send startup alert
        /// </summary>
        public void SendStartupAlert(string message)
        {
            SendAlert(new TelegramAlert
            {
                Type = TelegramAlertType.Startup,
                Message = message
            });
        }

        /// <summary>
        /// Send token validation result alert
        /// </summary>
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

        /// <summary>
        /// Send option chain generated alert
        /// </summary>
        public void SendOptionChainAlert(string underlying, int dte, double atmStrike)
        {
            SendAlert(new TelegramAlert
            {
                Type = TelegramAlertType.OptionChain,
                Message = $"Option Chain Generated - {underlying}, DTE: {dte}, ATM: {atmStrike:F0}"
            });
        }

        /// <summary>
        /// Send TBS module loaded alert
        /// </summary>
        public void SendTBSModuleLoadedAlert(string underlying, int dte, int trancheCount)
        {
            SendAlert(new TelegramAlert
            {
                Type = TelegramAlertType.TBSModule,
                Message = $"TBS Module loaded - Underlying: {underlying}, DTE: {dte}, Tranches: {trancheCount}"
            });
        }

        /// <summary>
        /// Send tranche entry alert
        /// </summary>
        public void SendTrancheEntryAlert(int trancheId, decimal strike, decimal cePremium, decimal pePremium)
        {
            SendAlert(new TelegramAlert
            {
                Type = TelegramAlertType.TrancheEntry,
                Message = $"Tranche #{trancheId} ENTERED at strike {strike:F0} - CE: {cePremium:F2}, PE: {pePremium:F2}"
            });
        }

        /// <summary>
        /// Send tranche exit alert
        /// </summary>
        public void SendTrancheExitAlert(int trancheId, decimal pnl, string reason)
        {
            var pnlText = pnl >= 0 ? $"+{pnl:F2}" : $"{pnl:F2}";
            SendAlert(new TelegramAlert
            {
                Type = TelegramAlertType.TrancheExit,
                Message = $"Tranche #{trancheId} EXITED - P&L: {pnlText}, Reason: {reason}"
            });
        }

        /// <summary>
        /// Send stoploss triggered alert
        /// </summary>
        public void SendStoplossAlert(int trancheId, string legType, decimal triggerPrice)
        {
            SendAlert(new TelegramAlert
            {
                Type = TelegramAlertType.StoplossTriggered,
                Message = $"SL TRIGGERED - Tranche #{trancheId} {legType} @ {triggerPrice:F2}",
                IsUrgent = true
            });
        }

        /// <summary>
        /// Send combined SL triggered alert
        /// </summary>
        public void SendCombinedSLAlert(int trancheId, decimal premium)
        {
            SendAlert(new TelegramAlert
            {
                Type = TelegramAlertType.StoplossTriggered,
                Message = $"COMBINED SL TRIGGERED - Tranche #{trancheId} @ premium {premium:F2}",
                IsUrgent = true
            });
        }

        /// <summary>
        /// Send target hit alert
        /// </summary>
        public void SendTargetHitAlert(int trancheId, decimal pnl)
        {
            SendAlert(new TelegramAlert
            {
                Type = TelegramAlertType.TargetHit,
                Message = $"TARGET HIT - Tranche #{trancheId} P&L: +{pnl:F2}"
            });
        }

        /// <summary>
        /// Send profit protection applied alert
        /// </summary>
        public void SendProfitProtectionAlert(int trancheId, string action)
        {
            SendAlert(new TelegramAlert
            {
                Type = TelegramAlertType.ProfitProtection,
                Message = $"Profit Protection - Tranche #{trancheId}: {action}"
            });
        }

        /// <summary>
        /// Send Stoxxo order status alert
        /// </summary>
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
        /// Handle incoming Telegram updates (commands)
        /// </summary>
        private async Task HandleUpdateAsync(ITelegramBotClient botClient, Update update, CancellationToken cancellationToken)
        {
            try
            {
                if (update.Message?.Text == null) return;

                var messageText = update.Message.Text.Trim();
                var chatId = update.Message.Chat.Id;

                // Only process commands (messages starting with /)
                if (!messageText.StartsWith("/")) return;

                // Parse command
                var parts = messageText.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
                var command = parts[0].ToLowerInvariant();
                var args = parts.Length > 1 ? new string[parts.Length - 1] : Array.Empty<string>();
                if (args.Length > 0) Array.Copy(parts, 1, args, 0, args.Length);

                Logger.Info($"[TelegramAlertService] Received command: {command} from chat {chatId}");

                // Create command object and publish to stream
                var cmd = new TelegramCommand
                {
                    Command = command,
                    Arguments = args,
                    ChatId = chatId
                };

                _commandSubject.OnNext(cmd);

                // Handle built-in commands
                await HandleBuiltInCommandAsync(cmd, cancellationToken);
            }
            catch (Exception ex)
            {
                Logger.Error($"[TelegramAlertService] Error handling update: {ex.Message}");
            }
        }

        /// <summary>
        /// Handle built-in bot commands
        /// </summary>
        private async Task HandleBuiltInCommandAsync(TelegramCommand cmd, CancellationToken cancellationToken)
        {
            var chatId = new ChatId(cmd.ChatId);
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
                    // Unknown command - let external handlers deal with it via CommandStream
                    break;
            }

            if (!string.IsNullOrEmpty(response))
            {
                try
                {
                    await _botClient.SendMessage(
                        chatId: chatId,
                        text: response,
                        cancellationToken: cancellationToken);
                }
                catch (Exception ex)
                {
                    Logger.Error($"[TelegramAlertService] Failed to send command response: {ex.Message}");
                }
            }
        }

        /// <summary>
        /// Get help text for available commands
        /// </summary>
        private string GetHelpText()
        {
            return @"Available Commands:

/help - Show this help message
/status - Show current TBS status
/pnl - Show P&L summary

Trading Commands:
/trailsl <tranche_id> <percent> - Set trailing SL for a tranche
/squareoff <tranche_id> - Square off a specific tranche
/squareoffall - Square off all live tranches
/profitprotect <threshold> - Apply profit protection when P&L exceeds threshold";
        }

        /// <summary>
        /// Get current status text
        /// </summary>
        private string GetStatusText()
        {
            try
            {
                var tbs = TBSExecutionService.Instance;
                var now = DateTime.Now;

                return $@"TBS Status ({now:HH:mm:ss})
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

        /// <summary>
        /// Get P&L summary
        /// </summary>
        private string GetPnLSummary()
        {
            try
            {
                var tbs = TBSExecutionService.Instance;
                var summary = $"Total P&L: {tbs.TotalPnL:F2}\n\n";

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

        /// <summary>
        /// Handle /trailsl command
        /// </summary>
        private string HandleTrailSLCommand(string[] args)
        {
            if (args.Length < 2)
                return "Usage: /trailsl <tranche_id> <percent>\nExample: /trailsl 1 50 (moves SL to 50% of current profit)";

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

                // Apply trailing SL to each active leg
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
                return $"Trailing SL applied to Tranche #{trancheId} at {percent}% of profit";
            }
            catch (Exception ex)
            {
                return $"Error: {ex.Message}";
            }
        }

        /// <summary>
        /// Handle /squareoff command
        /// </summary>
        private string HandleSquareOffCommand(string[] args)
        {
            if (args.Length < 1)
                return "Usage: /squareoff <tranche_id>";

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

                // Mark for square off
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

                return $"Tranche #{trancheId} marked for square off";
            }
            catch (Exception ex)
            {
                return $"Error: {ex.Message}";
            }
        }

        /// <summary>
        /// Handle /squareoffall command
        /// </summary>
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
                    ? $"Marked {count} tranches for square off"
                    : "No live tranches to square off";
            }
            catch (Exception ex)
            {
                return $"Error: {ex.Message}";
            }
        }

        /// <summary>
        /// Handle /profitprotect command - Apply global trailing SL when P&L exceeds threshold
        /// </summary>
        private string HandleProfitProtectCommand(string[] args)
        {
            if (args.Length < 1)
                return "Usage: /profitprotect <threshold>\nExample: /profitprotect 1000 (trail SL when total P&L > 1000)";

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
                        // Move SL to cost
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
                    ? $"Profit protection applied to {count} live tranches (SL moved to cost)"
                    : "No live tranches for profit protection";
            }
            catch (Exception ex)
            {
                return $"Error: {ex.Message}";
            }
        }

        /// <summary>
        /// Handle polling errors
        /// </summary>
        private Task HandlePollingErrorAsync(ITelegramBotClient botClient, Exception exception, CancellationToken cancellationToken)
        {
            Logger.Error($"[TelegramAlertService] Polling error: {exception.Message}");
            return Task.CompletedTask;
        }

        #endregion

        #region IDisposable

        public void Dispose()
        {
            if (_isDisposed) return;
            _isDisposed = true;

            Logger.Info("[TelegramAlertService] Disposing...");

            _receivingCts?.Cancel();
            _receivingCts?.Dispose();

            _subscriptions.Dispose();
            _alertSubject.Dispose();
            _commandSubject.Dispose();
            _sendSemaphore.Dispose();

            Logger.Info("[TelegramAlertService] Disposed");
        }

        #endregion
    }
}
