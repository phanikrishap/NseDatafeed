using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Reactive.Disposables;
using System.Reactive.Linq;
using System.Windows;
using System.Windows.Input;
using NinjaTrader.Cbi;
using ZerodhaDatafeedAdapter.AddOns.MarketAnalyzer.Models;
using ZerodhaDatafeedAdapter.Helpers;
using ZerodhaDatafeedAdapter.Logging;
using ZerodhaDatafeedAdapter.Models;
using ZerodhaDatafeedAdapter.Models.Reactive;
using ZerodhaDatafeedAdapter.Services.Analysis;
using ZerodhaDatafeedAdapter.ViewModels;

namespace ZerodhaDatafeedAdapter.AddOns.MarketAnalyzer
{
    public class OptionChainViewModel : ViewModelBase, IDisposable
    {
        private CompositeDisposable _subscriptions;
        
        // Data Collections
        public ObservableCollection<OptionChainRow> Rows { get; } = new ObservableCollection<OptionChainRow>();
        
        // Maps for fast lookups
        private Dictionary<string, (OptionChainRow row, string optionType)> _symbolToRowMap = new Dictionary<string, (OptionChainRow, string)>();
        private Dictionary<string, string> _generatedToZerodhaMap = new Dictionary<string, string>();
        private Dictionary<string, OptionChainRow> _straddleSymbolToRowMap = new Dictionary<string, OptionChainRow>();
        private HashSet<string> _persistenceSubscribedSymbols = new HashSet<string>();

        // Properties backed by fields
        private string _underlying = "NIFTY";
        private string _expiry = "---";
        private string _atmStrike = "---";
        private string _statusText = "Ready";
        private string _strikePositionText = "-- above | -- below ATM";
        private string _selectedInstrumentMessage = "(Click CE/PE row to select and link to chart)";
        private bool _isSelectedInstrumentError;

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

        public string ATMStrike
        {
            get => _atmStrike;
            set { if (_atmStrike != value) { _atmStrike = value; OnPropertyChanged(); } }
        }

        public string StatusText
        {
            get => _statusText;
            set { if (_statusText != value) { _statusText = value; OnPropertyChanged(); } }
        }

        public string StrikePositionText
        {
            get => _strikePositionText;
            set { if (_strikePositionText != value) { _strikePositionText = value; OnPropertyChanged(); } }
        }

        public string SelectedInstrumentMessage
        {
            get => _selectedInstrumentMessage;
            set 
            { 
                if (_selectedInstrumentMessage != value) 
                { 
                    _selectedInstrumentMessage = value; 
                    OnPropertyChanged(); 
                } 
            }
        }

        public bool IsSelectedInstrumentError
        {
            get => _isSelectedInstrumentError;
            set
            {
                if (_isSelectedInstrumentError != value)
                {
                    _isSelectedInstrumentError = value;
                    OnPropertyChanged();
                }
            }
        }

        // Instrument property for linking
        private Instrument _instrument;
        public Instrument Instrument
        {
            get => _instrument;
            set
            {
                if (_instrument != value)
                {
                    _instrument = value;
                    OnPropertyChanged();
                    // Update Selected Instrument Message
                    if (value != null)
                    {
                         SelectedInstrumentMessage = value.FullName;
                         IsSelectedInstrumentError = false;
                    }
                }
            }
        }

        public OptionChainViewModel()
        {
            Logger.Info("[OptionChainViewModel] Initialized");
        }

        public void StartServices()
        {
            Logger.Info("[OptionChainViewModel] Starting services / subscriptions");
            _subscriptions = new CompositeDisposable();
            var hub = MarketDataReactiveHub.Instance;

            // Subscribe to streams
            _subscriptions.Add(hub.OptionsGeneratedStream
                .ObserveOnDispatcher()
                .Subscribe(OnOptionsGenerated, ex => Logger.Error($"[OptionChainViewModel] OptionsGenerated error: {ex.Message}")));

            _subscriptions.Add(hub.OptionPriceBatchStream
                .ObserveOnDispatcher()
                .Subscribe(OnOptionPriceBatch, ex => Logger.Error($"[OptionChainViewModel] OptionPriceBatch error: {ex.Message}")));

            _subscriptions.Add(hub.OptionStatusStream
                .GroupBy(s => s.Symbol)
                .SelectMany(g => g.Sample(TimeSpan.FromMilliseconds(100)))
                .ObserveOnDispatcher()
                .Subscribe(OnOptionStatus, ex => Logger.Error($"[OptionChainViewModel] OptionStatus error: {ex.Message}")));

            _subscriptions.Add(hub.SymbolResolvedStream
                .ObserveOnDispatcher()
                .Subscribe(OnSymbolResolved, ex => Logger.Error($"[OptionChainViewModel] SymbolResolved error: {ex.Message}")));

            _subscriptions.Add(hub.VWAPStream
                .GroupBy(v => v.Symbol)
                .SelectMany(g => g.Sample(TimeSpan.FromMilliseconds(200)))
                .ObserveOnDispatcher()
                .Subscribe(OnVWAPUpdate, ex => Logger.Error($"[OptionChainViewModel] VWAP error: {ex.Message}")));

            _subscriptions.Add(hub.StraddlePriceStream
                .Buffer(TimeSpan.FromMilliseconds(200))
                .Where(batch => batch.Count > 0) 
                 // Note: Original code used buffer w/ count check implicitly via logic, keeping similar.
                .ObserveOnDispatcher()
                .Subscribe(OnStraddlePriceBatch, ex => Logger.Error($"[OptionChainViewModel] StraddlePrice error: {ex.Message}")));
        }

        public void StopServices()
        {
            Logger.Info("[OptionChainViewModel] Stopping services");
            _subscriptions?.Dispose();
            _subscriptions = null;
        }

        public void Dispose()
        {
            StopServices();
        }

        // --- Logic Methods ---

        private void OnOptionsGenerated(OptionsGeneratedEvent evt)
        {
            Logger.Info($"[OptionChainViewModel] OnOptionsGenerated: {evt?.Options?.Count ?? 0} options");

            Rows.Clear();
            _symbolToRowMap.Clear();
            _generatedToZerodhaMap.Clear();
            _straddleSymbolToRowMap.Clear();

            if (evt?.Options == null || evt.Options.Count == 0)
            {
                Logger.Warn("[OptionChainViewModel] No options received");
                return;
            }

            var first = evt.Options.First();
            Underlying = first.underlying;
            Expiry = first.expiry.HasValue ? first.expiry.Value.ToString("dd-MMM-yyyy") : "---";

            var strikeGroups = evt.Options.Where(o => o.strike.HasValue).GroupBy(o => o.strike.Value).OrderBy(g => g.Key);

            foreach (var group in strikeGroups)
            {
                var row = new OptionChainRow { Strike = group.Key };
                var ce = group.FirstOrDefault(o => o.option_type == "CE");
                var pe = group.FirstOrDefault(o => o.option_type == "PE");

                if (ce != null)
                {
                    row.CESymbol = ce.symbol;
                    row.CEStatus = "Pending";
                    _symbolToRowMap[ce.symbol] = (row, "CE");
                    SubscribeForPersistence(ce.symbol);
                }

                if (pe != null)
                {
                    row.PESymbol = pe.symbol;
                    row.PEStatus = "Pending";
                    _symbolToRowMap[pe.symbol] = (row, "PE");
                    SubscribeForPersistence(pe.symbol);
                }

                if (first.expiry.HasValue && ce != null && pe != null)
                {
                    string monthAbbr = first.expiry.Value.ToString("MMM").ToUpper();
                    string straddleSymbol = $"{Underlying}{first.expiry.Value:yy}{monthAbbr}{group.Key:F0}_STRDL";
                    row.StraddleSymbol = straddleSymbol;
                    _straddleSymbolToRowMap[straddleSymbol] = row;
                }

                Rows.Add(row);
            }

            StatusText = $"Loaded {Rows.Count} strikes for {Underlying}";
        }

        private void OnOptionPriceBatch(IList<OptionPriceUpdate> batch)
        {
            bool needsRefresh = false;

            foreach (var update in batch)
            {
                MarketAnalyzerLogic.Instance.UpdateOptionPrice(update.Symbol, (decimal)update.Price, update.Timestamp);

                if (_symbolToRowMap.TryGetValue(update.Symbol, out var mapping))
                {
                    var (row, optType) = mapping;
                    string timeStr = DateTimeHelper.ClampToMarketHours(update.Timestamp).ToString("HH:mm:ss");

                    if (optType == "CE")
                    {
                        row.CELast = update.Price.ToString("F2");
                        row.CEPrice = update.Price;
                        row.CEUpdateTime = timeStr;
                    }
                    else
                    {
                        row.PELast = update.Price.ToString("F2");
                        row.PEPrice = update.Price;
                        row.PEUpdateTime = timeStr;
                    }
                    needsRefresh = true;
                }
            }

            if (needsRefresh)
            {
                UpdateATMAndHistograms();
            }
        }

        private void OnOptionStatus(OptionStatusUpdate update)
        {
            if (_symbolToRowMap.TryGetValue(update.Symbol, out var mapping))
            {
                if (mapping.optionType == "CE")
                    mapping.row.CEStatus = update.Status;
                else
                    mapping.row.PEStatus = update.Status;
            }
        }

        private void OnSymbolResolved((string generated, string zerodha) resolved)
        {
            _generatedToZerodhaMap[resolved.generated] = resolved.zerodha;
            if (_symbolToRowMap.TryGetValue(resolved.generated, out var m))
            {
                _symbolToRowMap[resolved.zerodha] = m;
                SubscribeForPersistence(resolved.zerodha);
            }
        }

        private void OnVWAPUpdate(VWAPUpdate vwap)
        {
            if (_symbolToRowMap.TryGetValue(vwap.Symbol, out var mapping))
            {
                if (mapping.optionType == "CE")
                {
                    mapping.row.CEVWAP = vwap.VWAP;
                    if (mapping.row.CEPrice > 0)
                        mapping.row.CEVWAPPosition = vwap.GetPosition(mapping.row.CEPrice);
                }
                else
                {
                    mapping.row.PEVWAP = vwap.VWAP;
                    if (mapping.row.PEPrice > 0)
                        mapping.row.PEVWAPPosition = vwap.GetPosition(mapping.row.PEPrice);
                }
            }
            else if (_straddleSymbolToRowMap.TryGetValue(vwap.Symbol, out var strRow))
            {
                strRow.StraddleVWAP = vwap.VWAP;
            }
        }

        private void OnStraddlePriceBatch(IList<StraddlePriceUpdate> batch)
        {
            foreach (var update in batch)
            {
                if (_straddleSymbolToRowMap.TryGetValue(update.Symbol, out var row))
                {
                    row.SyntheticStraddlePrice = update.Price;
                }
            }
        }

        private void UpdateATMAndHistograms()
        {
            OptionChainRow atmRow = null;
            double minStraddle = double.MaxValue;
            double maxPrice = 0;
            int atmIndex = -1;

            for (int i = 0; i < Rows.Count; i++)
            {
                var row = Rows[i];
                row.IsATM = false;
                maxPrice = Math.Max(maxPrice, Math.Max(row.CEPrice, row.PEPrice));
                if (row.CEPrice > 0 && row.PEPrice > 0)
                {
                    double s = row.CEPrice + row.PEPrice;
                    if (s < minStraddle) { minStraddle = s; atmRow = row; atmIndex = i; }
                }
            }

            if (maxPrice > 0)
            {
                double invMax = 100.0 / maxPrice;
                foreach (var row in Rows)
                {
                    row.CEHistogramWidth = row.CEPrice * invMax;
                    row.PEHistogramWidth = row.PEPrice * invMax;
                }
            }

            if (atmRow != null)
            {
                atmRow.IsATM = true;
                ATMStrike = $"{atmRow.Strike:F0} ({minStraddle:F2})";
                MarketAnalyzerLogic.Instance.SetATMStrike(Underlying, (decimal)atmRow.Strike);

                int strikesAbove = atmIndex;
                int strikesBelow = Rows.Count - atmIndex - 1;
                StrikePositionText = $"{strikesAbove} above | {strikesBelow} below ATM ({Rows.Count} total)";
            }
        }

        private void SubscribeForPersistence(string s)
        {
            if (_persistenceSubscribedSymbols.Contains(s)) return;
            var nt = Instrument.GetInstrument(s);
            if (nt == null) return;
            // Original code used Connector.Instance.GetAdapter() which could be null or return Generic adapter
            var adapter = Connector.Instance?.GetAdapter() as ZerodhaAdapter;
            adapter?.SubscribeMarketData(nt, (t, p, v, time, a5) => { });
            _persistenceSubscribedSymbols.Add(s);
        }

        public void HandleRowClick(OptionChainRow row, bool isCall)
        {
            if (row == null) return;
            string sym = isCall ? row.CESymbol : row.PESymbol;
            
            if (!string.IsNullOrEmpty(sym))
            {
                var nt = Instrument.GetInstrument(sym);
                if (nt != null)
                {
                    Instrument = nt;
                }
                else
                {
                     SelectedInstrumentMessage = $"{sym} (not in NT)";
                     IsSelectedInstrumentError = true;
                }
            }
        }
    }
}
