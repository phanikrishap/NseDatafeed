using ZerodhaDatafeedAdapter.Services.Historical;
using ZerodhaDatafeedAdapter.Services.Historical.Adapters;
using ZerodhaDatafeedAdapter.Services.Historical.Persistence;
using ZerodhaDatafeedAdapter.Services.Historical.Providers;

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

        #region Trading Services (Placeholder for Phase 2)

        // TODO: Add PnLTracker, OrderRouter, TradingContext in Phase 2

        #endregion

        #region Signal Services (Placeholder for Phase 3)

        // TODO: Add NotificationService, ExecutionBridge, SignalContext in Phase 3

        #endregion

        #region Market Data Services (Placeholder for Phase 4)

        // TODO: Add TickProcessor in Phase 4

        #endregion
    }
}
