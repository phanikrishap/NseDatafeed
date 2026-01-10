using ZerodhaAPI.Common.Enums;
using ZerodhaAPI.Zerodha.Websockets;
using ZerodhaDatafeedAdapter.Classes;
using ZerodhaDatafeedAdapter.Models;
using ZerodhaDatafeedAdapter.Services.Analysis;
using ZerodhaDatafeedAdapter.Services.Auth;
using ZerodhaDatafeedAdapter.Services.Configuration;
using ZerodhaDatafeedAdapter.Services.Historical;
using ZerodhaDatafeedAdapter.Services.Instruments;
using ZerodhaDatafeedAdapter.Services.MarketData;
using ZerodhaDatafeedAdapter.Services.Zerodha;
using ZerodhaDatafeedAdapter.ViewModels;
using ZerodhaDatafeedAdapter.Logging;
using log4net;
using NinjaTrader.Adapter;
using NinjaTrader.Cbi;
using NinjaTrader.Data;
using NinjaTrader.Gui;
using NinjaTrader.Gui.Tools;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Reactive.Linq;
using System.Reactive.Threading.Tasks;
using System.Xml.Linq;

#nullable disable
namespace ZerodhaDatafeedAdapter
{
    /// <summary>
    /// Main connector class for the Zerodha Ninja Adapter
    /// </summary>
    public class Connector : INotifyPropertyChanged
    {
        private bool _connected;
        private static Connector _instance;

        private readonly ConfigurationManager _configManager;
        private readonly ZerodhaClient _zerodhaClient;
        private readonly InstrumentManager _instrumentManager;
        private readonly HistoricalDataService _historicalDataService;
        private readonly MarketDataService _marketDataService;

        // TaskCompletionSource pattern for robust token ready signaling
        // Unlike events, late subscribers can await this and get the result
        private readonly TaskCompletionSource<bool> _tokenReadyTcs = new TaskCompletionSource<bool>();
        private readonly CancellationTokenSource _tokenValidationCts = new CancellationTokenSource();

        /// <summary>
        /// Task that completes when token validation is done.
        /// Late subscribers can await this - unlike events, they won't miss it.
        /// </summary>
        public Task<bool> WhenTokenReady => _tokenReadyTcs.Task;

        /// <summary>
        /// Gets whether the access token is ready and valid.
        /// Thread-safe property backed by TaskCompletionSource.
        /// </summary>
        public bool IsTokenReady => _tokenReadyTcs.Task.IsCompleted && _tokenReadyTcs.Task.Result;

        /// <summary>
        /// Waits for token to be ready with optional timeout.
        /// Returns true if token is valid, false if invalid or timeout.
        /// Uses TaskCompletionSource pattern - safe for late callers.
        /// </summary>
        /// <param name="timeoutMs">Timeout in milliseconds (default 60 seconds)</param>
        /// <param name="cancellationToken">Optional cancellation token</param>
        public async Task<bool> WaitForTokenReadyAsync(int timeoutMs = 60000, CancellationToken cancellationToken = default)
        {
            // Fast path - already completed
            if (_tokenReadyTcs.Task.IsCompleted)
            {
                Logger.Debug("[Connector] WaitForTokenReadyAsync: Token already ready");
                return _tokenReadyTcs.Task.Result;
            }

            Logger.Info("[Connector] WaitForTokenReadyAsync: Waiting for token validation...");
            try
            {
                using (var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _tokenValidationCts.Token))
                {
                    var timeoutTask = Task.Delay(timeoutMs, linkedCts.Token);
                    var completedTask = await Task.WhenAny(_tokenReadyTcs.Task, timeoutTask);

                    if (completedTask == timeoutTask)
                    {
                        if (linkedCts.Token.IsCancellationRequested)
                        {
                            Logger.Warn("[Connector] WaitForTokenReadyAsync: Cancelled");
                            return false;
                        }
                        Logger.Warn($"[Connector] WaitForTokenReadyAsync: Timeout after {timeoutMs}ms");
                        return false;
                    }

                    var result = await _tokenReadyTcs.Task;
                    Logger.Info($"[Connector] WaitForTokenReadyAsync: Token validation completed, result={result}");
                    return result;
                }
            }
            catch (OperationCanceledException)
            {
                Logger.Warn("[Connector] WaitForTokenReadyAsync: Cancelled");
                return false;
            }
            catch (Exception ex)
            {
                Logger.Error($"[Connector] WaitForTokenReadyAsync error: {ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// Gets the version of the adapter
        /// </summary>
        public string Version { get; } = "2.0.1";

        /// <summary>
        /// Gets whether the adapter is connected
        /// </summary>
        public bool IsConnected
        {
            get => this._connected;
            private set
            {
                if (this._connected == value)
                    return;
                this._connected = value;
                this.OnPropertyChanged(nameof(IsConnected));
            }
        }

        private static ZerodhaAdapter _ZerodhaAdapter;

        /// <summary>
        /// Sets the ZerodhaAdapter instance
        /// </summary>
        /// <param name="adapter">The ZerodhaAdapter instance</param>
        public static void SetAdapter(ZerodhaAdapter adapter)
        {
            _ZerodhaAdapter = adapter;
        }

        /// <summary>
        /// Gets the ZerodhaAdapter instance
        /// </summary>
        /// <returns>The ZerodhaAdapter instance</returns>
        public IAdapter GetAdapter()
        {
            return _ZerodhaAdapter;
        }

        /// <summary>
        /// Gets the singleton instance of the Connector
        /// </summary>
        public static Connector Instance
        {
            get
            {
                if (Connector._instance == null)
                    Connector._instance = new Connector();
                return Connector._instance;
            }
        }

        /// <summary>
        /// Constructor
        /// </summary>
        public Connector()
        {
            Logger.Initialize();
            Logger.Info($"ZerodhaDatafeedAdapter v{Version} initializing...");

            // Initialize dedicated startup logger for critical startup events
            StartupLogger.LogAdapterInit(Version);

            _configManager = ConfigurationManager.Instance;
            _zerodhaClient = ZerodhaClient.Instance;
            _instrumentManager = InstrumentManager.Instance;
            _historicalDataService = HistoricalDataService.Instance;
            _marketDataService = MarketDataService.Instance;
            StartupLogger.LogMilestone("Core services initialized");

            // Load configuration
            if (!_configManager.LoadConfiguration())
            {
                StartupLogger.LogConfigurationLoad(false);
                // Handle configuration failure
                MessageBox.Show("Using default API keys. Please check your configuration file.",
                    "Configuration Warning", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
            else
            {
                StartupLogger.LogConfigurationLoad(true);
            }

            // Check if simulation mode is enabled - if so, skip live data initialization
            if (_configManager.IsSimulationModeEnabled)
            {
                Logger.Info("[Connector] SIMULATION MODE: Skipping live data initialization routines");
                StartupLogger.Info("SIMULATION MODE: Skipping token validation and live data connections");
                NinjaTrader.NinjaScript.NinjaScript.Log(
                    "[ZerodhaAdapter] SIMULATION MODE: Running in offline replay mode",
                    NinjaTrader.Cbi.LogLevel.Information);

                // Signal token as "ready" (though we won't use it)
                _tokenReadyTcs.TrySetResult(true);
                MarketDataReactiveHub.Instance.PublishTokenReady(true);

                // Initialize InstrumentManager in simulation mode (loads from existing DB only, no downloads)
                _ = Task.Run(async () =>
                {
                    try
                    {
                        await InstrumentManager.Instance.InitializeAsync();
                        Logger.Info("[Connector] SIMULATION MODE: InstrumentManager initialized (offline)");

                        // Open SimulationEngineWindow on UI thread
                        OpenSimulationWindowFromConfig();
                    }
                    catch (Exception ex)
                    {
                        Logger.Error($"[Connector] SIMULATION MODE: InstrumentManager initialization error: {ex.Message}");
                    }
                });

                return;
            }

            // Ensure valid access token using TaskCompletionSource pattern
            // Late subscribers can await WhenTokenReady and will get the result
            _ = Task.Run(async () =>
            {
                try
                {
                    StartupLogger.LogTokenValidationStart();
                    Logger.Info("Checking access token validity...");
                    var tokenResult = await _configManager.EnsureValidTokenAsync();

                    if (tokenResult)
                    {
                        Logger.Info("Access token is valid.");
                        StartupLogger.LogTokenValidationResult(true, "Token validated successfully");
                        NinjaTrader.NinjaScript.NinjaScript.Log(
                            "[ZerodhaAdapter] Zerodha access token is valid.",
                            NinjaTrader.Cbi.LogLevel.Information);
                    }
                    else
                    {
                        Logger.Info("Failed to obtain valid access token. Manual login may be required.");
                        StartupLogger.LogTokenValidationResult(false, "Manual login may be required");
                        StartupLogger.LogCriticalError("Token Validation",
                            "Failed to obtain valid access token",
                            "WebSocket connections will fail. Manual Zerodha login required.");
                        NinjaTrader.NinjaScript.NinjaScript.Log(
                            "[ZerodhaAdapter] WARNING: Failed to obtain valid Zerodha access token. Manual login may be required.",
                            NinjaTrader.Cbi.LogLevel.Warning);
                    }

                    // Signal token ready via TaskCompletionSource (safe for late subscribers)
                    Logger.Info($"[Connector] Setting TokenReady via TCS with result={tokenResult}");
                    _tokenReadyTcs.TrySetResult(tokenResult);

                    // Also publish to Rx hub for reactive subscribers (InstrumentManager, etc.)
                    MarketDataReactiveHub.Instance.PublishTokenReady(tokenResult);

                    // Initialize InstrumentManager (downloads instruments if needed)
                    // This awaits TokenReadyStream internally, but since we just published, it will proceed
                    await InstrumentManager.Instance.InitializeAsync();

                    // Initialize Historical Tick Data Coordinator (non-blocking)
                    // This coordinator routes to Accelpix or ICICI based on config
                    // Failures here don't affect Zerodha operations
                    try
                    {
                        Logger.Info("[Connector] Starting Historical Tick Data Coordinator initialization...");

                        // Initialize the coordinator which reads config and initializes the appropriate source
                        // It will initialize Accelpix or ICICI based on HistoricalTickData.PreferredSource config
                        HistoricalTickDataCoordinator.Instance.Initialize();

                        // If ICICI is the active source, also initialize the token service
                        if (HistoricalTickDataCoordinator.Instance.PreferredSource == HistoricalTickDataSource.IciciDirect)
                        {
                            Logger.Info("[Connector] ICICI selected as tick source - initializing broker token service...");
                            IciciDirectTokenService.Instance.Initialize();

                            // Subscribe to status updates (logging only)
                            IciciDirectTokenService.Instance.BrokerStatus
                                .Subscribe(status =>
                                {
                                    Logger.Info($"[ICICI Broker] Status: {status.Message} (Available: {status.IsAvailable})");
                                });
                        }

                        Logger.Info($"[Connector] Historical Tick Data: Enabled={HistoricalTickDataCoordinator.Instance.IsEnabled}, Source={HistoricalTickDataCoordinator.Instance.PreferredSource}");
                    }
                    catch (Exception tickDataEx)
                    {
                        // Historical tick data failures are non-blocking
                        Logger.Info($"[Connector] Historical tick data initialization skipped: {tickDataEx.Message}");
                    }
                }
                catch (Exception ex)
                {
                    var innerMessage = ex.InnerException?.Message ?? ex.Message;
                    Logger.Error($"Token validation error: {innerMessage}");
                    StartupLogger.LogTokenValidationResult(false, innerMessage);
                    StartupLogger.LogCriticalError("Token Validation",
                        $"Exception during token validation: {innerMessage}",
                        "Trading will not be possible until token is manually refreshed.");
                    NinjaTrader.NinjaScript.NinjaScript.Log(
                        $"[ZerodhaAdapter] Token validation error: {innerMessage}",
                        NinjaTrader.Cbi.LogLevel.Error);

                    // Signal failure via TaskCompletionSource
                    _tokenReadyTcs.TrySetResult(false);

                    // Also publish failure to Rx hub
                    MarketDataReactiveHub.Instance.PublishTokenReady(false);
                }
            });
        }

        /// <summary>
        /// Checks the connection to the Zerodha API.
        /// Waits for token validation to complete first using TaskCompletionSource.
        /// </summary>
        /// <returns>True if the connection is valid, false otherwise</returns>
        public bool CheckConnection()
        {
            StartupLogger.Info("CheckConnection: Verifying Zerodha API connectivity...");

            // Wait for TCS to complete using synchronous wait (avoids async/await deadlock on UI thread)
            Logger.Info("[Connector] CheckConnection: Waiting for token validation...");
            try
            {
                // Use synchronous wait with timeout - avoids deadlock that GetAwaiter().GetResult() can cause
                bool completed = _tokenReadyTcs.Task.Wait(TimeSpan.FromSeconds(60));
                if (!completed)
                {
                    Logger.Error("[Connector] CheckConnection: Token validation timed out after 60 seconds.");
                    StartupLogger.LogConnectionCheck(false, "Zerodha (token timeout)");
                    return false;
                }

                bool tokenReady = _tokenReadyTcs.Task.Result;
                Logger.Info($"[Connector] CheckConnection: Token validation completed with result={tokenReady}");

                if (!tokenReady)
                {
                    Logger.Error("[Connector] CheckConnection: Token validation failed.");
                    StartupLogger.LogConnectionCheck(false, "Zerodha (token not ready)");
                    return false;
                }
                Logger.Info("[Connector] CheckConnection: Token is ready, checking Zerodha connection...");
            }
            catch (Exception ex)
            {
                Logger.Error($"[Connector] CheckConnection: Error waiting for token validation: {ex.Message}");
                StartupLogger.Error($"CheckConnection failed: {ex.Message}");
                return false;
            }

            if (!_zerodhaClient.CheckConnection())
            {
                StartupLogger.LogConnectionCheck(false, "Zerodha API");
                return false;
            }

            StartupLogger.LogConnectionCheck(true, "Zerodha API");
            this.IsConnected = true;
            return true;
        }

        /// <summary>
        /// Waits for the instrument database to be ready with timeout.
        /// Uses Rx-based InstrumentDbReadyStream for event-driven waiting.
        /// Returns true if DB is ready, false if timeout or failure.
        /// </summary>
        /// <param name="timeoutMs">Timeout in milliseconds (default 90 seconds - DB download can be slow)</param>
        /// <param name="cancellationToken">Optional cancellation token</param>
        public async Task<bool> WaitForInstrumentDbReadyAsync(int timeoutMs = 90000, CancellationToken cancellationToken = default)
        {
            Logger.Info("[Connector] WaitForInstrumentDbReadyAsync: Waiting for instrument database initialization...");

            try
            {
                // Use Rx to await InstrumentDbReadyStream with timeout
                var hub = MarketDataReactiveHub.Instance;

                var dbReady = await hub.InstrumentDbReadyStream
                    .Timeout(TimeSpan.FromMilliseconds(timeoutMs))
                    .FirstAsync()
                    .ToTask(cancellationToken);

                Logger.Info($"[Connector] WaitForInstrumentDbReadyAsync: Completed with result={dbReady}");
                return dbReady;
            }
            catch (TimeoutException)
            {
                Logger.Error($"[Connector] WaitForInstrumentDbReadyAsync: Timeout after {timeoutMs}ms waiting for instrument database.");
                return false;
            }
            catch (OperationCanceledException)
            {
                Logger.Warn("[Connector] WaitForInstrumentDbReadyAsync: Cancelled");
                return false;
            }
            catch (Exception ex)
            {
                Logger.Error($"[Connector] WaitForInstrumentDbReadyAsync: Error - {ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// Async version of CheckConnection for proper async/await usage.
        /// Preferred over CheckConnection() to avoid blocking.
        /// </summary>
        /// <param name="cancellationToken">Optional cancellation token</param>
        /// <returns>True if the connection is valid, false otherwise</returns>
        public async Task<bool> CheckConnectionAsync(CancellationToken cancellationToken = default)
        {
            Logger.Info("[Connector] CheckConnectionAsync: Waiting for token validation...");
            try
            {
                var tokenReady = await WaitForTokenReadyAsync(timeoutMs: 60000, cancellationToken);
                if (!tokenReady)
                {
                    Logger.Error("[Connector] CheckConnectionAsync: Token validation failed or timed out.");
                    return false;
                }
                Logger.Info("[Connector] CheckConnectionAsync: Token is ready, checking Zerodha connection...");
            }
            catch (OperationCanceledException)
            {
                Logger.Warn("[Connector] CheckConnectionAsync: Cancelled");
                return false;
            }
            catch (Exception ex)
            {
                Logger.Error($"[Connector] CheckConnectionAsync: Error waiting for token validation: {ex.Message}");
                return false;
            }

            if (!_zerodhaClient.CheckConnection())
                return false;

            this.IsConnected = true;
            return true;
        }

        /// <summary>
        /// Gets the symbol name with market type
        /// </summary>
        /// <param name="symbol">The symbol</param>
        /// <param name="marketType">The market type</param>
        /// <returns>The symbol name</returns>
        public static string GetSymbolName(string symbol, out MarketType marketType)
        {
            return InstrumentManager.GetSymbolName(symbol, out marketType);
        }

        /// <summary>
        /// Gets the suffix for a market type
        /// </summary>
        /// <param name="marketType">The market type</param>
        /// <returns>The suffix</returns>
        public static string GetSuffix(MarketType marketType)
        {
            return InstrumentManager.GetSuffix(marketType);
        }

        /// <summary>
        /// Registers instruments in NinjaTrader
        /// </summary>
        /// <returns>A task representing the asynchronous operation</returns>
        public async Task RegisterInstruments()
        {
            await _instrumentManager.RegisterSymbols();
        }

        /// <summary>
        /// Gets exchange information for all available instruments
        /// </summary>
        /// <returns>A collection of instrument definitions</returns>
        public async Task<ObservableCollection<InstrumentDefinition>> GetBrokerInformation()
        {
            return await _instrumentManager.GetBrokerInformation();
        }

        /// <summary>
        /// Creates an instrument in NinjaTrader
        /// </summary>
        /// <param name="instrument">The instrument definition to create</param>
        /// <param name="ntSymbolName">The NinjaTrader symbol name</param>
        /// <returns>True if the instrument was created successfully, false otherwise</returns>
        public bool CreateInstrument(InstrumentDefinition instrument, out string ntSymbolName)
        {
            return _instrumentManager.CreateInstrument(instrument, out ntSymbolName);
        }

        /// <summary>
        /// Removes an instrument from NinjaTrader
        /// </summary>
        /// <param name="instrument">The instrument definition to remove</param>
        /// <returns>True if the instrument was removed successfully, false otherwise</returns>
        public bool RemoveInstrument(InstrumentDefinition instrument)
        {
            return _instrumentManager.RemoveInstrument(instrument);
        }

        /// <summary>
        /// Gets all NinjaTrader symbols
        /// </summary>
        /// <returns>A collection of instrument definitions</returns>
        public async Task<ObservableCollection<InstrumentDefinition>> GetNTSymbols()
        {
            return await _instrumentManager.GetNTSymbols();
        }

        /// <summary>
        /// Gets historical trades for a symbol
        /// </summary>
        /// <param name="barsPeriodType">The bars period type</param>
        /// <param name="symbol">The symbol</param>
        /// <param name="fromDate">The start date</param>
        /// <param name="toDate">The end date</param>
        /// <param name="marketType">The market type</param>
        /// <param name="viewModelBase">The view model for progress updates</param>
        /// <returns>A list of historical records</returns>
        public async Task<List<Record>> GetHistoricalTrades(
            BarsPeriodType barsPeriodType,
            string symbol,
            DateTime fromDate,
            DateTime toDate,
            MarketType marketType,
            ViewModelBase viewModelBase)
        {
            return await _historicalDataService.GetHistoricalTrades(
                barsPeriodType,
                symbol,
                fromDate,
                toDate,
                marketType,
                viewModelBase);
        }

        /// <summary>
        /// Subscribes to real-time ticks for a symbol
        /// </summary>
        /// <param name="nativeSymbolName">The native symbol name</param>
        /// <param name="marketType">The market type</param>
        /// <param name="symbol">The symbol</param>
        /// <param name="l1Subscriptions">The L1 subscriptions dictionary</param>
        /// <param name="webSocketConnectionFunc">The WebSocket connection function</param>
        /// <returns>A task representing the asynchronous operation</returns>
        public async Task SubscribeToTicks(
            string nativeSymbolName,
            MarketType marketType,
            string symbol,
            ConcurrentDictionary<string, L1Subscription> l1Subscriptions,
            WebSocketConnectionFunc webSocketConnectionFunc)
        {
            // Use shared WebSocket for efficiency (single connection for all symbols)
            await _marketDataService.SubscribeToTicksShared(
                nativeSymbolName,
                marketType,
                symbol,
                l1Subscriptions,
                webSocketConnectionFunc);
        }

        /// <summary>
        /// Subscribes to market depth for a symbol
        /// </summary>
        /// <param name="nativeSymbolName">The native symbol name</param>
        /// <param name="marketType">The market type</param>
        /// <param name="symbol">The symbol</param>
        /// <param name="l2Subscriptions">The L2 subscriptions dictionary</param>
        /// <param name="webSocketConnectionFunc">The WebSocket connection function</param>
        /// <returns>A task representing the asynchronous operation</returns>
        public async Task SubscribeToDepth(
            string nativeSymbolName,
            MarketType marketType,
            string symbol,
            ConcurrentDictionary<string, L2Subscription> l2Subscriptions,
            WebSocketConnectionFunc webSocketConnectionFunc)
        {
            await _marketDataService.SubscribeToDepth(
                nativeSymbolName,
                marketType,
                symbol,
                l2Subscriptions,
                webSocketConnectionFunc);
        }

        // Removed ClearWrongStocks and FindCCControl as they were based on old logic
        // and may cause UI thread issues. Cleanup logic moved to specialized tools if needed.

        /// <summary>
        /// Clears stocks
        /// </summary>
        public void ClearStocks()
        {
            foreach (MasterInstrument masterInstrument in MasterInstrument.All.Where<MasterInstrument>((Func<MasterInstrument, bool>)(x =>
            {
                if (x.InstrumentType != InstrumentType.Stock)
                    return false;
                return x.Name.EndsWith("_NSE") || x.Name.EndsWith("_MCX") || x.Name.EndsWith("_NFO");
            })).ToArray<MasterInstrument>())
            {
                if (string.IsNullOrEmpty(masterInstrument.Description))
                    masterInstrument.DbRemove();
            }
        }

        /// <summary>
        /// Property changed event
        /// </summary>
        public event PropertyChangedEventHandler PropertyChanged;

        /// <summary>
        /// Raises the property changed event
        /// </summary>
        /// <param name="propertyName">The property name</param>
        protected virtual void OnPropertyChanged([CallerMemberName] string propertyName = null)
        {
            PropertyChangedEventHandler propertyChanged = this.PropertyChanged;
            if (propertyChanged == null)
                return;
            propertyChanged((object)this, new PropertyChangedEventArgs(propertyName));
        }

        /// <summary>
        /// Opens the SimulationEngineWindow and populates it with config settings.
        /// Called when simulation mode is enabled in config.json.
        /// </summary>
        private void OpenSimulationWindowFromConfig()
        {
            try
            {
                var settings = _configManager.SimulationSettings;
                if (settings == null || !settings.Enabled)
                    return;

                Logger.Info("[Connector] SIMULATION MODE: Opening SimulationEngineWindow from config...");

                // Set config FIRST, before opening window
                var simConfig = settings.ToSimulationConfig();
                Services.SimulationService.Instance.SetConfigFromSettings(simConfig);
                Logger.Info($"[Connector] SIMULATION MODE: Config set - Date={simConfig.SimulationDate:yyyy-MM-dd}, " +
                            $"Underlying={simConfig.Underlying}, ATM={simConfig.ATMStrike}");

                // Now open window on UI thread - it will read the pre-loaded config
                Application.Current?.Dispatcher?.BeginInvoke(new Action(async () =>
                {
                    try
                    {
                        // Create and show the SimulationEngineWindow
                        var window = new AddOns.SimulationEngine.SimulationEngineWindow();
                        window.Show();

                        Logger.Info("[Connector] SIMULATION MODE: SimulationEngineWindow opened");

                        // If AutoStart is enabled, automatically load data after a short delay
                        if (settings.AutoStart)
                        {
                            Logger.Info("[Connector] SIMULATION MODE: AutoStart enabled, loading data...");
                            await Task.Delay(500); // Give UI time to initialize

                            // Load Nifty Futures metrics for simulation date
                            Logger.Info("[Connector] SIMULATION MODE: Loading Nifty Futures metrics...");
                            await Services.Analysis.NiftyFuturesMetricsService.Instance.StartSimulationAsync(simConfig.SimulationDate);

                            // Load tick data for option symbols
                            Logger.Info("[Connector] SIMULATION MODE: Loading option tick data...");
                            bool loadSuccess = await Services.SimulationService.Instance.LoadHistoricalBars(simConfig);

                            if (loadSuccess)
                            {
                                // Publish simulated option chain to Option Chain and Option Signals windows
                                Logger.Info("[Connector] SIMULATION MODE: Publishing simulated option chain...");
                                Services.SimulationService.Instance.PublishSimulatedOptionChain();

                                // Auto-start playback
                                Logger.Info("[Connector] SIMULATION MODE: Auto-starting playback...");
                                Services.SimulationService.Instance.Start();
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        Logger.Error($"[Connector] SIMULATION MODE: Error opening window: {ex.Message}", ex);
                    }
                }), System.Windows.Threading.DispatcherPriority.Background);
            }
            catch (Exception ex)
            {
                Logger.Error($"[Connector] SIMULATION MODE: OpenSimulationWindowFromConfig error: {ex.Message}", ex);
            }
        }
    }
}
