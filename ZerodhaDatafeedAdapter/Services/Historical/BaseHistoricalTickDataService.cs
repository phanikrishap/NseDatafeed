using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Reactive.Linq;
using System.Reactive.Subjects;
using ZerodhaDatafeedAdapter.Logging;

namespace ZerodhaDatafeedAdapter.Services.Historical
{
    /// <summary>
    /// Base class for historical tick data sources providing common request queue and status management.
    /// </summary>
    public abstract class BaseHistoricalTickDataService : IHistoricalTickDataSource, IDisposable
    {
        // Per-instrument tick data request queue (buffered until service is ready)
        protected readonly ReplaySubject<InstrumentTickDataRequest> _instrumentRequestQueue;
        protected IDisposable _instrumentQueueSubscription;

        // Tracks status for each instrument request
        protected readonly ConcurrentDictionary<string, BehaviorSubject<InstrumentTickDataStatus>> _instrumentStatusSubjects
            = new ConcurrentDictionary<string, BehaviorSubject<InstrumentTickDataStatus>>();

        protected BaseHistoricalTickDataService(int bufferSize = 200)
        {
            _instrumentRequestQueue = new ReplaySubject<InstrumentTickDataRequest>(bufferSize: bufferSize);
        }

        public abstract bool IsInitialized { get; }
        public abstract bool IsReady { get; }
        public abstract void Initialize();

        /// <summary>
        /// Get observable for a specific instrument's tick data status.
        /// </summary>
        public IObservable<InstrumentTickDataStatus> GetInstrumentTickStatusStream(string zerodhaSymbol)
        {
            var subject = _instrumentStatusSubjects.GetOrAdd(zerodhaSymbol,
                _ => new BehaviorSubject<InstrumentTickDataStatus>(
                    new InstrumentTickDataStatus { ZerodhaSymbol = zerodhaSymbol, State = TickDataState.Pending }));
            return subject.AsObservable();
        }

        /// <summary>
        /// Queue a single instrument tick data request.
        /// </summary>
        public virtual IObservable<InstrumentTickDataStatus> QueueInstrumentTickRequest(string zerodhaSymbol, DateTime tradeDate)
        {
            if (string.IsNullOrEmpty(zerodhaSymbol))
            {
                return Observable.Return(new InstrumentTickDataStatus
                {
                    ZerodhaSymbol = zerodhaSymbol,
                    State = TickDataState.Failed,
                    ErrorMessage = "Symbol is null or empty"
                });
            }

            var statusSubject = _instrumentStatusSubjects.GetOrAdd(zerodhaSymbol,
                _ => new BehaviorSubject<InstrumentTickDataStatus>(
                    new InstrumentTickDataStatus { ZerodhaSymbol = zerodhaSymbol, State = TickDataState.Pending }));

            var currentStatus = statusSubject.Value;
            if (currentStatus.State == TickDataState.Ready || currentStatus.State == TickDataState.Downloading)
            {
                return statusSubject.AsObservable();
            }

            var request = new InstrumentTickDataRequest
            {
                ZerodhaSymbol = zerodhaSymbol,
                TradeDate = tradeDate,
                QueuedAt = DateTime.Now
            };

            statusSubject.OnNext(new InstrumentTickDataStatus
            {
                ZerodhaSymbol = zerodhaSymbol,
                State = TickDataState.Queued,
                TradeDate = tradeDate
            });

            _instrumentRequestQueue.OnNext(request);
            return statusSubject.AsObservable();
        }

        /// <summary>
        /// Updates the status for a given symbol.
        /// </summary>
        protected void UpdateInstrumentStatus(string zerodhaSymbol, InstrumentTickDataStatus status)
        {
            if (_instrumentStatusSubjects.TryGetValue(zerodhaSymbol, out var subject))
            {
                subject.OnNext(status);
            }
        }

        protected abstract void SubscribeToInstrumentQueue();

        public virtual void Dispose()
        {
            _instrumentQueueSubscription?.Dispose();
            _instrumentRequestQueue?.Dispose();
            foreach (var subject in _instrumentStatusSubjects.Values)
            {
                subject.Dispose();
            }
            _instrumentStatusSubjects.Clear();
        }
    }
}
