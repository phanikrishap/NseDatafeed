using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;
using System.Windows.Threading;
using NinjaTrader.Cbi;
using NinjaTrader.Data;
using ZerodhaAPI.Common.Enums;
using ZerodhaDatafeedAdapter.Helpers;
using ZerodhaDatafeedAdapter.Models;
using ZerodhaDatafeedAdapter.Services.Analysis;
using ZerodhaDatafeedAdapter.Services.Instruments;

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
                }
            }
        }

        public DateTime CurrentSimTime
        {
            get => _currentSimTime;
            private set { _currentSimTime = value; OnPropertyChanged(); OnPropertyChanged(nameof(CurrentSimTimeDisplay)); OnPropertyChanged(nameof(Progress)); }
        }

        public string CurrentSimTimeDisplay => CurrentSimTime != DateTime.MinValue ? CurrentSimTime.ToString("HH:mm:ss") : "--:--:--";

        public int LoadedSymbolCount
        {
            get => _loadedSymbolCount;
            private set { _loadedSymbolCount = value; OnPropertyChanged(); }
        }

        public int TotalTickCount
        {
            get => _totalTickCount;
            private set { _totalTickCount = value; OnPropertyChanged(); }
        }

        public int PricesInjectedCount
        {
            get => _pricesInjectedCount;
            private set { _pricesInjectedCount = value; OnPropertyChanged(); }
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
            Logger.Info("[SimulationService] Initialized");
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
                Logger.Info($"[SimulationService] Generated {symbols.Count} option symbols for {config.Underlying}");

                StatusMessage = $"Loading ticks for {symbols.Count} symbols...";

                // Load ticks for each symbol
                int successCount = 0;

                foreach (var symbol in symbols)
                {
                    bool loaded = await LoadTicksForSymbol(symbol, config);
                    if (loaded)
                    {
                        successCount++;
                        LoadedSymbolCount = successCount;
                        StatusMessage = $"Loaded {successCount}/{symbols.Count} symbols...";
                    }
                }

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
                Logger.Info($"[SimulationService] Load complete: {successCount} symbols, {TotalTickCount} total ticks");

                return true;
            }
            catch (Exception ex)
            {
                Logger.Error($"[SimulationService] LoadHistoricalBars error: {ex.Message}", ex);
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

            Logger.Info($"[SimulationService] Built timeline with {_tickTimeline.Count} ticks");
            if (_tickTimeline.Count > 0)
            {
                Logger.Info($"[SimulationService] Timeline range: {_tickTimeline.First().Time:HH:mm:ss.fff} to {_tickTimeline.Last().Time:HH:mm:ss.fff}");
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
                            Logger.Info($"[SimulationService] Instrument {symbol} not found in runtime, creating...");

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
                                Logger.Info($"[SimulationService] Created NT instrument: {ntName}");
                                ntInstrument = Instrument.GetInstrument(ntName);
                            }
                        }

                        if (ntInstrument == null)
                        {
                            Logger.Info($"[SimulationService] Failed to get/create instrument for: {symbol}");
                            tcs.TrySetResult(false);
                            return;
                        }

                        // NinjaTrader BarsRequest uses local time (IST) for both request and response
                        // No timezone conversion needed
                        DateTime fromDateTime = config.SimulationDate.Date + config.TimeFrom;
                        DateTime toDateTime = config.SimulationDate.Date + config.TimeTo;
                        // Add buffer for request
                        DateTime fromDateTimeWithBuffer = fromDateTime.AddMinutes(-5);
                        DateTime toDateTimeWithBuffer = toDateTime.AddMinutes(5);

                        Logger.Info($"[SimulationService] Loading TICK data for {symbol}");
                        Logger.Info($"[SimulationService]   Request range (local): {fromDateTimeWithBuffer:yyyy-MM-dd HH:mm} to {toDateTimeWithBuffer:yyyy-MM-dd HH:mm}");
                        Logger.Info($"[SimulationService]   Filter range (local): {fromDateTime:HH:mm} to {toDateTime:HH:mm}");

                        // Use BarsRequest with TICK data type - NinjaTrader uses local time
                        var barsRequest = new BarsRequest(ntInstrument, fromDateTimeWithBuffer, toDateTimeWithBuffer);
                        barsRequest.BarsPeriod = new BarsPeriod
                        {
                            BarsPeriodType = BarsPeriodType.Tick,
                            Value = 1  // 1 tick per bar
                        };
                        barsRequest.TradingHours = TradingHours.Get("Default 24 x 7");

                        string symbolClosure = symbol;
                        DateTime filterFrom = fromDateTime;
                        DateTime filterTo = toDateTime;

                        barsRequest.Request((request, errorCode, errorMessage) =>
                        {
                            try
                            {
                                if (errorCode == ErrorCode.NoError && request.Bars != null && request.Bars.Count > 0)
                                {
                                    var ticks = new List<TickData>();

                                    Logger.Info($"[SimulationService] BarsRequest returned {request.Bars.Count} ticks for {symbolClosure}");

                                    if (request.Bars.Count > 0)
                                    {
                                        var firstTick = request.Bars.GetTime(0);
                                        var lastTick = request.Bars.GetTime(request.Bars.Count - 1);
                                        Logger.Info($"[SimulationService] {symbolClosure} First tick (local): {firstTick:yyyy-MM-dd HH:mm:ss.fff}, Last tick (local): {lastTick:yyyy-MM-dd HH:mm:ss.fff}");
                                    }

                                    for (int i = 0; i < request.Bars.Count; i++)
                                    {
                                        DateTime tickTime = request.Bars.GetTime(i);

                                        // Filter within time range (tick times are already in local time)
                                        if (tickTime >= filterFrom && tickTime <= filterTo)
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
                                        Logger.Info($"[SimulationService] Loaded {ticks.Count} ticks for {symbolClosure}");
                                        tcs.TrySetResult(true);
                                    }
                                    else
                                    {
                                        Logger.Info($"[SimulationService] No ticks in time range for {symbolClosure}");
                                        tcs.TrySetResult(false);
                                    }
                                }
                                else
                                {
                                    Logger.Info($"[SimulationService] BarsRequest failed for {symbolClosure}: {errorCode} - {errorMessage}");
                                    tcs.TrySetResult(false);
                                }

                                request.Dispose();
                            }
                            catch (Exception ex)
                            {
                                Logger.Error($"[SimulationService] Error processing ticks for {symbolClosure}: {ex.Message}");
                                tcs.TrySetResult(false);
                            }
                        });
                    }
                    catch (Exception ex)
                    {
                        Logger.Error($"[SimulationService] Error creating BarsRequest for {symbol}: {ex.Message}");
                        tcs.TrySetResult(false);
                    }
                });

                // Wait with timeout
                var completedTask = await Task.WhenAny(tcs.Task, Task.Delay(15000));
                if (completedTask == tcs.Task)
                {
                    return await tcs.Task;
                }
                else
                {
                    Logger.Warn($"[SimulationService] Timeout loading ticks for {symbol}");
                    return false;
                }
            }
            catch (Exception ex)
            {
                Logger.Error($"[SimulationService] LoadTicksForSymbol error for {symbol}: {ex.Message}");
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
                Logger.Warn($"[SimulationService] Cannot start: State={State}");
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
            Logger.Info($"[SimulationService] Started tick playback at {CurrentSimTime:HH:mm:ss.fff}, speed={_config.SpeedMultiplier}x, total ticks={_tickTimeline.Count}");
        }

        /// <summary>
        /// Pause playback
        /// </summary>
        public void Pause()
        {
            if (!CanPause)
            {
                Logger.Warn($"[SimulationService] Cannot pause: State={State}");
                return;
            }

            _playbackTimer?.Stop();
            State = SimulationState.Paused;
            StatusMessage = $"Paused at {CurrentSimTime:HH:mm:ss}";
            Logger.Info($"[SimulationService] Paused at {CurrentSimTime:HH:mm:ss}");
        }

        /// <summary>
        /// Stop playback and reset
        /// </summary>
        public void Stop()
        {
            if (!CanStop && State != SimulationState.Completed)
            {
                Logger.Warn($"[SimulationService] Cannot stop: State={State}");
                return;
            }

            _playbackTimer?.Stop();
            _playbackTimer = null;

            _currentTickIndex = 0;
            CurrentSimTime = DateTime.MinValue;
            PricesInjectedCount = 0;

            State = SimulationState.Ready;
            StatusMessage = "Stopped - Ready to replay";
            Logger.Info("[SimulationService] Stopped");
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

            Logger.Info($"[SimulationService] Speed set to {multiplier}x");
        }

        /// <summary>
        /// Playback timer tick handler - processes ticks based on elapsed wall-clock time
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
                Logger.Info($"[SimulationService] Playback completed at {CurrentSimTime:HH:mm:ss}");
                return;
            }

            // Calculate how much simulation time has passed based on wall clock and speed
            // We track total elapsed simulation time since playback started, not incremental
            var now = DateTime.Now;
            var wallClockElapsed = now - _lastTickTime;
            var simTimeElapsed = TimeSpan.FromTicks((long)(wallClockElapsed.Ticks * _config.SpeedMultiplier));

            // Update the target simulation time - this accumulates over each timer tick
            _targetSimTime = _targetSimTime + simTimeElapsed;

            // Process all ticks up to _targetSimTime
            int ticksInjected = 0;
            while (_currentTickIndex < _tickTimeline.Count)
            {
                var tick = _tickTimeline[_currentTickIndex];

                if (tick.Time <= _targetSimTime)
                {
                    // Inject this tick
                    MarketAnalyzerLogic.Instance.UpdateOptionPrice(tick.Symbol, (decimal)tick.Price, tick.Time);
                    SubscriptionManager.Instance.InjectSimulatedPrice(tick.Symbol, tick.Price);

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
                    var straddle = cePrice + pePrice;
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
            Logger.Info("[SimulationService] Reset");
        }

        protected void OnPropertyChanged([CallerMemberName] string propertyName = null)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }
    }
}
