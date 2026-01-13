using System;
using System.Collections.Generic;
using System.Linq;
using System.Reactive.Linq;
using System.Reactive.Threading.Tasks;
using System.Threading;
using System.Threading.Tasks;
using ZerodhaDatafeedAdapter.Logging;
using ZerodhaDatafeedAdapter.Models;
using ZerodhaDatafeedAdapter.Services.Historical;

namespace ZerodhaDatafeedAdapter.AddOns.OptionSignals.Services
{
    /// <summary>
    /// Result of a historical data synchronization operation.
    /// </summary>
    public class SyncResult
    {
        public int TotalSymbols { get; set; }
        public int SuccessCount { get; set; }
        public int FailedCount => TotalSymbols - SuccessCount;
        public bool IsSuccess => SuccessCount > TotalSymbols / 2; // >50% success
        public TimeSpan Duration { get; set; }
    }

    /// <summary>
    /// Handles historical tick data synchronization with NinjaTrader.
    /// Extracted from OptionSignalsViewModel for single responsibility.
    /// </summary>
    public class OptionDataSynchronizer
    {
        private static readonly ILoggerService _log = LoggerFactory.OpSignals;

        /// <summary>
        /// Synchronizes historical tick data for all options.
        /// Returns when sync is complete or timeout reached.
        /// </summary>
        /// <param name="options">List of option instruments to sync</param>
        /// <param name="ct">Cancellation token</param>
        /// <param name="timeoutSeconds">Timeout per symbol (default 30s)</param>
        /// <returns>Sync result with success/failure counts</returns>
        public async Task<SyncResult> SynchronizeAsync(
            List<MappedInstrument> options,
            CancellationToken ct,
            int timeoutSeconds = 30)
        {
            if (options == null || options.Count == 0)
            {
                return new SyncResult { TotalSymbols = 0, SuccessCount = 0 };
            }

            var startTime = DateTime.UtcNow;
            var coordinator = HistoricalTickDataCoordinator.Instance;
            var symbolsToSync = options
                .Where(o => !string.IsNullOrEmpty(o.symbol))
                .Select(o => o.symbol)
                .Distinct()
                .ToList();

            _log.Info($"[DataSync] Starting sync for {symbolsToSync.Count} symbols (timeout={timeoutSeconds}s)");

            var syncTasks = symbolsToSync.Select(async sym =>
            {
                try
                {
                    await coordinator.GetInstrumentTickStatusStream(sym)
                        .Where(s => s.State == TickDataState.Ready ||
                                   s.State == TickDataState.Failed ||
                                   s.State == TickDataState.NoData)
                        .Take(1)
                        .Timeout(TimeSpan.FromSeconds(timeoutSeconds))
                        .ToTask(ct);
                    return (Symbol: sym, Success: true);
                }
                catch (OperationCanceledException)
                {
                    // Propagate cancellation
                    throw;
                }
                catch (TimeoutException)
                {
                    // Timeout is acceptable - continue with partial data
                    return (Symbol: sym, Success: false);
                }
                catch (Exception ex)
                {
                    _log.Warn($"[DataSync] Error syncing {sym}: {ex.Message}");
                    return (Symbol: sym, Success: false);
                }
            }).ToList();

            try
            {
                var results = await Task.WhenAll(syncTasks);
                var duration = DateTime.UtcNow - startTime;

                int successCount = results.Count(r => r.Success);
                int failedCount = results.Count(r => !r.Success);

                _log.Info($"[DataSync] Complete in {duration.TotalSeconds:F1}s: {successCount}/{symbolsToSync.Count} symbols synced");

                if (failedCount > 0 && failedCount <= 10)
                {
                    var failedSymbols = results.Where(r => !r.Success).Select(r => r.Symbol);
                    _log.Warn($"[DataSync] Failed symbols: {string.Join(", ", failedSymbols)}");
                }

                return new SyncResult
                {
                    TotalSymbols = symbolsToSync.Count,
                    SuccessCount = successCount,
                    Duration = duration
                };
            }
            catch (OperationCanceledException)
            {
                _log.Info("[DataSync] Sync cancelled");
                throw;
            }
        }

        /// <summary>
        /// Synchronizes historical tick data for a single symbol.
        /// </summary>
        public async Task<bool> SynchronizeSymbolAsync(string symbol, CancellationToken ct, int timeoutSeconds = 30)
        {
            if (string.IsNullOrEmpty(symbol)) return false;

            var coordinator = HistoricalTickDataCoordinator.Instance;

            try
            {
                await coordinator.GetInstrumentTickStatusStream(symbol)
                    .Where(s => s.State == TickDataState.Ready ||
                               s.State == TickDataState.Failed ||
                               s.State == TickDataState.NoData)
                    .Take(1)
                    .Timeout(TimeSpan.FromSeconds(timeoutSeconds))
                    .ToTask(ct);
                return true;
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (TimeoutException)
            {
                return false;
            }
            catch (Exception ex)
            {
                _log.Warn($"[DataSync] Error syncing {symbol}: {ex.Message}");
                return false;
            }
        }
    }
}
