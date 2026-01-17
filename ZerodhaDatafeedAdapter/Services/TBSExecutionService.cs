using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Reactive.Disposables;
using System.Reactive.Linq;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;
using System.Windows.Threading;
using ZerodhaDatafeedAdapter.Core;
using ZerodhaDatafeedAdapter.Helpers;
using ZerodhaDatafeedAdapter.Logging;
using ZerodhaDatafeedAdapter.Models;
using ZerodhaDatafeedAdapter.Services.Analysis;
using ZerodhaDatafeedAdapter.Services.TBS;
using ZerodhaDatafeedAdapter.Services.Telegram;
using ZerodhaDatafeedAdapter.Services.Trading;
using ZerodhaDatafeedAdapter.Services.Simulation;
using ZerodhaDatafeedAdapter.Models.Reactive;

namespace ZerodhaDatafeedAdapter.Services
{
    /// <summary>
    /// TBS Execution Service - Manages TBS tranche execution, SL monitoring, and Stoxxo integration.
    /// Extracted from TBSManagerWindow to separate business logic from UI.
    /// </summary>
    public class TBSExecutionService : INotifyPropertyChanged, IDisposable
    {
        #region Singleton

        private static TBSExecutionService _instance;
        private static readonly object _instanceLock = new object();

        public static TBSExecutionService Instance
        {
            get
            {
                if (_instance == null)
                {
                    lock (_instanceLock)
                    {
                        if (_instance == null)
                        {
                            _instance = new TBSExecutionService();
                        }
                    }
                }
                return _instance;
            }
        }

        #endregion

        #region Fields

        private readonly ObservableCollection<TBSExecutionState> _executionStates;
        private readonly ConcurrentDictionary<int, TBSExecutionState> _executionStateDict;
        private readonly PnLTracker _pnlTracker;
        private readonly IOrderRouter _orderRouter;
        private readonly TradingContext _tradingContext;
        private DispatcherTimer _statusTimer;
        private DispatcherTimer _stoxxoPollingTimer;
        private bool _optionChainReady;
        private int _delayCountdown;
        private int _nextTrancheId = 1;
        private bool _isDisposed;

        private const int STATUS_TIMER_INTERVAL_MS = 1000;
        private const int STOXXO_POLLING_INTERVAL_MS = 5000;
        private const int FALLBACK_DELAY_SECONDS = 45; // Fallback if PriceSyncReady doesn't fire
        private Action _priceSyncReadyHandler;
        private CompositeDisposable _rxSubscriptions = new CompositeDisposable();

        // Event-Driven Optimization: Map symbols to interested tranches for O(1) lookup
        private readonly Dictionary<string, HashSet<int>> _symbolTrancheMap = new Dictionary<string, HashSet<int>>();
        private readonly object _mapLock = new object();


        #endregion

        #region Properties

        /// <summary>
        /// Collection of execution states for all tranches
        /// </summary>
        public ObservableCollection<TBSExecutionState> ExecutionStates => _executionStates;

        /// <summary>
        /// Whether Option Chain has finished loading and delay has passed
        /// </summary>
        public bool IsOptionChainReady
        {
            get => _optionChainReady;
            private set
            {
                if (_optionChainReady != value)
                {
                    _optionChainReady = value;
                    OnPropertyChanged();
                }
            }
        }

        /// <summary>
        /// Countdown seconds remaining before Option Chain is considered ready
        /// </summary>
        public int DelayCountdown
        {
            get => _delayCountdown;
            private set
            {
                if (_delayCountdown != value)
                {
                    _delayCountdown = value;
                    OnPropertyChanged();
                }
            }
        }

        /// <summary>
        /// Total P&L across all Live and SquaredOff tranches (excluding missed)
        /// </summary>
        public decimal TotalPnL => _pnlTracker.GetTotalPnL();

        /// <summary>
        /// Count of tranches currently Live
        /// </summary>
        public int LiveCount => _pnlTracker.GetLiveCount();

        /// <summary>
        /// Count of tranches currently in Monitoring state
        /// </summary>
        public int MonitoringCount => _pnlTracker.GetMonitoringCount();

        /// <summary>
        /// Currently selected underlying (from Option Chain)
        /// </summary>
        public string SelectedUnderlying
        {
            get => _tradingContext.SelectedUnderlying;
            set
            {
                if (_tradingContext.SelectedUnderlying != value)
                {
                    _tradingContext.SetUnderlying(value);
                    OnPropertyChanged();
                }
            }
        }

        /// <summary>
        /// Currently selected expiry (from Option Chain)
        /// </summary>
        public DateTime? SelectedExpiry
        {
            get => _tradingContext.SelectedExpiry;
            set
            {
                if (_tradingContext.SelectedExpiry != value)
                {
                    _tradingContext.SetExpiry(value);
                    OnPropertyChanged();
                }
            }
        }

        #endregion

        #region Events

        public event PropertyChangedEventHandler PropertyChanged;
        public event EventHandler<TBSExecutionState> StateChanged;
        public event EventHandler SummaryUpdated;

        #endregion

        #region Constructor

        private TBSExecutionService()
        {
            _executionStates = new ObservableCollection<TBSExecutionState>();
            _executionStateDict = new ConcurrentDictionary<int, TBSExecutionState>();

            // Get dependencies from ServiceFactory (centralized dependency management)
            _pnlTracker = ServiceFactory.GetPnLTracker();
            _pnlTracker.Initialize(_executionStateDict); // Initialize with execution state dictionary
            _orderRouter = ServiceFactory.GetOrderRouter();
            _tradingContext = ServiceFactory.GetTradingContext();
            _delayCountdown = FALLBACK_DELAY_SECONDS;

            // Subscribe to PriceSyncReady event for event-driven initialization
            SubscribeToPriceSyncReady();

            TBSLogger.Info("[TBSExecutionService] Initialized with new architecture using ServiceFactory");
        }

        /// <summary>
        /// Subscribe to PriceSyncReady via Rx stream (primary) and legacy event (fallback).
        /// The Rx stream uses ReplaySubject(1), so late subscribers (like TBS Manager opened
        /// after Option Chain is ready) will immediately receive the value.
        /// </summary>
        private void SubscribeToPriceSyncReady()
        {
            // Primary: Subscribe to Rx stream (ReplaySubject ensures late subscribers get the value)
            var hub = MarketDataReactiveHub.Instance;
            _rxSubscriptions.Add(
                hub.PriceSyncReadyStream
                    .Take(1)  // Only need the first signal
                    .Subscribe(
                        ready =>
                        {
                            if (ready && !_optionChainReady)
                            {
                                TBSLogger.Info("[TBSExecutionService] PriceSyncReady received via Rx stream");
                                DelayCountdown = 0;
                                IsOptionChainReady = true;
                                TBSLogger.Info("[TBSExecutionService] Option Chain marked ready via Rx PriceSyncReadyStream");
                            }
                        },
                        ex => TBSLogger.Error($"[TBSExecutionService] PriceSyncReadyStream error: {ex.Message}")));

            TBSLogger.Info("[TBSExecutionService] Subscribed to PriceSyncReadyStream (Rx)");

            // Legacy fallback: Also subscribe to event (for backward compatibility)
            _priceSyncReadyHandler = OnPriceSyncReady;
            MarketAnalyzerLogic.Instance.PriceSyncReady += _priceSyncReadyHandler;
            TBSLogger.Info("[TBSExecutionService] Subscribed to PriceSyncReady event (legacy)");

            // Check if price sync is already ready (in case we missed the event)
            if (MarketAnalyzerLogic.Instance.IsPriceSyncReady && !_optionChainReady)
            {
                TBSLogger.Info("[TBSExecutionService] Price sync already ready on startup - marking ready immediately");
                DelayCountdown = 0;
                IsOptionChainReady = true;
            }

            // Subscribe to simulation state changes
            // This allows TBS to reset and prepare when simulation mode starts
            SubscribeToSimulationState();

            // Event-Driven Optimization: Subscribe to Option Price Updates
            SubscribeToOptionPrices();
        }

        /// <summary>
        /// Subscribe to real-time option price updates for event-driven P&L calculation.
        /// Replaces 100ms polling with reactive updates.
        /// </summary>
        private void SubscribeToOptionPrices()
        {
            var hub = MarketDataReactiveHub.Instance;
            
            // Subscribe to batched stream to reduce UI pressure (buffer 100ms or 50 items)
            _rxSubscriptions.Add(
                hub.OptionPriceBatchStream
                    .ObserveOnDispatcher() // Ensure updates happen on UI thread
                    .Subscribe(
                        batch => ProcessOptionPriceBatch(batch),
                        ex => TBSLogger.Error($"[TBSExecutionService] OptionPriceStream error: {ex.Message}")));
                        
            TBSLogger.Info("[TBSExecutionService] Subscribed to OptionPriceBatchStream (Rx)");
        }

        /// <summary>
        /// Subscribe to simulation state changes.
        /// This allows TBS to reset and prepare when simulation mode starts.
        /// </summary>
        private void SubscribeToSimulationState()
        {
            var hub = MarketDataReactiveHub.Instance;

            _rxSubscriptions.Add(
                hub.SimulationStateStream
                    .DistinctUntilChanged(s => s.State)
                    .ObserveOnDispatcher()
                    .Subscribe(
                        state => OnSimulationStateChanged(state),
                        ex => TBSLogger.Error($"[TBSExecutionService] SimulationStateStream error: {ex.Message}")));

            TBSLogger.Info("[TBSExecutionService] Subscribed to SimulationStateStream (Rx)");
        }

        /// <summary>
        /// Handles simulation state changes.
        /// Resets TBS state when simulation starts loading, ensures readiness when simulation is ready.
        /// </summary>
        private void OnSimulationStateChanged(SimulationStateUpdate state)
        {
            TBSLogger.Info($"[TBSExecutionService] Simulation state changed: {state.State}");

            switch (state.State)
            {
                case SimulationState.Loading:
                    // Simulation is loading tick data - reset TBS state
                    TBSLogger.Info("[TBSExecutionService] Simulation loading - resetting TBS for fresh simulation");
                    ResetForSimulation();
                    break;

                case SimulationState.Ready:
                    // Tick data loaded, options will be published
                    TBSLogger.Info("[TBSExecutionService] Simulation ready - awaiting option chain and tranche configs");
                    break;

                case SimulationState.Playing:
                    TBSLogger.Info("[TBSExecutionService] Simulation playing - processing ticks");
                    break;

                case SimulationState.Idle:
                case SimulationState.Completed:
                    TBSLogger.Info($"[TBSExecutionService] Simulation ended ({state.State})");
                    break;
            }
        }

        /// <summary>
        /// Resets TBS state for a new simulation run.
        /// Clears execution states but preserves configuration.
        /// </summary>
        private void ResetForSimulation()
        {
            lock (_mapLock)
            {
                // Clear symbol mappings
                _symbolTrancheMap.Clear();
            }

            // Reset execution states to Idle (preserve configs)
            foreach (var tranche in _executionStates.ToList())
            {
                tranche.Status = TBSExecutionStatus.Idle;
                tranche.CombinedPnL = 0;
                tranche.StoxxoPnL = 0;
                tranche.TargetHit = false;
                tranche.IsMissed = false;
                tranche.SkippedDueToProfitCondition = false;

                // Reset legs
                foreach (var leg in tranche.Legs)
                {
                    leg.Status = TBSLegStatus.Pending;
                    leg.EntryPrice = 0;
                    leg.CurrentPrice = 0;
                    leg.PnL = 0;
                }
            }

            TBSLogger.Info("[TBSExecutionService] TBS reset for simulation");
        }

        /// <summary>
        /// Process a batch of option price updates.
        /// Updates P&L and checks SL/Target for affected tranches only.
        /// </summary>
        private void ProcessOptionPriceBatch(IList<OptionPriceUpdate> batch)
        {
            if (batch.Count == 0) return;
            
            HashSet<TBSExecutionState> affectedTranches = new HashSet<TBSExecutionState>();

            lock (_mapLock)
            {
                foreach (var update in batch)
                {
                    if (_symbolTrancheMap.TryGetValue(update.Symbol, out var trancheIds))
                    {
                        foreach (var id in trancheIds)
                        {
                            var tranche = _executionStates.FirstOrDefault(t => t.TrancheId == id);
                            if (tranche != null && (tranche.Status == TBSExecutionStatus.Live || tranche.Status == TBSExecutionStatus.Monitoring)) // Only update active tranches
                            {
                                // Update the specific leg price
                                var leg = tranche.Legs.FirstOrDefault(l => l.Symbol == update.Symbol);
                                if (leg != null)
                                {
                                    leg.CurrentPrice = (decimal)update.Price;
                                    affectedTranches.Add(tranche);
                                }
                            }
                        }
                    }
                }
            }

            // Process logic for affected tranches
            foreach (var tranche in affectedTranches)
            {
                // Update P&L
                tranche.UpdateCombinedPnL();
                
                // If Live and locked, check SL/Target
                if (tranche.Status == TBSExecutionStatus.Live && tranche.StrikeLocked)
                {
                     CheckSLConditions(tranche); // This checks individual SL, combined SL, and Target
                }
            }
            
            if (affectedTranches.Count > 0)
            {
                OnSummaryUpdated();
            }
        }

        /// <summary>
        /// Update the symbol map when a tranche's symbols change.
        /// </summary>
        private void UpdateSymbolMap(TBSExecutionState state, string oldCe, string oldPe, string newCe, string newPe)
        {
            lock (_mapLock)
            {
                // Remove old symbols if they exist
                if (!string.IsNullOrEmpty(oldCe)) RemoveFromMap(oldCe, state.TrancheId);
                if (!string.IsNullOrEmpty(oldPe)) RemoveFromMap(oldPe, state.TrancheId);
                
                // Add new symbols
                if (!string.IsNullOrEmpty(newCe)) AddToMap(newCe, state.TrancheId);
                if (!string.IsNullOrEmpty(newPe)) AddToMap(newPe, state.TrancheId);
            }
        }
        
        private void AddToMap(string symbol, int trancheId)
        {
            if (!_symbolTrancheMap.ContainsKey(symbol))
            {
                _symbolTrancheMap[symbol] = new HashSet<int>();
            }
            _symbolTrancheMap[symbol].Add(trancheId);
        }
        
        private void RemoveFromMap(string symbol, int trancheId)
        {
            if (_symbolTrancheMap.TryGetValue(symbol, out var ids))
            {
                ids.Remove(trancheId);
                if (ids.Count == 0)
                {
                    _symbolTrancheMap.Remove(symbol);
                }
            }
        }

        /// <summary>
        /// Handler for PriceSyncReady legacy event - marks Option Chain as ready immediately.
        /// </summary>
        private void OnPriceSyncReady()
        {
            if (_optionChainReady) return; // Already ready

            TBSLogger.Info("[TBSExecutionService] PriceSyncReady received via legacy event");

            // Mark as ready immediately - no more waiting!
            DelayCountdown = 0;
            IsOptionChainReady = true;

            TBSLogger.Info("[TBSExecutionService] Option Chain marked ready via PriceSyncReady event");
        }

        #endregion

        #region State Initialization

        /// <summary>
        /// Initialize execution states from configuration
        /// </summary>
        /// <param name="underlying">Filter by underlying (null for all)</param>
        /// <param name="dte">Filter by DTE (null for all)</param>
        public void InitializeExecutionStates(string underlying, int? dte)
        {
            _executionStates.Clear();
            _executionStateDict.Clear();
            lock (_mapLock) { _symbolTrancheMap.Clear(); }
            _nextTrancheId = 1;

            var configs = TBSConfigurationService.Instance.GetConfigurations(underlying, dte);

            // Use simulation time when in simulation mode, real time otherwise
            var isSimulationActive = SimulationTimeHelper.IsSimulationActive;
            var now = SimulationTimeHelper.TimeOfDay;

            foreach (var config in configs)
            {
                var state = CreateExecutionState(config, now, isSimulationActive);
                _executionStates.Add(state);
                _executionStateDict[state.TrancheId] = state;
            }

            OnSummaryUpdated();
            TBSLogger.Info($"[TBSExecutionService] Initialized {_executionStates.Count} tranches");

            // Send Telegram alert for TBS Module loaded
            if (_executionStates.Count > 0)
            {
                var firstConfig = _executionStates.First().Config;
                TelegramAlertService.Instance.SendTBSModuleLoadedAlert(
                    underlying ?? firstConfig?.Underlying ?? "Unknown",
                    dte ?? firstConfig?.DTE ?? 0,
                    _executionStates.Count);
            }
        }

        private TBSExecutionState CreateExecutionState(TBSConfigEntry config, TimeSpan now, bool isSimulationActive)
        {
            int lotSize = SymbolHelper.GetLotSize(config.Underlying);

            var state = new TBSExecutionState
            {
                Config = config,
                TrancheId = _nextTrancheId++,
                LotSize = lotSize,
                Quantity = config.Quantity,
                IndividualSLPercent = config.IndividualSL,
                CombinedSLPercent = config.CombinedSL,
                TargetPercent = config.TargetPercent,
                ProfitCondition = config.ProfitCondition,
                ConfigExitTime = config.ExitTime,
                HedgeAction = config.HedgeAction ?? "exit_both"
            };

            // Initialize legs with lot size and quantity
            state.Legs.Add(new TBSLegState
            {
                OptionType = "CE",
                Quantity = config.Quantity,
                LotSize = lotSize
            });
            state.Legs.Add(new TBSLegState
            {
                OptionType = "PE",
                Quantity = config.Quantity,
                LotSize = lotSize
            });

            // Always populate ATM strike for all tranches (so they're ready)
            UpdateStrikeForState(state);

            // Determine initial status based on time
            InitializeTrancheStatus(state, config, now, isSimulationActive);

            return state;
        }

        private void InitializeTrancheStatus(TBSExecutionState state, TBSConfigEntry config, TimeSpan now, bool isSimulationActive)
        {
            var entryTime = config.EntryTime;
            var timeSinceEntry = now - entryTime;

            if (timeSinceEntry.TotalMinutes > 1)
            {
                // Entry time passed more than 1 minute ago - this tranche was MISSED
                state.Status = TBSExecutionStatus.Idle;
                state.IsMissed = true;
                state.Message = $"Missed (entry was at {entryTime:hh\\:mm\\:ss})";
                TBSLogger.Info($"Tranche #{state.TrancheId} {config.EntryTime:hh\\:mm\\:ss} MISSED - initialized after entry time passed");
            }
            else
            {
                // Entry time is in the future or within monitoring window
                state.UpdateStatusBasedOnTime(now, skipExitTimeCheck: isSimulationActive);

                // If status went to Live during initialization, it means entry "just passed"
                if (state.Status == TBSExecutionStatus.Live && !state.StrikeLocked)
                {
                    state.Status = TBSExecutionStatus.Idle;
                    state.IsMissed = true;
                    state.Message = $"Missed (entry just passed at {entryTime:hh\\:mm\\:ss})";
                    TBSLogger.Info($"Tranche #{state.TrancheId} {config.EntryTime:hh\\:mm\\:ss} Entry just passed - marking as MISSED");
                }
            }

            // For tranches that are SquaredOff (past exit time), mark as historical
            if (state.Status == TBSExecutionStatus.SquaredOff)
            {
                state.Message = "Missed (initialized after exit time)";
            }
        }

        #endregion

        #region Timer Management

        /// <summary>
        /// Start all monitoring timers
        /// </summary>
        public void StartMonitoring()
        {
            StartStatusTimer();
            StartStoxxoPollingTimer();
            TBSLogger.Info("[TBSExecutionService] Monitoring started");
        }

        /// <summary>
        /// Stop all monitoring timers
        /// </summary>
        public void StopMonitoring()
        {
            StopStatusTimer();
            StopStoxxoPollingTimer();
            TBSLogger.Info("[TBSExecutionService] Monitoring stopped");
        }

        private void StartStatusTimer()
        {
            if (_statusTimer != null) return;

            _statusTimer = new DispatcherTimer
            {
                Interval = TimeSpan.FromMilliseconds(STATUS_TIMER_INTERVAL_MS)
            };
            _statusTimer.Tick += OnStatusTimerTick;
            _statusTimer.Start();
        }

        private void StopStatusTimer()
        {
            if (_statusTimer != null)
            {
                _statusTimer.Stop();
                _statusTimer.Tick -= OnStatusTimerTick;
                _statusTimer = null;
            }
        }

        private void StartStoxxoPollingTimer()
        {
            if (_stoxxoPollingTimer != null) return;

            _stoxxoPollingTimer = new DispatcherTimer
            {
                Interval = TimeSpan.FromMilliseconds(STOXXO_POLLING_INTERVAL_MS)
            };
            _stoxxoPollingTimer.Tick += OnStoxxoPollingTimerTick;
            _stoxxoPollingTimer.Start();
            TBSLogger.Info("[TBSExecutionService] Stoxxo polling timer started");
        }

        private void StopStoxxoPollingTimer()
        {
            if (_stoxxoPollingTimer != null)
            {
                _stoxxoPollingTimer.Stop();
                _stoxxoPollingTimer.Tick -= OnStoxxoPollingTimerTick;
                _stoxxoPollingTimer = null;
            }
        }

        #endregion

        #region Status Timer Logic

        private void OnStatusTimerTick(object sender, EventArgs e)
        {
            if (!_optionChainReady) return;

            // Use simulation time when in simulation mode, real time otherwise
            var isSimulationActive = SimulationTimeHelper.IsSimulationActive;
            var now = SimulationTimeHelper.TimeOfDay;

            foreach (var state in _executionStates)
            {
                ProcessTrancheStatus(state, now, isSimulationActive);
            }

            OnSummaryUpdated();
        }

        private void ProcessTrancheStatus(TBSExecutionState state, TimeSpan now, bool isSimulationActive)
        {
            var oldStatus = state.Status;
            
            // 1. Update Status based on Time (Entry/Exit times)
            bool statusChanged = state.UpdateStatusBasedOnTime(now, skipExitTimeCheck: isSimulationActive);

            if (statusChanged)
            {
                TBSLogger.LogStatusTransition(state.TrancheId, oldStatus.ToString(), state.Status.ToString(), $"StrikeLocked={state.StrikeLocked}");
                OnStateChanged(state);
            }

            // 2. Monitoring Phase Logic
            // When entering Monitoring state, fetch ATM strike
            if (oldStatus != TBSExecutionStatus.Monitoring && state.Status == TBSExecutionStatus.Monitoring)
            {
                TBSLogger.Info($"Tranche #{state.TrancheId} Entered Monitoring - fetching ATM strike");
                UpdateStrikeForState(state);
            }

            // While in Monitoring, keep updating ATM strike until locked
            if (state.Status == TBSExecutionStatus.Monitoring && !state.StrikeLocked)
            {
                UpdateStrikeForState(state);
            }

            // Stoxxo: Place order at 5 seconds before entry
            if (state.Status == TBSExecutionStatus.Monitoring && !state.StoxxoOrderPlaced && !state.SkippedDueToProfitCondition)
            {
                var timeDiff = state.Config.EntryTime - now;
                if (timeDiff.TotalSeconds <= 5 && timeDiff.TotalSeconds > 0)
                {
                    if (state.ProfitCondition && !ShouldDeployTranche(state))
                    {
                        TBSLogger.Warn($"Tranche #{state.TrancheId} ProfitCondition not met 5s before entry - skipping");
                        state.SkippedDueToProfitCondition = true;
                        state.Status = TBSExecutionStatus.Skipped;
                        state.Message = "Skipped: Prior tranches P&L <= 0";
                    }
                    else
                    {
                        PlaceStoxxoOrderAsync(state).SafeFireAndForget("TBS.PlaceStoxxoOrder");
                    }
                }
            }

            // 3. Live Phase Logic
            // When going Live, lock strike and enter positions (only on transition from Monitoring)
            if (oldStatus == TBSExecutionStatus.Monitoring && state.Status == TBSExecutionStatus.Live)
            {
                if (state.SkippedDueToProfitCondition)
                {
                    state.Status = TBSExecutionStatus.Skipped;
                }
                else if (!state.StrikeLocked) // Extra guard to prevent double execution
                {
                    TBSLogger.Info($"Tranche #{state.TrancheId} Going Live - locking strike");
                    LockStrikeAndEnter(state);
                }
            }

            // Stoxxo: Reconcile legs 10 seconds after going Live
            if (state.Status == TBSExecutionStatus.Live && state.StoxxoOrderPlaced && !state.StoxxoReconciled)
            {
                if (state.ActualEntryTime.HasValue)
                {
                    var elapsed = SimulationTimeHelper.Now - state.ActualEntryTime.Value;
                    if (elapsed.TotalSeconds >= 10)
                    {
                        ReconcileStoxxoLegsAsync(state).SafeFireAndForget("TBS.ReconcileStoxxoLegs");
                    }
                }
            }

            // NOTE: SL/Target checks moved to Event-Driven ProcessOptionPriceBatch method!
            // However, we still call CheckSLConditions here as a fallback/safety check periodically
            if (state.Status == TBSExecutionStatus.Live && state.StrikeLocked)
            {
                CheckSLConditions(state);
            }


            // Stoxxo: Send SL modification when SL-to-cost is applied
            if (state.SLToCostApplied && !state.StoxxoSLModified && !string.IsNullOrEmpty(state.StoxxoPortfolioName))
            {
                ModifyStoxxoSLAsync(state).SafeFireAndForget("TBS.ModifyStoxxoSL");
            }

            // When going SquaredOff, record exit details
            if (oldStatus == TBSExecutionStatus.Live && state.Status == TBSExecutionStatus.SquaredOff)
            {
                TBSLogger.Info($"Tranche #{state.TrancheId} Going SquaredOff - recording exit prices");
                RecordExitPrices(state);

                if (!state.StoxxoExitCalled && !string.IsNullOrEmpty(state.StoxxoPortfolioName))
                {
                    ExitStoxxoOrderAsync(state).SafeFireAndForget("TBS.ExitStoxxoOrder");
                }
            }

            // Update combined P&L from legs
            state.UpdateCombinedPnL();
        }

        private void ProcessStoxxoOrderPlacement(TBSExecutionState state, TimeSpan now)
        {
            if (state.Status != TBSExecutionStatus.Monitoring || state.StoxxoOrderPlaced || state.SkippedDueToProfitCondition)
                return;

            var timeDiff = state.Config.EntryTime - now;
            if (timeDiff.TotalSeconds <= 5 && timeDiff.TotalSeconds > 0)
            {
                if (state.ProfitCondition && !ShouldDeployTranche(state))
                {
                    TBSLogger.Warn($"Tranche #{state.TrancheId} ProfitCondition not met 5s before entry - skipping");
                    state.SkippedDueToProfitCondition = true;
                    state.Status = TBSExecutionStatus.Skipped;
                    state.Message = "Skipped: Prior tranches P&L <= 0";
                }
                else
                {
                    PlaceStoxxoOrderAsync(state).SafeFireAndForget("TBS.ProcessStoxxoOrderPlacement");
                }
            }
        }

        private void ProcessGoingLive(TBSExecutionState state, TBSExecutionStatus oldStatus)
        {
            if (oldStatus != TBSExecutionStatus.Monitoring || state.Status != TBSExecutionStatus.Live)
                return;

            if (state.SkippedDueToProfitCondition)
            {
                state.Status = TBSExecutionStatus.Skipped;
            }
            else
            {
                TBSLogger.Info($"Tranche #{state.TrancheId} Going Live - locking strike");
                LockStrikeAndEnter(state);
            }
        }

        private void ProcessStoxxoReconciliation(TBSExecutionState state)
        {
            if (state.Status != TBSExecutionStatus.Live || !state.StoxxoOrderPlaced || state.StoxxoReconciled)
                return;

            if (state.ActualEntryTime.HasValue)
            {
                var elapsed = SimulationTimeHelper.Now - state.ActualEntryTime.Value;
                if (elapsed.TotalSeconds >= 10)
                {
                    ReconcileStoxxoLegsAsync(state).SafeFireAndForget("TBS.ProcessStoxxoReconciliation");
                }
            }
        }

        private void ProcessGoingSquaredOff(TBSExecutionState state, TBSExecutionStatus oldStatus)
        {
            if (oldStatus != TBSExecutionStatus.Live || state.Status != TBSExecutionStatus.SquaredOff)
                return;

            TBSLogger.Info($"Tranche #{state.TrancheId} Going SquaredOff - recording exit prices");
            RecordExitPrices(state);

            if (!state.StoxxoExitCalled && !string.IsNullOrEmpty(state.StoxxoPortfolioName))
            {
                ExitStoxxoOrderAsync(state).SafeFireAndForget("TBS.ProcessGoingSquaredOff");
            }
        }

        #endregion

        #region SL/Target Monitoring

        private void CheckSLConditions(TBSExecutionState state)
        {
            bool anyLegHitSLThisTick = false;

            foreach (var leg in state.Legs)
            {
                if (leg.Status != TBSLegStatus.Active) continue;

                // Update current price from PriceHub
                // (Event-Driven: Already updated by ProcessOptionPriceBatch, but updating here ensures fallback if tick missed)
                decimal currentPrice = GetCurrentPrice(leg.Symbol);
                if (currentPrice > 0)
                {
                    leg.CurrentPrice = currentPrice;
                }


                // Check individual leg SL
                if (leg.SLPrice > 0 && leg.CurrentPrice >= leg.SLPrice)
                {
                    TBSLogger.Warn($"Tranche #{state.TrancheId} {leg.OptionType} SL TRIGGERED");
                    leg.Status = TBSLegStatus.SLHit;
                    leg.ExitPrice = leg.CurrentPrice;
                    leg.ExitTime = SimulationTimeHelper.Now;
                    leg.ExitReason = $"SL Hit @ {leg.CurrentPrice:F2}";
                    anyLegHitSLThisTick = true;

                    // Send Telegram alert for individual SL
                    TelegramAlertService.Instance.SendStoplossAlert(
                        state.TrancheId, leg.OptionType, leg.CurrentPrice);
                }
            }

            // Handle hedge_to_cost
            if (anyLegHitSLThisTick && !state.SLToCostApplied &&
                state.HedgeAction?.Contains("hedge_to_cost") == true)
            {
                foreach (var leg in state.Legs.Where(l => l.Status == TBSLegStatus.Active))
                {
                    leg.SLPrice = leg.EntryPrice;
                    TBSLogger.Info($"Tranche #{state.TrancheId} HEDGE_TO_COST: {leg.OptionType} SL moved to cost");
                }
                state.SLToCostApplied = true;
                state.Message = "SL moved to cost (hedge)";

                // Send Telegram alert for profit protection
                TelegramAlertService.Instance.SendProfitProtectionAlert(
                    state.TrancheId, "SL moved to cost (hedge)");
            }

            // Check combined SL
            CheckCombinedSL(state);

            // Check Target
            CheckTarget(state);

            // If all legs are closed, mark state as SquaredOff
            if (state.AllLegsClosed())
            {
                state.Status = TBSExecutionStatus.SquaredOff;
                state.Message = state.TargetHit ? "All legs exited (Target)" : "All legs exited (SL)";
                state.ExitTime = SimulationTimeHelper.Now;
            }
        }

        private void CheckCombinedSL(TBSExecutionState state)
        {
            if (state.CombinedSLPercent <= 0) return;

            decimal entryPremium = state.Legs.Sum(l => l.EntryPrice);
            decimal currentPremium = state.Legs.Sum(l => l.CurrentPrice);

            if (entryPremium > 0)
            {
                decimal combinedSLPrice = entryPremium * (1 + state.CombinedSLPercent);
                if (currentPremium >= combinedSLPrice)
                {
                    TBSLogger.Warn($"Tranche #{state.TrancheId} Combined SL HIT");
                    foreach (var leg in state.Legs.Where(l => l.Status == TBSLegStatus.Active))
                    {
                        leg.Status = TBSLegStatus.SLHit;
                        leg.ExitPrice = leg.CurrentPrice;
                        leg.ExitTime = SimulationTimeHelper.Now;
                        leg.ExitReason = "Combined SL Hit";
                    }

                    // Send Telegram alert for combined SL
                    TelegramAlertService.Instance.SendCombinedSLAlert(state.TrancheId, currentPremium);
                }
            }
        }

        private void CheckTarget(TBSExecutionState state)
        {
            if (state.TargetPercent <= 0 || state.TargetProfitThreshold <= 0 || state.TargetHit)
                return;

            int activeLegsCount = state.Legs.Count(l => l.Status == TBSLegStatus.Active);
            if (activeLegsCount != 2) return;

            state.UpdateCombinedPnL();

            if (state.CombinedPnL >= state.TargetProfitThreshold)
            {
                TBSLogger.Info($"Tranche #{state.TrancheId} TARGET HIT: P&L={state.CombinedPnL:F2}");
                foreach (var leg in state.Legs.Where(l => l.Status == TBSLegStatus.Active))
                {
                    leg.Status = TBSLegStatus.TargetHit;
                    leg.ExitPrice = leg.CurrentPrice;
                    leg.ExitTime = SimulationTimeHelper.Now;
                    leg.ExitReason = $"Target Hit @ P&L={state.CombinedPnL:F2}";
                }
                state.TargetHit = true;
                state.Message = $"Target hit @ P&L {state.CombinedPnL:F2}";

                // Send Telegram alert for target hit
                TelegramAlertService.Instance.SendTargetHitAlert(state.TrancheId, state.CombinedPnL);

                if (!state.StoxxoExitCalled && !string.IsNullOrEmpty(state.StoxxoPortfolioName))
                {
                    ExitStoxxoOrderAsync(state).SafeFireAndForget("TBS.ExitStoxxoTargetHit");
                }
            }
        }

        #endregion

        #region Strike and Price Management

        private void UpdateStrikeForState(TBSExecutionState state)
        {
            if (state.StrikeLocked) return;

            var underlying = state.Config?.Underlying;
            if (string.IsNullOrEmpty(underlying)) return;

            decimal atmStrike = MarketAnalyzerLogic.Instance.GetATMStrike(underlying);
            if (atmStrike <= 0) return;

            if (state.Strike != atmStrike)
            {
                state.Strike = atmStrike;
                UpdateLegSymbols(state, underlying, atmStrike);
            }
        }

        private void UpdateLegSymbols(TBSExecutionState state, string underlying, decimal strike)
        {
            DateTime? expiry = null;
            var selectedExpiryStr = MarketAnalyzerLogic.Instance.SelectedExpiry;
            if (!string.IsNullOrEmpty(selectedExpiryStr) && DateTime.TryParse(selectedExpiryStr, out var parsed))
                expiry = parsed;
            expiry = expiry ?? _tradingContext.SelectedExpiry ?? FindNearestExpiry(underlying, state.Config?.DTE ?? 0);
            if (!expiry.HasValue) return;

            bool isMonthlyExpiry = MarketAnalyzerLogic.Instance.SelectedIsMonthlyExpiry;
            if (MarketAnalyzerLogic.Instance.SelectedUnderlying != underlying)
            {
                var expiries = MarketAnalyzerLogic.Instance.GetCachedExpiries(underlying);
                isMonthlyExpiry = SymbolHelper.IsMonthlyExpiry(expiry.Value, expiries);
            }

            string ceSymbol = SymbolHelper.BuildOptionSymbol(underlying, expiry.Value, strike, "CE", isMonthlyExpiry);
            string peSymbol = SymbolHelper.BuildOptionSymbol(underlying, expiry.Value, strike, "PE", isMonthlyExpiry);

            var ceLeg = state.Legs.FirstOrDefault(l => l.OptionType == "CE");
            var peLeg = state.Legs.FirstOrDefault(l => l.OptionType == "PE");

            // Update symbol map
            UpdateSymbolMap(state, ceLeg?.Symbol, peLeg?.Symbol, ceSymbol, peSymbol);

            if (ceLeg != null) ceLeg.Symbol = ceSymbol;
            if (peLeg != null) peLeg.Symbol = peSymbol;
        }

        private void LockStrikeAndEnter(TBSExecutionState state)
        {
            state.StrikeLocked = true;
            state.ActualEntryTime = SimulationTimeHelper.Now;

            foreach (var leg in state.Legs)
            {
                decimal price = GetCurrentPrice(leg.Symbol);
                if (price > 0)
                {
                    leg.EntryPrice = price;
                    leg.CurrentPrice = price;
                    leg.Status = TBSLegStatus.Active;

                    if (state.IndividualSLPercent > 0)
                    {
                        leg.SLPrice = leg.EntryPrice * (1 + state.IndividualSLPercent);
                    }
                }
            }

            // Calculate target threshold
            var ceLeg = state.Legs.FirstOrDefault(l => l.OptionType == "CE");
            var peLeg = state.Legs.FirstOrDefault(l => l.OptionType == "PE");
            if (ceLeg != null && peLeg != null && ceLeg.EntryPrice > 0 && peLeg.EntryPrice > 0)
            {
                state.CombinedEntryPremium = ceLeg.EntryPrice + peLeg.EntryPrice;
                if (state.TargetPercent > 0)
                {
                    state.TargetProfitThreshold = state.CombinedEntryPremium * state.TargetPercent * state.Quantity * state.LotSize;
                }

                // Send Telegram alert for tranche entry
                TelegramAlertService.Instance.SendTrancheEntryAlert(
                    state.TrancheId, state.Strike, ceLeg.EntryPrice, peLeg.EntryPrice);
            }

            TBSLogger.Info($"Tranche #{state.TrancheId} Locked strike {state.Strike}");
        }

        private void RecordExitPrices(TBSExecutionState state)
        {
            foreach (var leg in state.Legs)
            {
                if (leg.Status == TBSLegStatus.Active)
                {
                    decimal exitPrice = GetCurrentPrice(leg.Symbol);
                    if (exitPrice > 0)
                    {
                        leg.ExitPrice = exitPrice;
                        leg.ExitTime = SimulationTimeHelper.Now;
                        leg.ExitReason = "Time-based exit";
                        leg.Status = TBSLegStatus.Exited;
                    }
                }
            }
        }

        private DateTime? FindNearestExpiry(string underlying, int targetDTE)
        {
            var today = DateTime.Today;
            var targetDate = today.AddDays(targetDTE);
            while (targetDate.DayOfWeek != DayOfWeek.Thursday)
                targetDate = targetDate.AddDays(1);
            return targetDate;
        }

        private decimal GetCurrentPrice(string symbol)
        {
            if (string.IsNullOrEmpty(symbol)) return 0;
            return (decimal)MarketAnalyzerLogic.Instance.GetPrice(symbol);
        }

        #endregion

        #region Profit Condition

        private bool ShouldDeployTranche(TBSExecutionState state)
        {
            if (!state.ProfitCondition) return true;

            var priorDeployedTranches = _executionStates
                .Where(t => t.TrancheId < state.TrancheId)
                .Where(t => t.StoxxoOrderPlaced || t.Status == TBSExecutionStatus.Live || t.Status == TBSExecutionStatus.SquaredOff)
                .Where(t => !t.SkippedDueToProfitCondition)
                .Where(t => !t.IsMissed)
                .ToList();

            if (priorDeployedTranches.Count == 0) return true;

            decimal cumulativePnL = priorDeployedTranches.Sum(t => t.CombinedPnL);
            return cumulativePnL > 0;
        }

        #endregion

        #region Stoxxo Integration

        private void OnStoxxoPollingTimerTick(object sender, EventArgs e)
        {
            PollStoxxoPortfoliosAsync().SafeFireAndForget("TBS.OnStoxxoPollingTimerTick");
        }

        private async Task PollStoxxoPortfoliosAsync()
        {
            try
            {
                foreach (var state in _executionStates.Where(s => !string.IsNullOrEmpty(s.StoxxoPortfolioName)))
                {
                    if (state.Status != TBSExecutionStatus.Live && state.Status != TBSExecutionStatus.SquaredOff)
                        continue;

                    await _orderRouter.PollAndUpdateStatusAsync(state);
                }
            }
            catch (Exception ex)
            {
                TBSLogger.Error($"Stoxxo polling error: {ex.Message}");
            }
        }

        private async Task PlaceStoxxoOrderAsync(TBSExecutionState state)
        {
            try
            {
                state.StoxxoOrderPlaced = true;

                var result = await _orderRouter.PlaceOrderAsync(state);

                if (result.Success)
                {
                    state.StoxxoPortfolioName = result.PortfolioName;
                    state.Message = result.Message;
                }
                else
                {
                    state.Message = result.Message;
                }
            }
            catch (Exception ex)
            {
                TBSLogger.Error($"Tranche #{state.TrancheId} PlaceStoxxoOrderAsync error: {ex.Message}");
                state.Message = $"Stoxxo error: {ex.Message}";
            }
        }

        private async Task ReconcileStoxxoLegsAsync(TBSExecutionState state)
        {
            try
            {
                state.StoxxoReconciled = true;
                await _orderRouter.ReconcileOrderAsync(state);
            }
            catch (Exception ex)
            {
                TBSLogger.Error($"Tranche #{state.TrancheId} ReconcileStoxxoLegsAsync error: {ex.Message}");
            }
        }

        private async Task ModifyStoxxoSLAsync(TBSExecutionState state)
        {
            try
            {
                state.StoxxoSLModified = true;
                await _orderRouter.ModifyStopLossAsync(state);
            }
            catch (Exception ex)
            {
                TBSLogger.Error($"Tranche #{state.TrancheId} ModifyStoxxoSLAsync error: {ex.Message}");
            }
        }

        private async Task ExitStoxxoOrderAsync(TBSExecutionState state)
        {
            try
            {
                state.StoxxoExitCalled = true;

                var success = await _orderRouter.ExitOrderAsync(state);

                if (success)
                {
                    state.Message = "Stoxxo exit sent";
                }
                else
                {
                    state.Message = "Stoxxo exit failed";
                }
            }
            catch (Exception ex)
            {
                TBSLogger.Error($"Tranche #{state.TrancheId} ExitStoxxoOrderAsync error: {ex.Message}");
                state.Message = $"Stoxxo exit error: {ex.Message}";
            }
        }

        #endregion

        #region Option Chain Integration

        /// <summary>
        /// Called when Option Chain is ready (delay passed)
        /// </summary>
        public void OnOptionChainReady()
        {
            IsOptionChainReady = true;
            TBSLogger.Info("[TBSExecutionService] Option Chain ready");
        }

        /// <summary>
        /// Called when Option Chain generates options
        /// </summary>
        public void OnOptionsGenerated(string underlying, DateTime? expiry)
        {
            if (string.IsNullOrEmpty(underlying) || !expiry.HasValue) return;

            SelectedUnderlying = underlying;
            SelectedExpiry = expiry;

            TBSLogger.Info($"[TBSExecutionService] Options generated: {underlying}, expiry={expiry.Value:dd-MMM-yyyy}");
        }

        #endregion

        #region INotifyPropertyChanged

        protected void OnPropertyChanged([CallerMemberName] string name = null)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
        }

        protected void OnStateChanged(TBSExecutionState state)
        {
            StateChanged?.Invoke(this, state);
        }

        protected void OnSummaryUpdated()
        {
            OnPropertyChanged(nameof(TotalPnL));
            OnPropertyChanged(nameof(LiveCount));
            OnPropertyChanged(nameof(MonitoringCount));
            SummaryUpdated?.Invoke(this, EventArgs.Empty);
        }

        #endregion

        #region IDisposable

        public void Dispose()
        {
            if (_isDisposed) return;
            _isDisposed = true;

            // Dispose Rx subscriptions (primary method for PriceSyncReady)
            _rxSubscriptions?.Dispose();
            _rxSubscriptions = null;

            // Unsubscribe from PriceSyncReady legacy event (fallback)
            if (_priceSyncReadyHandler != null)
            {
                MarketAnalyzerLogic.Instance.PriceSyncReady -= _priceSyncReadyHandler;
                _priceSyncReadyHandler = null;
            }

            StopMonitoring();
            _executionStates.Clear();

            TBSLogger.Info("[TBSExecutionService] Disposed");
        }

        #endregion
    }
}
