using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Reactive.Linq;
using System.Reactive.Subjects;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;
using System.Windows.Threading;
using NinjaTrader.Cbi;
using NinjaTrader.Data;
using ZerodhaAPI.Common.Enums;
using ZerodhaDatafeedAdapter.Helpers;
using ZerodhaDatafeedAdapter.Logging;
using ZerodhaDatafeedAdapter.Models;
using ZerodhaDatafeedAdapter.Models.Reactive;
using ZerodhaDatafeedAdapter.Services.Analysis;
using ZerodhaDatafeedAdapter.Services.Instruments;
using ZerodhaDatafeedAdapter.Services.Simulation;

namespace ZerodhaDatafeedAdapter.Services
{
    /// <summary>
    /// Singleton service that manages historical tick data replay simulation.
    /// Loads tick data from NinjaTrader database and replays them through PriceHub.
    /// </summary>
    public class SimulationService : INotifyPropertyChanged
    {
        private static SimulationService _instance;
        private static readonly object _lock = new object();

        // Dedicated logger for simulation flow tracking
        private static readonly ILoggerService _log = LoggerFactory.Simulation;

        public static SimulationService Instance
        {
            get
            {
                if (_instance == null)
                {
                    lock (_lock)
                    {
                        if (_instance == null)
                            _instance = new SimulationService();
                    }
                }
                return _instance;
            }
        }

        public event PropertyChangedEventHandler PropertyChanged;
        public event Action<string> StatusChanged;
        public event Action<SimulationState> StateChanged;

        // Reactive state stream - subscribers receive current state and all updates
        private readonly BehaviorSubject<SimulationStateUpdate> _stateSubject =
            new BehaviorSubject<SimulationStateUpdate>(SimulationStateUpdate.Idle);

        /// <summary>
        /// Observable stream of simulation state updates.
        /// New subscribers immediately receive the current state.
        /// Use this for UI binding instead of PropertyChanged events.
        /// </summary>
        public IObservable<SimulationStateUpdate> StateStream => _stateSubject.AsObservable();

        // Track total symbols for progress calculation
        private int _totalSymbolsToLoad;

        // Loaded historical ticks indexed by symbol
        private readonly ConcurrentDictionary<string, List<TickData>> _loadedTicks = new ConcurrentDictionary<string, List<TickData>>();

        // Merged timeline of all ticks sorted by time
        private List<(DateTime Time, string Symbol, double Price)> _tickTimeline = new List<(DateTime, string, double)>();
        private int _currentTickIndex;

        // Generated option symbols
        private readonly List<string> _optionSymbols = new List<string>();

        // Playback state
        private DispatcherTimer _playbackTimer;
        private SimulationConfig _config;
        private DateTime _currentSimTime;
        private DateTime _targetSimTime;  // Target simulation time that accumulates based on wall-clock elapsed
        private DateTime _lastTickTime;
        private int _pricesInjectedCount;
        private int _loadedSymbolCount;
        private int _totalTickCount;

        // State properties
        private SimulationState _state = SimulationState.Idle;
        public SimulationState State
        {
            get => _state;
            private set
            {
                if (_state != value)
                {
                    _state = value;
                    OnPropertyChanged();
                    StateChanged?.Invoke(value);
                    PublishStateUpdate();
                }
            }
        }

        private string _statusMessage = "Idle";
        public string StatusMessage
        {
            get => _statusMessage;
            private set
            {
                if (_statusMessage != value)
                {
                    _statusMessage = value;
                    OnPropertyChanged();
                    StatusChanged?.Invoke(value);
                    PublishStateUpdate();
                }
            }
        }

        public DateTime CurrentSimTime
        {
            get => _currentSimTime;
            private set { _currentSimTime = value; OnPropertyChanged(); OnPropertyChanged(nameof(CurrentSimTimeDisplay)); OnPropertyChanged(nameof(Progress)); PublishStateUpdate(); }
        }

        public string CurrentSimTimeDisplay => CurrentSimTime != DateTime.MinValue ? CurrentSimTime.ToString("HH:mm:ss") : "--:--:--";

        public int LoadedSymbolCount
        {
            get => _loadedSymbolCount;
            private set { _loadedSymbolCount = value; OnPropertyChanged(); PublishStateUpdate(); }
        }

        public int TotalTickCount
        {
            get => _totalTickCount;
            private set { _totalTickCount = value; OnPropertyChanged(); PublishStateUpdate(); }
        }

        public int PricesInjectedCount
        {
            get => _pricesInjectedCount;
            private set { _pricesInjectedCount = value; OnPropertyChanged(); PublishStateUpdate(); }
        }

        /// <summary>
        /// Progress as percentage (0-100)
        /// </summary>
        public double Progress
        {
            get
            {
                if (_tickTimeline == null || _tickTimeline.Count == 0) return 0;
                return Math.Min(100, Math.Max(0, ((double)_currentTickIndex / _tickTimeline.Count) * 100));
            }
        }

        public bool IsRunning => State == SimulationState.Playing;
        public bool IsPaused => State == SimulationState.Paused;
        public bool CanStart => State == SimulationState.Ready || State == SimulationState.Paused;
        public bool CanPause => State == SimulationState.Playing;
        public bool CanStop => State == SimulationState.Playing || State == SimulationState.Paused;

        /// <summary>
        /// Returns true if simulation is actively running (Playing state)
        /// </summary>
        public bool IsSimulationActive => State == SimulationState.Playing;

        /// <summary>
        /// Returns true if we are in simulation mode (data loaded and ready, playing, or paused).
        /// Use this to check if Option Chain should use simulation data vs live data.
        /// </summary>
        public bool IsSimulationMode => State == SimulationState.Ready ||
                                         State == SimulationState.Playing ||
                                         State == SimulationState.Paused ||
                                         State == SimulationState.Completed;

        /// <summary>
        /// Gets the current simulation configuration (underlying, expiry, etc.)
        /// Returns null if not in simulation mode.
        /// </summary>
        public SimulationConfig CurrentConfig => IsSimulationMode ? _config : null;

        /// <summary>
        /// Gets the current time to use for TBS logic.
        /// Returns simulation time when simulation is active, otherwise real system time.
        /// </summary>
        public TimeSpan GetCurrentTimeOfDay()
        {
            if (IsSimulationActive && CurrentSimTime != DateTime.MinValue)
            {
                return CurrentSimTime.TimeOfDay;
            }
            return DateTime.Now.TimeOfDay;
        }

        private SimulationService()
        {
            _log.Info("[SimulationService] Initialized");
        }

        /// <summary>
        /// Sets the simulation config from settings loaded from config.json.
        /// Called by Connector when simulation mode is enabled in config.
        /// This allows the SimulationEngineWindow to read the config and populate UI.
        /// </summary>
        public void SetConfigFromSettings(SimulationConfig config)
        {
            if (config == null)
            {
                _log.Warn("[SimulationService] SetConfigFromSettings: null config provided");
                return;
            }

            _config = config;
            _log.Info($"[SimulationService] SetConfigFromSettings: Config pre-loaded from config.json - " +
                      $"Date={config.SimulationDate:yyyy-MM-dd}, Underlying={config.Underlying}, " +
                      $"Expiry={config.ExpiryDate:yyyy-MM-dd}, ATM={config.ATMStrike}, " +
                      $"Time={config.TimeFrom}-{config.TimeTo}");
        }

        /// <summary>
        /// Gets the pre-loaded config from config.json settings.
        /// Returns null if no config was set via SetConfigFromSettings.
        /// Used by SimulationEngineWindow to populate UI on startup.
        /// </summary>
        public SimulationConfig GetPreloadedConfig()
        {
            return _config;
        }

        /// <summary>
        /// Load historical tick data for all option symbols based on configuration
        /// </summary>
        public async Task<bool> LoadHistoricalBars(SimulationConfig config)
        {
            if (config == null)
            {
                StatusMessage = "Error: No configuration provided";
                return false;
            }

            if (config.ProjectedOpen <= 0)
            {
                StatusMessage = "Error: Projected Open must be > 0";
                return false;
            }

            _config = config;
            State = SimulationState.Loading;
            StatusMessage = "Generating option symbols...";
            _loadedTicks.Clear();
            _tickTimeline.Clear();
            _optionSymbols.Clear();
            LoadedSymbolCount = 0;
            TotalTickCount = 0;
            PricesInjectedCount = 0;
            _currentTickIndex = 0;

            try
            {
                // Generate option symbols
                var symbols = GenerateOptionSymbols(config);
                _optionSymbols.AddRange(symbols);
                _totalSymbolsToLoad = symbols.Count;
                _log.Info($"[SimulationService] Generated {symbols.Count} option symbols for {config.Underlying}");

                StatusMessage = $"Loading ticks for {symbols.Count} symbols...";

                // Use direct NCD file reading for MUCH faster loading
                // This bypasses NinjaTrader's slow BarsRequest API and reads directly from the database files
                var loadStart = DateTime.Now;
                int successCount = 0;
                int failCount = 0;
                object lockObj = new object();

                // Parallel loading - direct file I/O is fast and parallelizes well
                await Task.Run(() =>
                {
                    Parallel.ForEach(symbols, new ParallelOptions { MaxDegreeOfParallelism = 8 }, symbol =>
                    {
                        try
                        {
                            var ticks = NCDFileReader.LoadTicksForSymbol(
                                symbol,
                                config.SimulationDate,
                                config.TimeFrom,
                                config.TimeTo);

                            if (ticks.Count > 0)
                            {
                                // Convert NCDTick to TickData
                                var tickDataList = ticks.Select(t => new TickData
                                {
                                    Time = t.Timestamp,
                                    Price = t.Price,
                                    Volume = (long)t.Volume
                                }).ToList();

                                _loadedTicks[symbol] = tickDataList;

                                lock (lockObj)
                                {
                                    successCount++;
                                    LoadedSymbolCount = successCount;
                                }
                            }
                            else
                            {
                                lock (lockObj)
                                {
                                    failCount++;
                                }
                            }
                        }
                        catch (Exception ex)
                        {
                            _log.Warn($"[SimulationService] Error loading {symbol}: {ex.Message}");
                            lock (lockObj)
                            {
                                failCount++;
                            }
                        }
                    });
                });

                var loadElapsed = DateTime.Now - loadStart;
                _log.Info($"[SimulationService] Direct NCD loading completed: {successCount} symbols in {loadElapsed.TotalMilliseconds:F0}ms");

                if (successCount == 0)
                {
                    State = SimulationState.Error;
                    StatusMessage = "Error: No historical tick data found for any symbol";
                    return false;
                }

                // Build merged timeline of all ticks
                StatusMessage = "Building tick timeline...";
                BuildTickTimeline();

                State = SimulationState.Ready;
                StatusMessage = $"Ready: {successCount} symbols, {TotalTickCount} ticks loaded";
                _log.Info($"[SimulationService] Load complete: {successCount} symbols, {TotalTickCount} total ticks");

                return true;
            }
            catch (Exception ex)
            {
                _log.Error($"[SimulationService] LoadHistoricalBars error: {ex.Message}", ex);
                State = SimulationState.Error;
                StatusMessage = $"Error: {ex.Message}";
                return false;
            }
        }

        /// <summary>
        /// Build a merged timeline of all ticks sorted by time
        /// </summary>
        private void BuildTickTimeline()
        {
            _tickTimeline.Clear();

            foreach (var kvp in _loadedTicks)
            {
                foreach (var tick in kvp.Value)
                {
                    _tickTimeline.Add((tick.Time, kvp.Key, tick.Price));
                }
            }

            // Sort by time
            _tickTimeline = _tickTimeline.OrderBy(t => t.Time).ToList();
            TotalTickCount = _tickTimeline.Count;

            _log.Info($"[SimulationService] Built timeline with {_tickTimeline.Count} ticks");
            if (_tickTimeline.Count > 0)
            {
                _log.Info($"[SimulationService] Timeline range: {_tickTimeline.First().Time:HH:mm:ss.fff} to {_tickTimeline.Last().Time:HH:mm:ss.fff}");
            }
        }

        /// <summary>
        /// Generate option symbols based on configuration.
        /// Uses SymbolHelper.BuildOptionSymbol for consistent symbol formatting.
        /// </summary>
        private List<string> GenerateOptionSymbols(SimulationConfig config)
        {
            var symbols = new List<string>();
            decimal atmStrike = config.ATMStrike;
            int stepSize = config.StepSize;
            int strikeCount = config.StrikeCount;
            string prefix = config.SymbolPrefix?.Trim() ?? "";

            // Check if this is a monthly expiry (affects symbol format)
            // For simulation, we default to monthly format unless we have expiry list
            var expiries = MarketAnalyzerLogic.Instance?.GetCachedExpiries(config.Underlying);
            bool isMonthlyExpiry = expiries != null && expiries.Count > 0
                ? SymbolHelper.IsMonthlyExpiry(config.ExpiryDate, expiries)
                : true; // Default to monthly format for simulation

            for (int i = -strikeCount; i <= strikeCount; i++)
            {
                decimal strike = atmStrike + (i * stepSize);

                string ceSymbol = FormatOptionSymbolForSimulation(config.Underlying, config.ExpiryDate, strike, "CE", prefix, isMonthlyExpiry);
                symbols.Add(ceSymbol);

                string peSymbol = FormatOptionSymbolForSimulation(config.Underlying, config.ExpiryDate, strike, "PE", prefix, isMonthlyExpiry);
                symbols.Add(peSymbol);
            }

            return symbols;
        }

        /// <summary>
        /// Format option symbol for simulation.
        /// Handles prefix override (for custom DB lookups) or delegates to SymbolHelper.
        /// </summary>
        private string FormatOptionSymbolForSimulation(string underlying, DateTime expiry, decimal strike, string optionType, string prefix, bool isMonthlyExpiry)
        {
            // Prefix override: used when loading from NinjaTrader DB with custom naming
            if (!string.IsNullOrEmpty(prefix))
            {
                return $"{prefix}{strike:F0}{optionType}";
            }

            // Use centralized SymbolHelper for consistent symbol generation
            return SymbolHelper.BuildOptionSymbol(underlying, expiry, strike, optionType, isMonthlyExpiry);
        }

        /// <summary>
        /// Generates MappedInstrument list for simulation, enabling Option Chain to display
        /// the simulated option chain structure (underlying, expiry, strikes).
        /// </summary>
        private List<MappedInstrument> GenerateOptionsForSimulation(SimulationConfig config)
        {
            var instruments = new List<MappedInstrument>();
            decimal atmStrike = config.ATMStrike;
            int stepSize = config.StepSize;
            int strikeCount = config.StrikeCount;
            string prefix = config.SymbolPrefix?.Trim() ?? "";

            // Determine segment based on underlying
            string segment = config.Underlying.Contains("SENSEX") ? "BFO-OPT" : "NFO-OPT";
            int lotSize = config.Underlying == "SENSEX" ? 10 : 50;

            // Check if this is a monthly expiry
            var expiries = MarketAnalyzerLogic.Instance?.GetCachedExpiries(config.Underlying);
            bool isMonthlyExpiry = expiries != null && expiries.Count > 0
                ? SymbolHelper.IsMonthlyExpiry(config.ExpiryDate, expiries)
                : true;

            for (int i = -strikeCount; i <= strikeCount; i++)
            {
                decimal strike = atmStrike + (i * stepSize);

                // Generate CE
                string ceSymbol = FormatOptionSymbolForSimulation(config.Underlying, config.ExpiryDate, strike, "CE", prefix, isMonthlyExpiry);
                instruments.Add(new MappedInstrument
                {
                    symbol = ceSymbol,
                    zerodhaSymbol = ceSymbol,
                    underlying = config.Underlying,
                    expiry = config.ExpiryDate,
                    strike = (double)strike,
                    option_type = "CE",
                    segment = segment,
                    tick_size = 0.05,
                    lot_size = lotSize,
                    instrument_token = 0 // Simulation doesn't need real token
                });

                // Generate PE
                string peSymbol = FormatOptionSymbolForSimulation(config.Underlying, config.ExpiryDate, strike, "PE", prefix, isMonthlyExpiry);
                instruments.Add(new MappedInstrument
                {
                    symbol = peSymbol,
                    zerodhaSymbol = peSymbol,
                    underlying = config.Underlying,
                    expiry = config.ExpiryDate,
                    strike = (double)strike,
                    option_type = "PE",
                    segment = segment,
                    tick_size = 0.05,
                    lot_size = lotSize,
                    instrument_token = 0
                });
            }

            _log.Info($"[SimulationService] GenerateOptionsForSimulation: Generated {instruments.Count} MappedInstruments for {config.Underlying}");
            return instruments;
        }

        /// <summary>
        /// Publishes simulated option chain to MarketDataReactiveHub.
        /// This triggers OptionChainWindow to regenerate its rows with simulation data.
        /// Call this after LoadHistoricalBars() succeeds.
        /// </summary>
        public void PublishSimulatedOptionChain()
        {
            if (_config == null)
            {
                _log.Warn("[SimulationService] PublishSimulatedOptionChain: No config available");
                return;
            }

            _log.Info($"[SimulationService] PublishSimulatedOptionChain: Publishing option chain for {_config.Underlying} {_config.ExpiryDate:dd-MMM-yyyy}");

            // Generate MappedInstrument list
            var options = GenerateOptionsForSimulation(_config);

            // Use calculated DTE from config (SimulationDate to ExpiryDate)
            int dte = _config.CalculatedDTE;

            // Publish to MarketDataReactiveHub - this will trigger OptionChainWindow.OnOptionsGenerated
            MarketDataReactiveHub.Instance.PublishOptionsGenerated(
                options,
                _config.Underlying,
                _config.ExpiryDate,
                dte,
                (double)_config.ATMStrike,
                (double)_config.ProjectedOpen
            );

            // Also set ATM strike in MarketAnalyzerLogic for consistency
            MarketAnalyzerLogic.Instance.SetATMStrike(_config.Underlying, _config.ATMStrike);

            _log.Info($"[SimulationService] PublishSimulatedOptionChain: Published {options.Count} options, ATM={_config.ATMStrike}, DTE={dte}");
        }

        /// <summary>
        /// Load tick data for a single symbol from NinjaTrader database
        /// </summary>
        private async Task<bool> LoadTicksForSymbol(string symbol, SimulationConfig config)
        {
            var tcs = new TaskCompletionSource<bool>();

            try
            {
                await NinjaTrader.Core.Globals.RandomDispatcher.InvokeAsync(() =>
                {
                    try
                    {
                        // Get instrument
                        Instrument ntInstrument = Instrument.GetInstrument(symbol);

                        if (ntInstrument == null)
                        {
                            _log.Info($"[SimulationService] Instrument {symbol} not found in runtime, creating...");

                            var instrumentDef = new InstrumentDefinition
                            {
                                Symbol = symbol,
                                BrokerSymbol = symbol,
                                Segment = config.Underlying.Contains("SENSEX") ? "BSE" : "NSE",
                                MarketType = MarketType.UsdM
                            };

                            string ntName = "";
                            bool created = InstrumentManager.Instance.CreateInstrument(instrumentDef, out ntName);

                            if (created || !string.IsNullOrEmpty(ntName))
                            {
                                _log.Info($"[SimulationService] Created NT instrument: {ntName}");
                                ntInstrument = Instrument.GetInstrument(ntName);
                            }
                        }

                        if (ntInstrument == null)
                        {
                            _log.Info($"[SimulationService] Failed to get/create instrument for: {symbol}");
                            tcs.TrySetResult(false);
                            return;
                        }

                        // NinjaTrader BarsRequest uses local time (IST) for both request and response
                        // No timezone conversion needed
                        DateTime fromDateTime = config.SimulationDate.Date + config.TimeFrom;
                        DateTime toDateTime = config.SimulationDate.Date + config.TimeTo;
                        // Request full day's data (from midnight to midnight) to ensure we get all ticks
                        DateTime fromDateTimeWithBuffer = config.SimulationDate.Date;
                        DateTime toDateTimeWithBuffer = config.SimulationDate.Date.AddDays(1);

                        _log.Info($"[SimulationService] Loading TICK data for {symbol}");
                        _log.Info($"[SimulationService]   Request range (local): {fromDateTimeWithBuffer:yyyy-MM-dd HH:mm} to {toDateTimeWithBuffer:yyyy-MM-dd HH:mm}");
                        _log.Info($"[SimulationService]   Filter range (local): {fromDateTime:HH:mm} to {toDateTime:HH:mm}");

                        // Use BarsRequest with date range constructor - requesting full simulation day
                        BarsRequest barsRequest = null;
                        try
                        {
                            barsRequest = new BarsRequest(ntInstrument, fromDateTimeWithBuffer, toDateTimeWithBuffer);
                        }
                        catch (Exception barsEx)
                        {
                            _log.Error($"[SimulationService] BarsRequest constructor failed for {symbol}: {barsEx.Message}");
                            tcs.TrySetResult(false);
                            return;
                        }

                        if (barsRequest == null)
                        {
                            _log.Error($"[SimulationService] BarsRequest is null for {symbol}");
                            tcs.TrySetResult(false);
                            return;
                        }

                        barsRequest.BarsPeriod = new BarsPeriod
                        {
                            BarsPeriodType = BarsPeriodType.Tick,
                            Value = 1  // 1 tick per bar
                        };

                        // Try to get trading hours, use default if not available
                        var tradingHours = TradingHours.Get("Default 24 x 7");
                        if (tradingHours != null)
                        {
                            barsRequest.TradingHours = tradingHours;
                        }
                        else
                        {
                            _log.Warn($"[SimulationService] TradingHours 'Default 24 x 7' not found, using default");
                        }

                        string symbolClosure = symbol;
                        DateTime filterFrom = fromDateTime;
                        DateTime filterTo = toDateTime;
                        DateTime simDate = config.SimulationDate.Date;
                        var requestStartTime = DateTime.Now;

                        barsRequest.Request((request, errorCode, errorMessage) =>
                        {
                            var dbReadTime = (DateTime.Now - requestStartTime).TotalMilliseconds;
                            try
                            {
                                if (errorCode == ErrorCode.NoError && request.Bars != null && request.Bars.Count > 0)
                                {
                                    var ticks = new List<TickData>();

                                    _log.Info($"[SimulationService] BarsRequest returned {request.Bars.Count} ticks for {symbolClosure} (DB read: {dbReadTime:F0}ms)");

                                    if (request.Bars.Count > 0)
                                    {
                                        var firstTick = request.Bars.GetTime(0);
                                        var lastTick = request.Bars.GetTime(request.Bars.Count - 1);
                                        _log.Info($"[SimulationService] {symbolClosure} First tick (local): {firstTick:yyyy-MM-dd HH:mm:ss.fff}, Last tick (local): {lastTick:yyyy-MM-dd HH:mm:ss.fff}");
                                    }

                                    for (int i = 0; i < request.Bars.Count; i++)
                                    {
                                        DateTime tickTime = request.Bars.GetTime(i);

                                        // Filter: must be on simulation date AND within time range
                                        if (tickTime.Date == simDate && tickTime >= filterFrom && tickTime <= filterTo)
                                        {
                                            ticks.Add(new TickData
                                            {
                                                Time = tickTime,  // Already in local time, no conversion needed
                                                Price = request.Bars.GetClose(i),  // For tick bars, Close = tick price
                                                Volume = (long)request.Bars.GetVolume(i)
                                            });
                                        }
                                    }

                                    if (ticks.Count > 0)
                                    {
                                        _loadedTicks[symbolClosure] = ticks.OrderBy(t => t.Time).ToList();
                                        _log.Info($"[SimulationService] Loaded {ticks.Count} ticks for {symbolClosure}");
                                        tcs.TrySetResult(true);
                                    }
                                    else
                                    {
                                        _log.Info($"[SimulationService] No ticks in time range for {symbolClosure}");
                                        tcs.TrySetResult(false);
                                    }
                                }
                                else
                                {
                                    _log.Info($"[SimulationService] BarsRequest failed for {symbolClosure}: {errorCode} - {errorMessage}");
                                    tcs.TrySetResult(false);
                                }

                                request.Dispose();
                            }
                            catch (Exception ex)
                            {
                                _log.Error($"[SimulationService] Error processing ticks for {symbolClosure}: {ex.Message}");
                                tcs.TrySetResult(false);
                            }
                        });
                    }
                    catch (Exception ex)
                    {
                        _log.Error($"[SimulationService] Error creating BarsRequest for {symbol}: {ex.Message}");
                        tcs.TrySetResult(false);
                    }
                });

                // Wait with timeout - 15 seconds as in original
                var completedTask = await Task.WhenAny(tcs.Task, Task.Delay(15000));
                if (completedTask == tcs.Task)
                {
                    return await tcs.Task;
                }
                else
                {
                    _log.Warn($"[SimulationService] Timeout loading ticks for {symbol}");
                    return false;
                }
            }
            catch (Exception ex)
            {
                _log.Error($"[SimulationService] LoadTicksForSymbol error for {symbol}: {ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// Start playback
        /// </summary>
        public void Start()
        {
            if (!CanStart)
            {
                _log.Warn($"[SimulationService] Cannot start: State={State}");
                return;
            }

            if (_config == null || _tickTimeline.Count == 0)
            {
                StatusMessage = "Error: No tick data loaded";
                return;
            }

            // Set ATM strike
            MarketAnalyzerLogic.Instance.SetATMStrike(_config.Underlying, _config.ATMStrike);

            // Initialize if fresh start
            if (State == SimulationState.Ready)
            {
                _currentTickIndex = 0;
                PricesInjectedCount = 0;
                CurrentSimTime = _tickTimeline[0].Time;
                _targetSimTime = _tickTimeline[0].Time;  // Initialize target time to first tick
                _lastTickTime = DateTime.Now;
            }
            else if (State == SimulationState.Paused)
            {
                // Resuming from pause - reset last tick time but keep target sim time
                _lastTickTime = DateTime.Now;
            }

            // Create timer - tick every 100ms to check if next tick should fire
            // At 1x speed: ticks play at their actual time intervals
            // At 10x speed: 10 seconds of data per 1 second of wall clock
            _playbackTimer = new DispatcherTimer
            {
                Interval = TimeSpan.FromMilliseconds(100) // Check every 100ms
            };
            _playbackTimer.Tick += OnPlaybackTick;
            _playbackTimer.Start();

            State = SimulationState.Playing;
            StatusMessage = $"Playing at {_config.SpeedMultiplier}x speed...";
            _log.Info($"[SimulationService] Started tick playback at {CurrentSimTime:HH:mm:ss.fff}, speed={_config.SpeedMultiplier}x, total ticks={_tickTimeline.Count}");
        }

        /// <summary>
        /// Pause playback
        /// </summary>
        public void Pause()
        {
            if (!CanPause)
            {
                _log.Warn($"[SimulationService] Cannot pause: State={State}");
                return;
            }

            _playbackTimer?.Stop();
            State = SimulationState.Paused;
            StatusMessage = $"Paused at {CurrentSimTime:HH:mm:ss}";
            _log.Info($"[SimulationService] Paused at {CurrentSimTime:HH:mm:ss}");
        }

        /// <summary>
        /// Stop playback and reset
        /// </summary>
        public void Stop()
        {
            if (!CanStop && State != SimulationState.Completed)
            {
                _log.Warn($"[SimulationService] Cannot stop: State={State}");
                return;
            }

            _playbackTimer?.Stop();
            _playbackTimer = null;

            _currentTickIndex = 0;
            CurrentSimTime = DateTime.MinValue;
            PricesInjectedCount = 0;

            State = SimulationState.Ready;
            StatusMessage = "Stopped - Ready to replay";
            _log.Info("[SimulationService] Stopped");
        }

        /// <summary>
        /// Set playback speed
        /// </summary>
        public void SetSpeed(int multiplier)
        {
            if (_config != null)
            {
                _config.SpeedMultiplier = multiplier;
            }

            if (State == SimulationState.Playing)
            {
                StatusMessage = $"Playing at {multiplier}x speed...";
            }

            _log.Info($"[SimulationService] Speed set to {multiplier}x");
        }

        // Maximum ticks to process per timer callback to prevent UI freeze
        private const int MAX_TICKS_PER_CALLBACK = 500;

        /// <summary>
        /// Playback timer tick handler - processes ticks based on elapsed wall-clock time.
        /// Limits ticks per callback to prevent UI thread starvation at high speeds.
        /// </summary>
        private void OnPlaybackTick(object sender, EventArgs e)
        {
            if (State != SimulationState.Playing || _config == null || _tickTimeline.Count == 0)
                return;

            // Check if we've reached the end
            if (_currentTickIndex >= _tickTimeline.Count)
            {
                _playbackTimer?.Stop();
                State = SimulationState.Completed;
                StatusMessage = $"Completed - {PricesInjectedCount} ticks injected";
                _log.Info($"[SimulationService] Playback completed at {CurrentSimTime:HH:mm:ss}");
                return;
            }

            // Calculate how much simulation time has passed based on wall clock and speed
            // We track total elapsed simulation time since playback started, not incremental
            var now = DateTime.Now;
            var wallClockElapsed = now - _lastTickTime;
            var simTimeElapsed = TimeSpan.FromTicks((long)(wallClockElapsed.Ticks * _config.SpeedMultiplier));

            // Update the target simulation time - this accumulates over each timer tick
            _targetSimTime = _targetSimTime + simTimeElapsed;

            // Process ticks up to _targetSimTime, but limit to MAX_TICKS_PER_CALLBACK to prevent UI freeze
            int ticksInjected = 0;
            while (_currentTickIndex < _tickTimeline.Count && ticksInjected < MAX_TICKS_PER_CALLBACK)
            {
                var tick = _tickTimeline[_currentTickIndex];

                if (tick.Time <= _targetSimTime)
                {
                    // Inject this tick
                    MarketAnalyzerLogic.Instance.UpdateOptionPrice(tick.Symbol, (decimal)tick.Price, tick.Time);
                    SubscriptionManager.Instance.InjectSimulatedPrice(tick.Symbol, tick.Price, tick.Time);

                    _currentTickIndex++;
                    ticksInjected++;
                    CurrentSimTime = tick.Time;
                }
                else
                {
                    // Next tick is in the future, wait
                    break;
                }
            }

            if (ticksInjected > 0)
            {
                PricesInjectedCount += ticksInjected;

                // Update ATM periodically
                if (PricesInjectedCount % 100 == 0)
                {
                    UpdateATMStrike();
                }
            }

            _lastTickTime = now;
            OnPropertyChanged(nameof(Progress));
        }

        /// <summary>
        /// Update ATM strike based on straddle prices
        /// </summary>
        private void UpdateATMStrike()
        {
            if (_config == null) return;

            decimal minStraddle = decimal.MaxValue;
            decimal bestStrike = _config.ATMStrike;

            foreach (var symbol in _optionSymbols.Where(s => s.EndsWith("CE")))
            {
                var peSymbol = symbol.Replace("CE", "PE");

                var cePrice = MarketAnalyzerLogic.Instance.GetPrice(symbol);
                var pePrice = MarketAnalyzerLogic.Instance.GetPrice(peSymbol);

                if (cePrice > 0 && pePrice > 0)
                {
                    var straddle = (decimal)cePrice + (decimal)pePrice;
                    if (straddle < minStraddle)
                    {
                        minStraddle = straddle;
                        if (SymbolHelper.TryExtractStrike(symbol, out decimal strike))
                        {
                            bestStrike = strike;
                        }
                    }
                }
            }

            if (bestStrike != _config.ATMStrike)
            {
                MarketAnalyzerLogic.Instance.SetATMStrike(_config.Underlying, bestStrike);
            }
        }

        /// <summary>
        /// Clear all loaded data
        /// </summary>
        public void Reset()
        {
            Stop();
            _loadedTicks.Clear();
            _tickTimeline.Clear();
            _optionSymbols.Clear();
            LoadedSymbolCount = 0;
            TotalTickCount = 0;
            PricesInjectedCount = 0;
            _currentTickIndex = 0;
            State = SimulationState.Idle;
            StatusMessage = "Idle";
            _log.Info("[SimulationService] Reset");
        }

        protected void OnPropertyChanged([CallerMemberName] string propertyName = null)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }

        /// <summary>
        /// Publishes current state to the reactive StateStream.
        /// Call this whenever state or key properties change.
        /// </summary>
        private void PublishStateUpdate()
        {
            try
            {
                var update = new SimulationStateUpdate
                {
                    State = _state,
                    StatusMessage = _statusMessage,
                    LoadedSymbolCount = _loadedSymbolCount,
                    TotalSymbolCount = _totalSymbolsToLoad,
                    TotalTickCount = _totalTickCount,
                    PricesInjectedCount = _pricesInjectedCount,
                    CurrentSimTime = _currentSimTime,
                    Progress = Progress,
                    SpeedMultiplier = _config?.SpeedMultiplier ?? 1,
                    Timestamp = DateTime.Now
                };

                _stateSubject.OnNext(update);
            }
            catch (Exception ex)
            {
                _log.Error($"[SimulationService] PublishStateUpdate error: {ex.Message}");
            }
        }
    }
}
