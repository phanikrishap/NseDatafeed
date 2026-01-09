using System;
using System.Collections.Generic;
using System.IO;
using System.Reactive.Linq;
using Newtonsoft.Json.Linq;
using ZerodhaDatafeedAdapter.Logging;

namespace ZerodhaDatafeedAdapter.Services.Historical
{
    /// <summary>
    /// Preferred historical tick data source.
    /// </summary>
    public enum HistoricalTickDataSource
    {
        /// <summary>Historical tick data disabled</summary>
        None,

        /// <summary>Use ICICI Direct Breeze API (rate-limited, 1 prior day + current)</summary>
        IciciDirect,

        /// <summary>Use Accelpix API (high-throughput, 3 prior days + current)</summary>
        Accelpix
    }

    /// <summary>
    /// Coordinator for historical tick data sources.
    /// Routes requests to the configured source (Accelpix or ICICI).
    ///
    /// Config structure in config.json:
    /// {
    ///   "HistoricalTickData": {
    ///     "Enabled": true,
    ///     "PreferredSource": "Accelpix"  // "Accelpix" | "IciciDirect" | "None"
    ///   },
    ///   "Accelpix": {
    ///     "ApiKey": "...",
    ///     "DaysToFetch": 3
    ///   },
    ///   "IciciDirect": {
    ///     "ApiKey": "...",
    ///     "AutoLogin": true
    ///   }
    /// }
    /// </summary>
    public class HistoricalTickDataCoordinator : IHistoricalTickDataSource
    {
        private static readonly Lazy<HistoricalTickDataCoordinator> _instance =
            new Lazy<HistoricalTickDataCoordinator>(() => new HistoricalTickDataCoordinator());

        public static HistoricalTickDataCoordinator Instance => _instance.Value;

        #region State

        private bool _isEnabled;
        private HistoricalTickDataSource _preferredSource;
        private IHistoricalTickDataSource _activeSource;
        private bool _isInitialized;
        private readonly object _initLock = new object();

        #endregion

        #region Properties

        /// <summary>
        /// Whether historical tick data fetching is enabled.
        /// </summary>
        public bool IsEnabled => _isEnabled;

        /// <summary>
        /// The configured preferred source.
        /// </summary>
        public HistoricalTickDataSource PreferredSource => _preferredSource;

        /// <summary>
        /// The currently active source (may be null if disabled or not yet initialized).
        /// </summary>
        public IHistoricalTickDataSource ActiveSource => _activeSource;

        /// <summary>
        /// True when coordinator is initialized and has an active source ready.
        /// </summary>
        public bool IsReady => _isInitialized && _isEnabled && _activeSource != null && _activeSource.IsReady;

        #endregion

        #region Constructor

        private HistoricalTickDataCoordinator()
        {
            Logger.Info("[HistoricalTickDataCoordinator] Singleton instance created");
        }

        #endregion

        #region IHistoricalTickDataSource Implementation

        /// <summary>
        /// Initialize the coordinator - reads config and initializes the appropriate source.
        /// </summary>
        public void Initialize()
        {
            if (_isInitialized)
            {
                Logger.Info("[HistoricalTickDataCoordinator] Already initialized, skipping");
                return;
            }

            lock (_initLock)
            {
                if (_isInitialized) return;

                Logger.Info("[HistoricalTickDataCoordinator] Initializing...");

                // Load configuration
                if (!LoadConfiguration())
                {
                    Logger.Error("[HistoricalTickDataCoordinator] Failed to load configuration");
                    _isInitialized = true; // Mark as initialized even on failure to avoid retry loops
                    return;
                }

                if (!_isEnabled)
                {
                    Logger.Info("[HistoricalTickDataCoordinator] Historical tick data is DISABLED in config");
                    _isInitialized = true;
                    return;
                }

                // Initialize the appropriate source
                switch (_preferredSource)
                {
                    case HistoricalTickDataSource.Accelpix:
                        Logger.Info("[HistoricalTickDataCoordinator] Initializing Accelpix as tick data source");
                        AccelpixHistoricalTickDataService.Instance.Initialize();
                        _activeSource = AccelpixHistoricalTickDataService.Instance;
                        break;

                    case HistoricalTickDataSource.IciciDirect:
                        Logger.Info("[HistoricalTickDataCoordinator] Initializing ICICI Direct as tick data source");
                        HistoricalTickDataService.Instance.Initialize();
                        _activeSource = HistoricalTickDataService.Instance;
                        break;

                    case HistoricalTickDataSource.None:
                    default:
                        Logger.Info("[HistoricalTickDataCoordinator] No tick data source configured");
                        _activeSource = null;
                        break;
                }

                _isInitialized = true;
                Logger.Info($"[HistoricalTickDataCoordinator] Initialized - Enabled={_isEnabled}, Source={_preferredSource}, ActiveSourceReady={_activeSource?.IsReady ?? false}");
            }
        }

        /// <summary>
        /// Queue a tick data request - routes to active source.
        /// </summary>
        public IObservable<InstrumentTickDataStatus> QueueInstrumentTickRequest(string zerodhaSymbol, DateTime tradeDate)
        {
            if (!_isEnabled || _activeSource == null)
            {
                Logger.Debug($"[HistoricalTickDataCoordinator] Tick data disabled or no source - skipping {zerodhaSymbol}");
                return Observable.Return(new InstrumentTickDataStatus
                {
                    ZerodhaSymbol = zerodhaSymbol,
                    State = TickDataState.Failed,
                    ErrorMessage = "Historical tick data is disabled or no source configured"
                });
            }

            return _activeSource.QueueInstrumentTickRequest(zerodhaSymbol, tradeDate);
        }

        /// <summary>
        /// Get status stream for instrument - routes to active source.
        /// </summary>
        public IObservable<InstrumentTickDataStatus> GetInstrumentTickStatusStream(string zerodhaSymbol)
        {
            if (!_isEnabled || _activeSource == null)
            {
                return Observable.Return(new InstrumentTickDataStatus
                {
                    ZerodhaSymbol = zerodhaSymbol,
                    State = TickDataState.Failed,
                    ErrorMessage = "Historical tick data is disabled"
                });
            }

            return _activeSource.GetInstrumentTickStatusStream(zerodhaSymbol);
        }

        /// <summary>
        /// Queue batch download request using context - routes to appropriate source.
        /// Uses context (underlying, expiry, strikes, isMonthlyExpiry) to build symbols correctly,
        /// handling SENSEX BSE suffix and monthly/weekly formats automatically.
        /// </summary>
        /// <param name="underlying">Underlying index (NIFTY, BANKNIFTY, FINNIFTY, MIDCPNIFTY, SENSEX)</param>
        /// <param name="expiry">Expiry date</param>
        /// <param name="projectedAtmStrike">Projected ATM strike for center-out propagation</param>
        /// <param name="strikes">List of all strikes to download</param>
        /// <param name="isMonthlyExpiry">Whether this is a monthly expiry</param>
        /// <param name="zerodhaSymbolMap">Optional mapping of (strike,optionType) to Zerodha trading symbol</param>
        /// <param name="historicalDate">Date to fetch history for (defaults to prior working day)</param>
        public void QueueDownloadRequest(
            string underlying,
            DateTime expiry,
            int projectedAtmStrike,
            List<int> strikes,
            bool isMonthlyExpiry,
            Dictionary<(int strike, string optionType), string> zerodhaSymbolMap = null,
            DateTime? historicalDate = null)
        {
            if (!_isEnabled)
            {
                Logger.Debug($"[HistoricalTickDataCoordinator] Tick data disabled - skipping batch request for {underlying}");
                return;
            }

            Logger.Info($"[HistoricalTickDataCoordinator] Routing batch request: {underlying} {expiry:dd-MMM-yy} ATM={projectedAtmStrike} Strikes={strikes?.Count ?? 0} Monthly={isMonthlyExpiry} Source={_preferredSource}");

            // Route to appropriate service based on configured source
            switch (_preferredSource)
            {
                case HistoricalTickDataSource.Accelpix:
                    AccelpixHistoricalTickDataService.Instance.QueueDownloadRequest(
                        underlying, expiry, projectedAtmStrike, strikes, isMonthlyExpiry, zerodhaSymbolMap, historicalDate);
                    break;

                case HistoricalTickDataSource.IciciDirect:
                    // ICICI's QueueDownloadRequest doesn't need isMonthlyExpiry - it parses symbols
                    HistoricalTickDataService.Instance.QueueDownloadRequest(
                        underlying, expiry, projectedAtmStrike, strikes, zerodhaSymbolMap, historicalDate);
                    break;

                default:
                    Logger.Debug($"[HistoricalTickDataCoordinator] No tick data source configured");
                    break;
            }
        }

        #endregion

        #region Private Methods

        private bool LoadConfiguration()
        {
            try
            {
                string documentsPath = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
                string configPath = Path.Combine(documentsPath, Classes.Constants.BaseDataFolder, Classes.Constants.ConfigFileName);

                if (!File.Exists(configPath))
                {
                    Logger.Error($"[HistoricalTickDataCoordinator] Config file not found: {configPath}");
                    return false;
                }

                string json = File.ReadAllText(configPath);
                var config = JObject.Parse(json);

                // Read HistoricalTickData section
                var historicalConfig = config["HistoricalTickData"] as JObject;
                if (historicalConfig != null)
                {
                    _isEnabled = historicalConfig["Enabled"]?.Value<bool>() ?? false;

                    string sourceStr = historicalConfig["PreferredSource"]?.ToString() ?? "None";
                    _preferredSource = ParseSource(sourceStr);

                    Logger.Info($"[HistoricalTickDataCoordinator] Config loaded - Enabled={_isEnabled}, PreferredSource={sourceStr} -> {_preferredSource}");
                }
                else
                {
                    // Backward compatibility: check if IciciDirect has AutoLogin enabled
                    var iciciConfig = config["IciciDirect"] as JObject;
                    if (iciciConfig != null)
                    {
                        bool iciciAutoLogin = iciciConfig["AutoLogin"]?.Value<bool>() ?? false;
                        if (iciciAutoLogin)
                        {
                            _isEnabled = true;
                            _preferredSource = HistoricalTickDataSource.IciciDirect;
                            Logger.Info("[HistoricalTickDataCoordinator] Using legacy config - IciciDirect with AutoLogin");
                        }
                    }

                    // Also check if Accelpix has UseAsTickSource enabled
                    var accelpixConfig = config["Accelpix"] as JObject;
                    if (accelpixConfig != null)
                    {
                        bool accelpixEnabled = accelpixConfig["UseAsTickSource"]?.Value<bool>() ?? false;
                        string apiKey = accelpixConfig["ApiKey"]?.ToString();

                        if (accelpixEnabled && !string.IsNullOrEmpty(apiKey))
                        {
                            _isEnabled = true;
                            _preferredSource = HistoricalTickDataSource.Accelpix;
                            Logger.Info("[HistoricalTickDataCoordinator] Using Accelpix config with UseAsTickSource=true");
                        }
                    }
                }

                return true;
            }
            catch (Exception ex)
            {
                Logger.Error($"[HistoricalTickDataCoordinator] Error loading configuration: {ex.Message}", ex);
                return false;
            }
        }

        private HistoricalTickDataSource ParseSource(string sourceStr)
        {
            if (string.IsNullOrEmpty(sourceStr))
                return HistoricalTickDataSource.None;

            switch (sourceStr.ToLowerInvariant())
            {
                case "accelpix":
                    return HistoricalTickDataSource.Accelpix;
                case "icicidirect":
                case "icici":
                    return HistoricalTickDataSource.IciciDirect;
                case "none":
                default:
                    return HistoricalTickDataSource.None;
            }
        }

        #endregion
    }
}
