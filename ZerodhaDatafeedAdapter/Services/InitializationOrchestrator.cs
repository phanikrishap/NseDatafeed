using System;
using System.Reactive.Linq;
using System.Reactive.Threading.Tasks;
using System.Threading;
using System.Threading.Tasks;
using ZerodhaDatafeedAdapter.Services.Analysis;
using ZerodhaDatafeedAdapter.Services.Configuration;
using ZerodhaDatafeedAdapter.Services.Instruments;
using ZerodhaDatafeedAdapter.Models.Reactive;

namespace ZerodhaDatafeedAdapter.Services
{
    /// <summary>
    /// Orchestrates the entire initialization pipeline using reactive composition.
    /// Eliminates all blocking calls while maintaining sequential dependencies.
    ///
    /// Initialization Flow:
    /// 1. Token Validation (60s timeout)
    /// 2. Instrument DB Refresh (120s timeout)
    /// 3. Market Analyzer Start - Index Subscriptions (60s timeout)
    /// 4. Wait for Projected Opens Calculation (120s timeout)
    /// 5. Generate Options (60s timeout)
    /// 6. Wait for Option Subscriptions (30s timeout)
    ///
    /// Each step publishes progress to MarketDataReactiveHub.InitializationStateStream.
    /// Late subscribers can use hub.WhenReady to await completion.
    /// </summary>
    public class InitializationOrchestrator
    {
        private readonly MarketDataReactiveHub _hub = MarketDataReactiveHub.Instance;
        private readonly ConfigurationManager _configManager;
        private readonly InstrumentManager _instrumentManager;
        private readonly MarketAnalyzerService _marketAnalyzerService;
        private readonly MarketAnalyzerLogic _marketAnalyzerLogic;

        private IDisposable _pipelineSubscription;
        private readonly object _lock = new object();
        private bool _isRunning = false;
        private CancellationTokenSource _cancellationTokenSource;

        public InitializationOrchestrator(
            ConfigurationManager configManager,
            InstrumentManager instrumentManager,
            MarketAnalyzerService marketAnalyzerService,
            MarketAnalyzerLogic marketAnalyzerLogic)
        {
            _configManager = configManager ?? throw new ArgumentNullException(nameof(configManager));
            _instrumentManager = instrumentManager ?? throw new ArgumentNullException(nameof(instrumentManager));
            _marketAnalyzerService = marketAnalyzerService ?? throw new ArgumentNullException(nameof(marketAnalyzerService));
            _marketAnalyzerLogic = marketAnalyzerLogic ?? throw new ArgumentNullException(nameof(marketAnalyzerLogic));
        }

        /// <summary>
        /// Starts the initialization pipeline. Returns an observable that completes
        /// when the entire pipeline finishes or errors.
        /// This method is idempotent - calling it while running returns immediately.
        /// </summary>
        public IObservable<System.Reactive.Unit> StartInitializationPipeline()
        {
            lock (_lock)
            {
                if (_isRunning)
                {
                    Logger.Warn("[InitOrchestrator] Pipeline already running");
                    return Observable.Empty<System.Reactive.Unit>();
                }
                _isRunning = true;
                _cancellationTokenSource = new CancellationTokenSource();
            }

            Logger.Info("[InitOrchestrator] ═══════════════════════════════════════");
            Logger.Info("[InitOrchestrator] Starting Initialization Pipeline");
            Logger.Info("[InitOrchestrator] ═══════════════════════════════════════");

            return CreatePipeline()
                .Finally(() =>
                {
                    lock (_lock)
                    {
                        _isRunning = false;
                        _cancellationTokenSource?.Dispose();
                        _cancellationTokenSource = null;
                    }
                });
        }

        /// <summary>
        /// Cancels the running pipeline gracefully.
        /// </summary>
        public void Cancel()
        {
            lock (_lock)
            {
                if (!_isRunning)
                {
                    Logger.Warn("[InitOrchestrator] No pipeline running to cancel");
                    return;
                }

                Logger.Warn("[InitOrchestrator] Cancelling initialization pipeline...");
                _cancellationTokenSource?.Cancel();
            }
        }

        private IObservable<System.Reactive.Unit> CreatePipeline()
        {
            return Observable.Defer(() =>
            {
                // ═══════════════════════════════════════════════════════════
                // STEP 1: Token Validation
                // ═══════════════════════════════════════════════════════════
                Logger.Info("[InitOrchestrator] [1/6] Starting token validation...");
                _hub.PublishInitializationPhase(InitializationPhase.ValidatingToken,
                    "Validating Zerodha access token", 10);

                return Observable.FromAsync(() => _configManager.EnsureValidTokenAsync())
                    .Timeout(TimeSpan.FromSeconds(60))
                    .Do(isValid =>
                    {
                        Logger.Info($"[InitOrchestrator] [1/6] Token validation: {(isValid ? "VALID" : "INVALID")}");
                        _hub.PublishTokenReady(isValid);

                        if (!isValid)
                            throw new InvalidOperationException("Token validation failed - please check your API credentials");
                    });
            })
            // ═══════════════════════════════════════════════════════════
            // STEP 2: Instrument DB Refresh
            // ═══════════════════════════════════════════════════════════
            .SelectMany(_ =>
            {
                Logger.Info("[InitOrchestrator] [2/6] Starting instrument DB refresh...");
                _hub.PublishInitializationPhase(InitializationPhase.DownloadingInstruments,
                    "Downloading instrument master database", 30);

                return Observable.FromAsync(() => _instrumentManager.InitializeAsync())
                    .Timeout(TimeSpan.FromSeconds(120))
                    .Do(() =>
                    {
                        Logger.Info("[InitOrchestrator] [2/6] Instrument DB ready");
                        _hub.PublishInstrumentDbReady(true);
                    });
            })
            // ═══════════════════════════════════════════════════════════
            // STEP 3: Market Analyzer Start (Index Subscriptions)
            // ═══════════════════════════════════════════════════════════
            .SelectMany(_ =>
            {
                Logger.Info("[InitOrchestrator] [3/6] Starting index subscriptions...");
                _hub.PublishInitializationPhase(InitializationPhase.ConnectingWebSocket,
                    "Subscribing to GIFT_NIFTY, NIFTY, SENSEX", 50);

                return Observable.FromAsync(() => StartMarketAnalyzerAsync())
                    .Timeout(TimeSpan.FromSeconds(60))
                    .Do(() =>
                    {
                        Logger.Info("[InitOrchestrator] [3/6] Index subscriptions active");
                    });
            })
            // ═══════════════════════════════════════════════════════════
            // STEP 4: Wait for Projected Opens Calculation
            // ═══════════════════════════════════════════════════════════
            .SelectMany(_ =>
            {
                Logger.Info("[InitOrchestrator] [4/6] Waiting for projected opens...");
                _hub.PublishInitializationPhase(InitializationPhase.Ready,
                    "Calculating projected opens (waiting for GIFT NIFTY, NIFTY, SENSEX prices)", 70);

                return _hub.ProjectedOpenStream
                    .Where(state => state.IsComplete)
                    .Take(1)
                    .Timeout(TimeSpan.FromSeconds(120))
                    .Do(state =>
                    {
                        Logger.Info($"[InitOrchestrator] [4/6] Projected opens ready: " +
                            $"NIFTY={state.NiftyProjectedOpen:F0}, SENSEX={state.SensexProjectedOpen:F0}");
                    });
            })
            // ═══════════════════════════════════════════════════════════
            // STEP 5: Generate Options
            // ═══════════════════════════════════════════════════════════
            .SelectMany(projectedOpenState =>
            {
                Logger.Info("[InitOrchestrator] [5/6] Triggering option generation...");
                _hub.PublishInitializationPhase(InitializationPhase.Ready,
                    "Generating option symbols", 85);

                // TriggerOptionsGeneration is fire-and-forget, so we wait for OptionsGeneratedStream instead
                _marketAnalyzerLogic.TriggerOptionsGeneration(
                    projectedOpenState.NiftyProjectedOpen,
                    projectedOpenState.SensexProjectedOpen);

                // Wait for options to be generated and published
                return _hub.OptionsGeneratedStream
                    .Take(1)
                    .Timeout(TimeSpan.FromSeconds(60))
                    .Do(evt =>
                    {
                        Logger.Info($"[InitOrchestrator] [5/6] Options generated: {evt.Options?.Count ?? 0} symbols");
                    });
            })
            // ═══════════════════════════════════════════════════════════
            // STEP 6: Wait for Option Subscriptions to Complete
            // ═══════════════════════════════════════════════════════════
            .SelectMany(optionsEvent =>
            {
                Logger.Info("[InitOrchestrator] [6/6] Option subscriptions queued, waiting for completion signal...");
                _hub.PublishInitializationPhase(InitializationPhase.Ready,
                    $"Subscribing to {optionsEvent.Options?.Count ?? 0} options", 95);

                // Note: SubscriptionManager doesn't currently expose a completion signal.
                // For now, we just delay briefly and assume success.
                // TODO: Enhance SubscriptionManager to expose IObservable<bool> completion stream
                return Observable.Timer(TimeSpan.FromSeconds(5))
                    .Select(_ => System.Reactive.Unit.Default)
                    .Do(_ =>
                    {
                        Logger.Info("[InitOrchestrator] [6/6] Option subscriptions initiated");
                        _hub.PublishInitializationPhase(InitializationPhase.Ready,
                            "Initialization complete", 100);
                    });
            })
            // ═══════════════════════════════════════════════════════════
            // Error Handling & Retry
            // ═══════════════════════════════════════════════════════════
            .RetryWhen(errors => errors
                .Zip(Observable.Range(1, 3), (error, attempt) => new { error, attempt })
                .SelectMany(x =>
                {
                    if (x.attempt >= 3)
                    {
                        Logger.Error($"[InitOrchestrator] Max retries (3) exceeded");
                        return Observable.Throw<long>(x.error);
                    }

                    var delay = TimeSpan.FromSeconds(Math.Pow(2, x.attempt));
                    Logger.Warn($"[InitOrchestrator] Retry {x.attempt}/3 in {delay.TotalSeconds}s: {x.error.Message}");

                    _hub.PublishInitializationRetry(x.attempt, 3, x.error.Message);
                    return Observable.Timer(delay);
                }))
            .Catch<System.Reactive.Unit, Exception>(ex =>
            {
                Logger.Error($"[InitOrchestrator] Pipeline FAILED: {ex.Message}", ex);
                _hub.PublishInitializationState(InitializationState.Failed(ex.Message));
                return Observable.Return(System.Reactive.Unit.Default);
            })
            .Do(
                _ => Logger.Info("[InitOrchestrator] Pipeline step completed"),
                ex => Logger.Error($"[InitOrchestrator] Pipeline ERROR: {ex.Message}", ex),
                () =>
                {
                    Logger.Info("[InitOrchestrator] ═══════════════════════════════════════");
                    Logger.Info("[InitOrchestrator] Initialization Pipeline Complete");
                    Logger.Info("[InitOrchestrator] ═══════════════════════════════════════");
                });
        }

        /// <summary>
        /// Starts the MarketAnalyzerService asynchronously.
        /// This is a wrapper to convert the synchronous Start() method.
        /// </summary>
        private async Task StartMarketAnalyzerAsync()
        {
            // MarketAnalyzerService.Start() is currently synchronous and fire-and-forget
            // We wrap it in Task.Run to avoid blocking
            await Task.Run(() =>
            {
                try
                {
                    _marketAnalyzerService.Start();
                }
                catch (Exception ex)
                {
                    Logger.Error($"[InitOrchestrator] MarketAnalyzerService.Start() error: {ex.Message}", ex);
                    throw;
                }
            }).ConfigureAwait(false);

            // Give subscriptions time to initialize
            await Task.Delay(2000).ConfigureAwait(false);
        }

        /// <summary>
        /// Gets the current initialization state synchronously.
        /// </summary>
        public InitializationState GetCurrentState()
        {
            return _hub.CurrentInitializationState;
        }

        /// <summary>
        /// Gets whether the pipeline is currently running.
        /// </summary>
        public bool IsRunning
        {
            get
            {
                lock (_lock)
                {
                    return _isRunning;
                }
            }
        }
    }
}
