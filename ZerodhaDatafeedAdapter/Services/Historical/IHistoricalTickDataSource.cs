using System;

namespace ZerodhaDatafeedAdapter.Services.Historical
{
    /// <summary>
    /// Common interface for historical tick data sources.
    /// Implemented by both HistoricalTickDataService and AccelpixHistoricalTickDataService.
    /// </summary>
    public interface IHistoricalTickDataSource
    {
        /// <summary>
        /// True when the service is fully initialized and ready to process requests.
        /// </summary>
        bool IsReady { get; }

        /// <summary>
        /// Initialize the service. Call this during adapter startup.
        /// </summary>
        void Initialize();

        /// <summary>
        /// Queue a single instrument tick data request.
        /// This is the API for on-demand tick data requests when cache misses.
        /// </summary>
        /// <param name="zerodhaSymbol">Zerodha trading symbol (e.g., NIFTY2611326000CE)</param>
        /// <param name="tradeDate">Date to fetch tick data for</param>
        /// <returns>Observable that signals when tick data is available</returns>
        IObservable<InstrumentTickDataStatus> QueueInstrumentTickRequest(string zerodhaSymbol, DateTime tradeDate);

        /// <summary>
        /// Get observable for a specific instrument's tick data status.
        /// Subscribe to this to get notified when tick data becomes available.
        /// </summary>
        /// <param name="zerodhaSymbol">Zerodha trading symbol</param>
        /// <returns>Observable stream of status updates</returns>
        IObservable<InstrumentTickDataStatus> GetInstrumentTickStatusStream(string zerodhaSymbol);
    }
}
