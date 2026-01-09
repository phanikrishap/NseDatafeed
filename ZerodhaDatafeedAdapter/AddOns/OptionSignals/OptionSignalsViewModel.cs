using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Reactive.Disposables;
using System.Reactive.Linq;
using System.Reactive.Subjects;
using System.Reactive.Threading.Tasks;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Data;
using System.Windows.Threading;
using NinjaTrader.Cbi;
using NinjaTrader.Data;
using ZerodhaDatafeedAdapter.AddOns.OptionSignals.Models;
using ZerodhaDatafeedAdapter.Helpers;
using ZerodhaDatafeedAdapter.Logging;
using ZerodhaDatafeedAdapter.Models;
using ZerodhaDatafeedAdapter.Models.Reactive;
using ZerodhaDatafeedAdapter.Services.Analysis;
using ZerodhaDatafeedAdapter.Services.Historical;
using ZerodhaDatafeedAdapter.ViewModels;

namespace ZerodhaDatafeedAdapter.AddOns.OptionSignals
{
    public class OptionSignalsViewModel : ViewModelBase, IDisposable
    {
        private CompositeDisposable _subscriptions;
        private readonly Dictionary<string, BarsRequest> _barsRequests = new Dictionary<string, BarsRequest>();
        private readonly Dictionary<string, (OptionSignalsRow row, string type)> _symbolToRowMap = new Dictionary<string, (OptionSignalsRow, string)>();
        private readonly Subject<OptionsGeneratedEvent> _strikeGenerationTrigger = new Subject<OptionsGeneratedEvent>();
        private readonly Dispatcher _dispatcher;
        private readonly object _rowsLock = new object();

        // Data Collections
        public ObservableCollection<OptionSignalsRow> Rows { get; } = new ObservableCollection<OptionSignalsRow>();

        // Properties
        private string _underlying = "NIFTY";
        private string _expiry = "---";
        private string _statusText = "Waiting for Data...";
        private bool _isBusy;
        private DateTime? _selectedExpiry;

        public string Underlying
        {
            get => _underlying;
            set { if (_underlying != value) { _underlying = value; OnPropertyChanged(); } }
        }

        public string Expiry
        {
            get => _expiry;
            set { if (_expiry != value) { _expiry = value; OnPropertyChanged(); } }
        }

        public string StatusText
        {
            get => _statusText;
            set { if (_statusText != value) { _statusText = value; OnPropertyChanged(); } }
        }

        public bool IsBusy
        {
            get => _isBusy;
            set { if (_isBusy != value) { _isBusy = value; OnPropertyChanged(); } }
        }

        public OptionSignalsViewModel()
        {
            _dispatcher = Dispatcher.CurrentDispatcher;
            BindingOperations.EnableCollectionSynchronization(Rows, _rowsLock);
            Logger.Info("[OptionSignalsViewModel] Initialized");
            SetupInternalPipelines();
        }

        private void SetupInternalPipelines()
        {
            // Use Switch() to ensure that if a new OptionsGeneratedEvent arrives,
            // we immediately stop/cancel any previous synchronization and strike population.
            _subscriptions = new CompositeDisposable();
            
            var strikePipeline = _strikeGenerationTrigger
                .Select(evt => Observable.FromAsync(ct => SyncAndPopulateStrikes(evt, ct)))
                .Switch() // The magic sauce: cancels previous task if new event arrives
                .Subscribe(
                    _ => Logger.Debug("[OptionSignalsViewModel] Strike generation pipeline iteration completed"),
                    ex => Logger.Error($"[OptionSignalsViewModel] Strike pipeline error: {ex.Message}")
                );

            _subscriptions.Add(strikePipeline);
        }

        public void StartServices()
        {
            Logger.Info("[OptionSignalsViewModel] Starting services");
            var hub = MarketDataReactiveHub.Instance;

            // 1. Monitor Initial Strikes (Projected Open or Spot)
            _subscriptions.Add(hub.ProjectedOpenStream
                .Where(s => s.IsComplete)
                .Take(1)
                .ObserveOnDispatcher()
                .Subscribe(OnProjectedOpenReady));

            // 2. Monitor Options Generated - this drives the row population
            _subscriptions.Add(hub.OptionsGeneratedStream
                .Subscribe(evt => _strikeGenerationTrigger.OnNext(evt)));

            // 3. Monitor Real-time LTP for current rows
            _subscriptions.Add(hub.OptionPriceBatchStream
                .ObserveOnDispatcher()
                .Subscribe(OnOptionPriceBatch));
        }

        public void StopServices()
        {
            Logger.Info("[OptionSignalsViewModel] Stopping services");
            _subscriptions?.Clear(); // Clear but keep instance to avoid null refs in async callbacks
            ClearBarsRequests();
        }

        private void OnProjectedOpenReady(ProjectedOpenState state)
        {
            Logger.Info($"[OptionSignalsViewModel] Projected Open Ready: {state.NiftyProjectedOpen:F2}");
        }

        private async Task SyncAndPopulateStrikes(OptionsGeneratedEvent evt, System.Threading.CancellationToken ct)
        {
            if (evt?.Options == null || evt.Options.Count == 0) return;
            Logger.Info($"[OptionSignalsViewModel] SyncAndPopulateStrikes started for {evt.SelectedUnderlying} ATM={evt.ATMStrike}");

            // Update UI Properties (via UI dispatcher)
            await _dispatcher.InvokeAsync(() => {
                Underlying = evt.SelectedUnderlying;
                _selectedExpiry = evt.SelectedExpiry;
                Expiry = evt.SelectedExpiry.ToString("dd-MMM-yyyy");
                IsBusy = true;
                StatusText = "Synchronizing Historical Tick Data...";
            });

            // Calculate Strikes (ATM +/- 10)
            int atmStrike = (int)evt.ATMStrike;
            int step = 50;
            var uniqueStrikes = evt.Options.Where(o => o.strike.HasValue)
                .Select(o => (int)o.strike.Value)
                .Distinct()
                .OrderBy(s => s)
                .ToList();

            if (uniqueStrikes.Count >= 2) step = uniqueStrikes[1] - uniqueStrikes[0];

            var strikesToLoad = new List<int>();
            for (int i = -10; i <= 10; i++) strikesToLoad.Add(atmStrike + (i * step));

            var symbolsToSync = new List<string>();
            var strikeToSymbolMap = new Dictionary<(int strike, string type), string>();

            foreach (var strike in strikesToLoad)
            {
                var ce = evt.Options.FirstOrDefault(o => o.strike == strike && o.option_type == "CE");
                var pe = evt.Options.FirstOrDefault(o => o.strike == strike && o.option_type == "PE");
                if (ce != null) { symbolsToSync.Add(ce.symbol); strikeToSymbolMap[(strike, "CE")] = ce.symbol; }
                if (pe != null) { symbolsToSync.Add(pe.symbol); strikeToSymbolMap[(strike, "PE")] = pe.symbol; }
            }

            if (symbolsToSync.Count == 0)
            {
                await _dispatcher.InvokeAsync(() => {
                    StatusText = "No symbols found for requested strikes.";
                    IsBusy = false;
                });
                return;
            }

            // Wait for all symbols in batch to be Ready in NT DB (with timeout and cancellation)
            var coordinator = HistoricalTickDataCoordinator.Instance;
            var syncTaskList = symbolsToSync.Select(async sym => 
            {
                try {
                    await coordinator.GetInstrumentTickStatusStream(sym)
                        .Where(s => s.State == TickDataState.Ready || s.State == TickDataState.Failed || s.State == TickDataState.NoData)
                        .Take(1)
                        .Timeout(TimeSpan.FromSeconds(60)) // Give it a minute per symbol
                        .ToTask(ct);
                } catch (OperationCanceledException) {
                    throw;
                } catch (Exception ex) {
                    Logger.Warn($"[OptionSignalsViewModel] Sync timeout/error for {sym}: {ex.Message}");
                }
            }).ToList();

            try
            {
                await Task.WhenAll(syncTaskList);
                Logger.Info($"[OptionSignalsViewModel] Historical sync finished for {evt.SelectedUnderlying}");
            }
            catch (OperationCanceledException)
            {
                Logger.Debug($"[OptionSignalsViewModel] SyncAndPopulateStrikes cancelled");
                return;
            }
            catch (Exception ex)
            {
                Logger.Error($"[OptionSignalsViewModel] Sync wait failed: {ex.Message}");
            }

            // Populate Rows on UI thread
            await _dispatcher.InvokeAsync(() =>
            {
                lock (_rowsLock)
                {
                    Rows.Clear();
                    _symbolToRowMap.Clear();
                    ClearBarsRequests();

                    foreach (var strike in strikesToLoad)
                    {
                        var row = new OptionSignalsRow { Strike = strike, IsATM = (strike == (int)evt.ATMStrike) };
                        
                        if (strikeToSymbolMap.TryGetValue((strike, "CE"), out var ceSym))
                        {
                            row.CESymbol = ceSym;
                            _symbolToRowMap[ceSym] = (row, "CE");
                            CreateBarsRequest(ceSym, (int)strike, "CE", row);
                        }

                        if (strikeToSymbolMap.TryGetValue((strike, "PE"), out var peSym))
                        {
                            row.PESymbol = peSym;
                            _symbolToRowMap[peSym] = (row, "PE");
                            CreateBarsRequest(peSym, (int)strike, "PE", row);
                        }

                        Rows.Add(row);
                    }

                    StatusText = $"Monitoring {Rows.Count} strikes. (Historical Sync OK)";
                    IsBusy = false;
                }
            });
        }

        private void CreateBarsRequest(string symbol, int strike, string type, OptionSignalsRow row)
        {
            var instrument = Instrument.GetInstrument(symbol);
            if (instrument == null)
            {
                Logger.Warn($"[OptionSignalsViewModel] Instrument not found for {symbol}");
                return;
            }

            // Use NT's specialized dispatcher for NT core objects
            NinjaTrader.Core.Globals.RandomDispatcher.InvokeAsync(() =>
            {
                var request = new BarsRequest(instrument, 10); // Small buffer for ATR signals
                request.BarsPeriod = new BarsPeriod
                {
                    BarsPeriodType = (BarsPeriodType)7015, // RangeATR
                    Value = 1, // Traditional value
                    Value2 = 3, // Min Seconds
                    BaseBarsPeriodValue = 1 // Min Ticks
                };
                request.TradingHours = TradingHours.Get("Default 24 x 7");
                request.Update += (s, e) => OnBarsUpdate(request, e, row, type);
                
                request.Request((r, code, msg) => {
                   if (code == ErrorCode.NoError)
                   {
                       Logger.Debug($"[OptionSignalsViewModel] BarsRequest Success: {symbol}");
                   }
                   else
                   {
                       Logger.Warn($"[OptionSignalsViewModel] BarsRequest Failed: {symbol} - {msg}");
                   }
                });

                _barsRequests[symbol] = request;
            });
        }

        private void OnBarsUpdate(BarsRequest request, BarsUpdateEventArgs e, OptionSignalsRow row, string type)
        {
            // We want the LAST COMPLETED bar (e.MaxIndex - 1) for stable signals.
            // e.MaxIndex is the currently developing bar which moves every tick.
            int closedBarIndex = e.MaxIndex - 1;
            if (closedBarIndex < 0) return;

            var bars = request.Bars;
            double close = bars.GetClose(closedBarIndex);
            string timeStr = bars.GetTime(closedBarIndex).ToString("HH:mm:ss");

            if (type == "CE")
            {
                row.CEAtrLTP = close.ToString("F2");
                row.CEAtrTime = timeStr;
            }
            else
            {
                row.PEAtrLTP = close.ToString("F2");
                row.PEAtrTime = timeStr;
            }
        }

        private void OnOptionPriceBatch(IList<OptionPriceUpdate> batch)
        {
            foreach (var update in batch)
            {
                if (_symbolToRowMap.TryGetValue(update.Symbol, out var map))
                {
                    string timeStr = update.Timestamp.ToString("HH:mm:ss");
                    if (map.type == "CE")
                    {
                        map.row.CELTP = update.Price.ToString("F2");
                        map.row.CETickTime = timeStr;
                    }
                    else
                    {
                        map.row.PELTP = update.Price.ToString("F2");
                        map.row.PETickTime = timeStr;
                    }
                }
            }
        }

        private void ClearBarsRequests()
        {
            foreach (var req in _barsRequests.Values)
            {
                req.Dispose();
            }
            _barsRequests.Clear();
        }

        public void Dispose()
        {
            StopServices();
        }
    }
}
