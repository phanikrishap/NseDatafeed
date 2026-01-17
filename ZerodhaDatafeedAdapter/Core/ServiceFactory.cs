using ZerodhaDatafeedAdapter.Services.Historical;
using ZerodhaDatafeedAdapter.Services.Historical.Adapters;
using ZerodhaDatafeedAdapter.Services.Historical.Persistence;
using ZerodhaDatafeedAdapter.Services.Historical.Providers;
using ZerodhaDatafeedAdapter.Services.Trading;
using ZerodhaDatafeedAdapter.Services.Signals;
using ZerodhaDatafeedAdapter.Services.TBS;

namespace ZerodhaDatafeedAdapter.Core
{
    public static class ServiceFactory
    {
        #region Historical Services

        private static IHistoricalDataProvider _accelpixProvider;
        private static IHistoricalDataProvider _iciciProvider;
        private static ITickDataPersistence _tickPersistence;
        private static INT8BarsRequestAdapter _nt8Adapter;

        public static IHistoricalDataProvider GetAccelpixProvider()
        {
            if (_accelpixProvider == null)
            {
                var apiClient = new AccelpixApiClient(apiKey: null);
                _accelpixProvider = new AccelpixHistoricalDataProvider(apiClient);
            }
            return _accelpixProvider;
        }

        public static IHistoricalDataProvider GetIciciProvider()
        {
            return _iciciProvider ??= new IciciHistoricalDataProvider();
        }

        public static ITickDataPersistence GetTickPersistence()
        {
            return _tickPersistence ??= new SqliteTickDataPersistence();
        }

        public static INT8BarsRequestAdapter GetNT8Adapter()
        {
            return _nt8Adapter ??= new NT8BarsRequestAdapter();
        }

        #endregion

        #region Trading Services

        private static PnLTracker _pnlTracker;
        private static IOrderRouter _orderRouter;
        private static TradingContext _tradingContext;
        private static StoxxoBridgeManager _stoxxoBridgeManager;

        /// <summary>
        /// Gets the singleton PnLTracker instance.
        /// Tracks profit & loss across all active trading positions.
        /// </summary>
        public static PnLTracker GetPnLTracker()
        {
            return _pnlTracker ??= new PnLTracker();
        }

        /// <summary>
        /// Gets the singleton OrderRouter instance.
        /// Routes orders to the Stoxxo execution platform.
        /// </summary>
        public static IOrderRouter GetOrderRouter()
        {
            return _orderRouter ??= new StoxxoOrderRouter();
        }

        /// <summary>
        /// Gets the singleton TradingContext instance.
        /// Manages trading session state (underlying, expiry, strikes).
        /// </summary>
        public static TradingContext GetTradingContext()
        {
            return _tradingContext ??= new TradingContext();
        }

        /// <summary>
        /// Gets the singleton StoxxoBridgeManager instance.
        /// Manages low-level Stoxxo API integration.
        /// </summary>
        public static StoxxoBridgeManager GetStoxxoBridgeManager()
        {
            return _stoxxoBridgeManager ??= new StoxxoBridgeManager();
        }

        #endregion

        #region Signal Services

        private static INotificationService _notificationService;
        private static IExecutionBridge _executionBridge;
        private static SignalContext _signalContext;

        /// <summary>
        /// Gets the singleton NotificationService instance.
        /// Sends notifications to Terminal and Telegram.
        /// </summary>
        public static INotificationService GetNotificationService()
        {
            return _notificationService ??= new CompositeNotificationService();
        }

        /// <summary>
        /// Gets the singleton ExecutionBridge instance.
        /// Executes signal orders via SignalBridgeService.
        /// </summary>
        public static IExecutionBridge GetExecutionBridge()
        {
            return _executionBridge ??= new SignalExecutionBridge();
        }

        /// <summary>
        /// Gets the singleton SignalContext instance.
        /// Manages signal generation configuration.
        /// </summary>
        public static SignalContext GetSignalContext()
        {
            return _signalContext ??= new SignalContext();
        }

        #endregion

        #region Lifecycle Management

        /// <summary>
        /// Resets all service instances.
        /// Use this for testing or session restart scenarios.
        /// WARNING: Only call this when all services are idle.
        /// </summary>
        public static void Reset()
        {
            // Historical services
            _accelpixProvider = null;
            _iciciProvider = null;
            _tickPersistence = null;
            _nt8Adapter = null;

            // Trading services
            _pnlTracker = null;
            _orderRouter = null;
            _tradingContext = null;
            _stoxxoBridgeManager = null;

            // Signal services
            _notificationService = null;
            _executionBridge = null;
            _signalContext = null;
        }

        /// <summary>
        /// Gets diagnostic information about factory state.
        /// </summary>
        public static string GetDiagnostics()
        {
            return $"ServiceFactory Status:\n" +
                   $"  Historical Services:\n" +
                   $"    AccelpixProvider: {(_accelpixProvider != null ? "Initialized" : "Not Created")}\n" +
                   $"    IciciProvider: {(_iciciProvider != null ? "Initialized" : "Not Created")}\n" +
                   $"    TickPersistence: {(_tickPersistence != null ? "Initialized" : "Not Created")}\n" +
                   $"    NT8Adapter: {(_nt8Adapter != null ? "Initialized" : "Not Created")}\n" +
                   $"  Trading Services:\n" +
                   $"    PnLTracker: {(_pnlTracker != null ? "Initialized" : "Not Created")}\n" +
                   $"    OrderRouter: {(_orderRouter != null ? "Initialized" : "Not Created")}\n" +
                   $"    TradingContext: {(_tradingContext != null ? "Initialized" : "Not Created")}\n" +
                   $"    StoxxoBridgeManager: {(_stoxxoBridgeManager != null ? "Initialized" : "Not Created")}\n" +
                   $"  Signal Services:\n" +
                   $"    NotificationService: {(_notificationService != null ? "Initialized" : "Not Created")}\n" +
                   $"    ExecutionBridge: {(_executionBridge != null ? "Initialized" : "Not Created")}\n" +
                   $"    SignalContext: {(_signalContext != null ? "Initialized" : "Not Created")}";
        }

        #endregion
    }
}
