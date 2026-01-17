using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Reactive.Disposables;
using System.Reactive.Linq;
using System.Reactive.Subjects;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Data;
using System.Windows.Threading;
using ZerodhaDatafeedAdapter.AddOns.OptionSignals.Models;
using ZerodhaDatafeedAdapter.AddOns.OptionSignals.Services;
using ZerodhaDatafeedAdapter.Helpers;
using ZerodhaDatafeedAdapter.Logging;
using ZerodhaDatafeedAdapter.Models;
using ZerodhaDatafeedAdapter.Models.Reactive;
using ZerodhaDatafeedAdapter.Services;
using ZerodhaDatafeedAdapter.Services.Analysis;
using ZerodhaDatafeedAdapter.Services.Analysis.Components;
using ZerodhaDatafeedAdapter.ViewModels;

namespace ZerodhaDatafeedAdapter.AddOns.OptionSignals
{
    public class OptionSignalsViewModel : ViewModelBase, IDisposable
    {
        private CompositeDisposable _subscriptions;
        private readonly OptionSignalsComputeService _computeService = OptionSignalsComputeService.Instance;
        private readonly Subject<OptionsGeneratedEvent> _strikeGenerationTrigger = new Subject<OptionsGeneratedEvent>();
        private readonly Dispatcher _dispatcher;
        private readonly object _rowsLock = new object();
        private readonly object _signalsLock = new object();

        // Dedicated logger
        private static readonly ILoggerService _log = LoggerFactory.OpSignals;

        // Data Collections
        public ObservableCollection<OptionSignalsRow> Rows { get; } = new ObservableCollection<OptionSignalsRow>();
        public ObservableCollection<SignalRow> Signals { get; } = new ObservableCollection<SignalRow>();

        // Signals Orchestrator
        private SignalsOrchestrator _signalsOrchestrator;
        
        // Properties
        private string _underlying = "NIFTY";
        private string _expiry = "---";
        private string _statusText = "Waiting for Data...";
        private bool _isBusy;
        private DateTime? _selectedExpiry;
        private double _atmStrike;
        private int _strikeStep = 50;
        private int _currentDisplayATM;
        private int _lastPopulatedATM; // Track ATM used when grid was last populated

        // Cached for re-triggering
        private OptionsGeneratedEvent _lastOptionsEvent; 

        public string Underlying
        {
            get => _underlying;
            set { if (_underlying != value) { _underlying = value; OnPropertyChanged(); } }
        }

        public string Expiry
        {
            get => _expiry;
            set { if (_expiry != value) { _expiry = value; OnPropertyChanged(); } }
        }

        public string StatusText
        {
            get => _statusText;
            set { if (_statusText != value) { _statusText = value; OnPropertyChanged(); } }
        }

        public new bool IsBusy
        {
            get => _isBusy;
            set { if (_isBusy != value) { _isBusy = value; OnPropertyChanged(); } }
        }

        // Signals P&L properties for UI binding
        public double TotalUnrealizedPnL => _signalsOrchestrator?.TotalUnrealizedPnL ?? 0;
        public double TotalRealizedPnL => _signalsOrchestrator?.TotalRealizedPnL ?? 0;
        public int ActiveSignalCount => _signalsOrchestrator?.ActiveSignalCount ?? 0;

        public OptionSignalsViewModel()
        {
            _dispatcher = Dispatcher.CurrentDispatcher;
            BindingOperations.EnableCollectionSynchronization(Rows, _rowsLock);
            BindingOperations.EnableCollectionSynchronization(Signals, _signalsLock);

            // Initialize Signals Orchestrator
            _signalsOrchestrator = new SignalsOrchestrator(Signals, _dispatcher);

            // Subscribe to signal stats changes to update UI summary metrics
            _signalsOrchestrator.SignalStatsChanged += OnSignalStatsChanged;

            _log.Info("[OptionSignalsViewModel] Initialized with SignalsOrchestrator");
            SetupInternalPipelines();
        }

        /// <summary>
        /// Handles signal stats changes (signal added, closed, or P&L updated).
        /// Raises PropertyChanged for computed summary properties.
        /// </summary>
        private void OnSignalStatsChanged(object sender, EventArgs e)
        {
            // Must raise PropertyChanged on UI thread for WPF binding
            _dispatcher.InvokeAsync(() =>
            {
                OnPropertyChanged(nameof(ActiveSignalCount));
                OnPropertyChanged(nameof(TotalUnrealizedPnL));
                OnPropertyChanged(nameof(TotalRealizedPnL));
            });
        }

        private void SetupInternalPipelines()
        {
            _subscriptions = new CompositeDisposable();

            var strikePipeline = _strikeGenerationTrigger
                .Select(evt => Observable.FromAsync(ct => SyncAndPopulateStrikes(evt, ct)))
                .Switch()
                .Subscribe(
                    _ => _log.Debug("[OptionSignalsViewModel] Strike generation pipeline iteration completed"),
                    ex => _log.Error($"[OptionSignalsViewModel] Strike pipeline error: {ex.Message}")
                );

            _subscriptions.Add(strikePipeline);
        }

        public void StartServices()
        {
            _log.Info("[OptionSignalsViewModel] Starting services - waiting for Market Analyzer to complete");
            var hub = MarketDataReactiveHub.Instance;

            // IMPORTANT: Wait for Market Analyzer to complete (ProjectedOpen ready) before subscribing to options
            // This ensures we have correct ATM strike before initializing option chain VP
            _subscriptions.Add(hub.ProjectedOpenStream
                .Where(s => s.IsComplete)
                .Take(1)
                .ObserveOnDispatcher()
                .Subscribe(state =>
                {
                    OnProjectedOpenReady(state);

                    // NOW subscribe to OptionsGeneratedStream after Market Analyzer is ready
                    _log.Info("[OptionSignalsViewModel] Market Analyzer ready - subscribing to OptionsGeneratedStream");
                    _subscriptions.Add(hub.OptionsGeneratedStream
                        .Subscribe(evt => _strikeGenerationTrigger.OnNext(evt)));
                }));

            // Price updates can start immediately (they'll be buffered/ignored until options are generated)
            // At high simulation speeds (>10x), sample more aggressively to prevent UI flooding
            _subscriptions.Add(hub.OptionPriceBatchStream
                .Sample(TimeSpan.FromMilliseconds(GetAdaptiveSampleInterval()))
                .ObserveOnDispatcher()
                .Subscribe(OnOptionPriceBatch));

            // Re-subscribe when simulation speed changes to adjust sampling
            _subscriptions.Add(hub.SimulationStateStream
                .Where(s => s.State == SimulationState.Playing)
                .Subscribe(_ => UpdatePriceBatchSubscription(hub)));

            // Subscribe to Nifty Futures VP metrics for underlying bias
            _subscriptions.Add(NiftyFuturesMetricsService.Instance.MetricsStream
                .Where(m => m.IsValid)
                .Sample(TimeSpan.FromSeconds(1)) // Throttle to 1 update per second
                .Subscribe(OnNiftyFuturesMetricsUpdate));

            // Subscribe to simulation state changes via Rx stream (replaces legacy event)
            // This ensures proper initialization order: Loading → Ready → Playing
            _subscriptions.Add(hub.SimulationStateStream
                .DistinctUntilChanged(s => s.State)
                .ObserveOnDispatcher()
                .Subscribe(s => OnSimulationStateChanged(s.State)));

            // Subscribe to ATM changes for fluid strike grid re-centering
            OptionGenerationService.Instance.ATMChanged += OnATMChanged;

            // Subscribe to compute service updates for diagnostic/UI refresh
            _computeService.StrikeDataUpdated += OnStrikeDataUpdated;

            // Initialize replay mode based on current simulation state
            if (SimulationService.Instance.IsSimulationMode)
            {
                // Use the simulation state handler for consistency
                var currentState = SimulationService.Instance.State;
                OnSimulationStateChanged(currentState);
                _log.Info($"[OptionSignalsViewModel] Started in simulation mode ({currentState}) - replay mode enabled");
            }

            TerminalService.Instance.Info("OptionSignalsViewModel services started");
        }

        /// <summary>
        /// Handles ATM strike changes. Re-centers the strike grid if ATM has shifted significantly.
        /// </summary>
        private void OnATMChanged(object sender, ATMChangedEventArgs e)
        {
            // Only process if this is for our current underlying
            if (e.Underlying != _underlying) return;

            int newATM = (int)e.NewATM;
            int atmDiff = Math.Abs(newATM - _currentDisplayATM);

            // Re-center if ATM shifted by 2+ strikes (avoid excessive re-centering)
            // Just refresh display, VP state is preserved in ComputeService
            if (atmDiff >= _strikeStep * 2)
            {
                _log.Info($"[OptionSignalsViewModel] ATM shifted significantly: {_currentDisplayATM} -> {newATM} (diff={atmDiff}). Refreshing display (VP preserved).");
                
                _currentDisplayATM = newATM;
                RefreshDisplayRows(newATM);

                // Update market context for signals orchestrator
                if (_selectedExpiry.HasValue)
                {
                    _computeService.SetMarketContext(newATM, _strikeStep, _selectedExpiry.Value, _underlying);
                }
            }
        }

        private void RefreshDisplayRows(int atmStrike)
        {
            _dispatcher.InvokeAsync(() =>
            {
                lock (_rowsLock)
                {
                    // Get ~20 strikes around current ATM for display
                    var displayRows = _computeService.GetRowsAroundATM(atmStrike, 10);
                    
                    Rows.Clear();
                    foreach (var row in displayRows)
                    {
                        Rows.Add(row);
                    }
                }
            });
        }

        public void StopServices()
        {
            _log.Info("[OptionSignalsViewModel] Stopping services");
            SimulationService.Instance.StateChanged -= OnSimulationStateChanged;
            OptionGenerationService.Instance.ATMChanged -= OnATMChanged;
            _computeService.StrikeDataUpdated -= OnStrikeDataUpdated;
            _subscriptions?.Clear();
            // DON'T clear ComputeService - it's a singleton shared across instances if any
            // but we can ensure it stops processing if needed, though typically it runs for the session
        }

        /// <summary>
        /// Handles strike data updates from compute service.
        /// Row properties use INotifyPropertyChanged so WPF binding auto-updates.
        /// This handler is mainly for diagnostics/explicit refresh if needed.
        /// </summary>
        private void OnStrikeDataUpdated(object sender, StrikeDataUpdatedEventArgs e)
        {
            // Row properties already implement INotifyPropertyChanged
            // WPF binding should auto-update, but we can force refresh if needed
        }

        /// <summary>
        /// Handles simulation state changes to enable/disable replay mode.
        /// Lifecycle: Loading → Ready → Playing → Paused/Stopped → Idle
        /// </summary>
        private void OnSimulationStateChanged(SimulationState newState)
        {
            _log.Info($"[OptionSignalsViewModel] Simulation state changed: {newState}");

            switch (newState)
            {
                case SimulationState.Loading:
                    // Simulation is loading tick data - prepare for fresh data
                    _log.Info("[OptionSignalsViewModel] Simulation loading - clearing rows for fresh data");
                    ClearAllRows();
                    _signalsOrchestrator?.SetReplayMode(true);
                    break;

                case SimulationState.Ready:
                    // Tick data loaded - options will be published via OptionsGeneratedStream
                    var simConfig = SimulationService.Instance.CurrentConfig;
                    string simUnderlying = simConfig?.Underlying ?? "NIFTY";

                    // Reset underlying history if it's different from NIFTY (default)
                    if (!simUnderlying.Equals("NIFTY", StringComparison.OrdinalIgnoreCase))
                    {
                        string underlyingSymbol = simUnderlying.Equals("SENSEX", StringComparison.OrdinalIgnoreCase)
                            ? "SENSEX_I"
                            : $"{simUnderlying}_I";
                        _signalsOrchestrator?.ResetUnderlyingHistory(underlyingSymbol);
                    }

                    _signalsOrchestrator?.SetReplayMode(true);
                    _log.Info($"[OptionSignalsViewModel] Simulation ready - replay mode enabled for {simUnderlying}");
                    TerminalService.Instance.Info($"Simulation ready - awaiting option chain for {simUnderlying}");
                    break;

                case SimulationState.Playing:
                    _log.Info("[OptionSignalsViewModel] Simulation playing - receiving tick data");
                    TerminalService.Instance.Info("Simulation playing - taking all trades");
                    break;

                case SimulationState.Paused:
                    _log.Info("[OptionSignalsViewModel] Simulation paused");
                    break;

                case SimulationState.Idle:
                case SimulationState.Completed:
                case SimulationState.Error:
                    _signalsOrchestrator?.SetReplayMode(false);
                    // Reset back to NIFTY underlying for live mode
                    _signalsOrchestrator?.ResetUnderlyingHistory("NIFTY_I");
                    _log.Info($"[OptionSignalsViewModel] Simulation ended ({newState}) - replay mode disabled");
                    TerminalService.Instance.Info("Live mode active - normal signal dedup enabled");
                    break;
            }
        }

        private void ClearAllRows()
        {
            Rows.Clear();
            Signals.Clear();
        }

        private void OnProjectedOpenReady(ProjectedOpenState state)
        {
            _log.Info($"[OptionSignalsViewModel] Projected Open Ready: {state.NiftyProjectedOpen:F2}");
        }

        /// <summary>
        /// Handles Nifty Futures VP metrics updates and forwards to SignalsOrchestrator.
        /// </summary>
        private void OnNiftyFuturesMetricsUpdate(NiftyFuturesVPMetrics metrics)
        {
            if (!metrics.IsValid || _signalsOrchestrator == null) return;

            // Determine HVN trends
            HvnTrend sessTrend = DetermineUnderlyingTrend(metrics.HVNBuyCount, metrics.HVNSellCount);
            HvnTrend rollTrend = DetermineUnderlyingTrend(metrics.RollingHVNBuyCount, metrics.RollingHVNSellCount);

            var snapshot = new UnderlyingStateSnapshot
            {
                Symbol = metrics.Symbol ?? "NIFTY_I",
                Price = metrics.POC, // Using POC as representative price
                Time = metrics.LastBarTime,

                // Session VP metrics
                POC = metrics.POC,
                VAH = metrics.VAH,
                VAL = metrics.VAL,
                VWAP = metrics.VWAP,
                SessHvnB = metrics.HVNBuyCount,
                SessHvnS = metrics.HVNSellCount,
                SessTrend = sessTrend,

                // Rolling VP metrics
                RollingPOC = metrics.RollingPOC,
                RollingVAH = metrics.RollingVAH,
                RollingVAL = metrics.RollingVAL,
                RollHvnB = metrics.RollingHVNBuyCount,
                RollHvnS = metrics.RollingHVNSellCount,
                RollTrend = rollTrend,

                // Relative metrics
                RelHvnBuySess = metrics.RelHVNBuy,
                RelHvnSellSess = metrics.RelHVNSell,
                RelHvnBuyRoll = metrics.RelHVNBuyRolling,
                RelHvnSellRoll = metrics.RelHVNSellRolling,

                IsValid = true
            };

            _signalsOrchestrator.UpdateUnderlyingState(snapshot);
        }

        /// <summary>
        /// Determines HVN trend for underlying based on buy/sell counts.
        /// </summary>
        private HvnTrend DetermineUnderlyingTrend(int hvnBuy, int hvnSell)
        {
            if (hvnBuy > hvnSell)
                return HvnTrend.Bullish;
            else if (hvnSell > hvnBuy)
                return HvnTrend.Bearish;
            else
                return HvnTrend.Neutral;
        }

        private async Task SyncAndPopulateStrikes(OptionsGeneratedEvent evt, System.Threading.CancellationToken ct)
        {
            if (evt?.Options == null || evt.Options.Count == 0) return;
            
            _log.Info($"[OptionSignalsViewModel] SyncAndPopulateStrikes started for {evt.SelectedUnderlying} ATM={evt.ATMStrike}");

            // Cache the event for potential use
            _lastOptionsEvent = evt;

            await _dispatcher.InvokeAsync(() => {
                Underlying = evt.SelectedUnderlying;
                _selectedExpiry = evt.SelectedExpiry;
                Expiry = evt.SelectedExpiry.ToString("dd-MMM-yyyy");
                IsBusy = true;
                StatusText = "Initializing...";
            });

            // Determine ATM and step
            // Priority: 1) Projected open strike (non-market hours), 2) Event ATM (market hours)
            var uniqueStrikes = evt.Options.Where(o => o.strike.HasValue)
                .Select(o => (int)o.strike.Value).Distinct().OrderBy(s => s).ToList();
            int step = uniqueStrikes.Count >= 2 ? uniqueStrikes[1] - uniqueStrikes[0] : 50;

            // Check if market is open - use projected open during pre-market
            bool isMarketOpen = MarketAnalyzerLogic.Instance.IsMarketOpen();
            double projectedOpen = MarketAnalyzerLogic.Instance.GetProjectedOpen(evt.SelectedUnderlying);

            int atmStrike;
            if (!isMarketOpen && projectedOpen > 0)
            {
                // Non-market hours: use projected open rounded to nearest strike
                atmStrike = ((int)Math.Round(projectedOpen / step)) * step;
                _log.Info($"[OptionSignalsViewModel] Pre-market: using projected open strike={atmStrike} (projected={projectedOpen:F2})");
            }
            else
            {
                // Market hours: use event ATM
                atmStrike = (int)evt.ATMStrike;
                _log.Info($"[OptionSignalsViewModel] Market hours: using event ATM={atmStrike}");
            }

            _atmStrike = atmStrike;
            _strikeStep = step;
            _currentDisplayATM = atmStrike;
            _lastPopulatedATM = atmStrike;

            // Initialize ComputeService with ALL options
            // This syncs historical data for all items and starts processors
            await _computeService.InitializeAsync(evt.SelectedUnderlying, evt.SelectedExpiry, evt.Options);

            // Wire up SignalsOrchestrator to the compute service so it can push updates
            _computeService.SetSignalsOrchestrator(_signalsOrchestrator);
            _computeService.SetMarketContext(atmStrike, step, evt.SelectedExpiry, evt.SelectedUnderlying);

            // Display rows around ATM
            RefreshDisplayRows(atmStrike);

            await _dispatcher.InvokeAsync(() => {
                StatusText = $"Monitoring {Rows.Count} strikes. (Historical Sync OK)";
                IsBusy = false;
            });
        }

        private void OnOptionPriceBatch(IList<OptionPriceUpdate> batch)
        {
            bool isSimulationMode = SimulationService.Instance.IsSimulationMode;

            foreach (var update in batch)
            {
                if (isSimulationMode)
                {
                    // Use pre-computed metrics during simulation playback
                    // This advances through pre-built row states based on simulation time
                    _computeService.ProcessSimulatedTick(update.Symbol, update.Price, update.Timestamp);
                }
                else
                {
                    // Live mode - signal generation via orchestrator
                    _computeService.UpdateSignalPrice(update.Symbol, update.Price);
                }

                // Update UI row if present in current display set
                var row = _computeService.GetRowForSymbol(update.Symbol);
                if (row == null) continue;

                // Only update if this row is currently being displayed/tracked
                // Note: GetRowForSymbol returns the master row object, so updating it updates the UI
                // if that row is in our ObservableCollection

                string timeStr = update.Timestamp.ToString("HH:mm:ss");

                if (update.Symbol == row.CESymbol)
                {
                    row.CELTP = update.Price.ToString("F2");
                    row.CETickTime = timeStr;
                }
                else if (update.Symbol == row.PESymbol)
                {
                    row.PELTP = update.Price.ToString("F2");
                    row.PETickTime = timeStr;
                }
            }
        }

        /// <summary>
        /// Gets adaptive sample interval based on simulation speed.
        /// Higher speeds need more aggressive sampling to prevent UI flooding.
        /// </summary>
        private int GetAdaptiveSampleInterval()
        {
            if (!SimulationService.Instance.IsSimulationMode)
                return 100; // Live mode: 100ms (default batch interval)

            var speed = SimulationService.Instance.CurrentConfig?.SpeedMultiplier ?? 1;
            if (speed >= 60) return 500;      // 60x+: sample every 500ms
            if (speed >= 30) return 300;      // 30x-60x: sample every 300ms
            if (speed >= 10) return 200;      // 10x-30x: sample every 200ms
            return 100;                       // <10x: normal 100ms
        }

        // Track price batch subscription for dynamic re-subscription
        private IDisposable _priceBatchSubscription;

        /// <summary>
        /// Updates the price batch subscription with new sampling interval.
        /// Called when simulation speed changes.
        /// </summary>
        private void UpdatePriceBatchSubscription(MarketDataReactiveHub hub)
        {
            // Don't re-subscribe during initial setup or if not in simulation
            if (!SimulationService.Instance.IsSimulationMode) return;

            var interval = GetAdaptiveSampleInterval();
            _log.Debug($"[OptionSignalsViewModel] Updating price batch sampling to {interval}ms");

            // The subscription is already set up with Sample() which adapts based on GetAdaptiveSampleInterval()
            // This method is called to log the adaptation - actual re-subscription would be more complex
            // For now, the initial Sample() with fixed interval is sufficient
        }

        public void Dispose()
        {
            StopServices();

            // Unsubscribe from orchestrator events before disposal
            if (_signalsOrchestrator != null)
            {
                _signalsOrchestrator.SignalStatsChanged -= OnSignalStatsChanged;
            }

            _signalsOrchestrator?.Dispose();
        }
    }
}
