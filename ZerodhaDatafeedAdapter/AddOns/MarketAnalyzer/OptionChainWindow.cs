using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Reactive.Disposables;
using System.Reactive.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Xml.Linq;
using NinjaTrader.Cbi;
using NinjaTrader.Gui.Tools;
using ZerodhaDatafeedAdapter.Helpers;
using ZerodhaDatafeedAdapter.Models;
using ZerodhaDatafeedAdapter.Models.Reactive;
using ZerodhaDatafeedAdapter.Services.Analysis;
using ZerodhaDatafeedAdapter.SyntheticInstruments;
using ZerodhaDatafeedAdapter.AddOns.MarketAnalyzer.Models;
using ZerodhaDatafeedAdapter.AddOns.MarketAnalyzer.Controls;

namespace ZerodhaDatafeedAdapter.AddOns.MarketAnalyzer
{
    /// <summary>
    /// Option Chain Window - hosts the OptionChainTabPage
    /// </summary>
    public class OptionChainWindow : NTWindow, IWorkspacePersistence
    {
        public OptionChainWindow()
        {
            Logger.Info("[OptionChainWindow] Constructor: Creating window");

            Caption = "Option Chain";
            Width = 800;
            Height = 600;

            TabControl tabControl = new TabControl();
            tabControl.Style = Application.Current.TryFindResource("TabControlStyle") as Style;

            OptionChainTabPage tabPage = new OptionChainTabPage();
            tabControl.Items.Add(tabPage);

            Content = tabControl;

            Logger.Info("[OptionChainWindow] Constructor: Window created with modular components");
        }

        public void Restore(XDocument document, XElement element) { }
        public void Save(XDocument document, XElement element) { }
        public WorkspaceOptions WorkspaceOptions { get; set; }
    }

    /// <summary>
    /// Modularized Option Chain Tab Page using Rx-based MarketDataReactiveHub.
    /// All market data flows through the hub's reactive streams with built-in backpressure.
    /// UI logic is delegated to specialized controls in the Controls/ directory.
    /// </summary>
    public class OptionChainTabPage : NTTabPage, IInstrumentProvider
    {
        private OptionChainHeaderControl _headerControl;
        private OptionChainListView _listViewControl;
        private OptionChainStatusBar _statusBarControl;
        private ObservableCollection<OptionChainRow> _rows;

        private Instrument _instrument;
        private string _underlying = "NIFTY";
        private DateTime? _expiry;

        private Dictionary<string, (OptionChainRow row, string optionType)> _symbolToRowMap = new Dictionary<string, (OptionChainRow, string)>();
        private Dictionary<string, string> _generatedToZerodhaMap = new Dictionary<string, string>();
        private Dictionary<string, OptionChainRow> _straddleSymbolToRowMap = new Dictionary<string, OptionChainRow>();
        private HashSet<string> _persistenceSubscribedSymbols = new HashSet<string>();

        // Rx subscriptions - disposed on unload
        private CompositeDisposable _subscriptions;

        // Track if UI refresh is needed after batch processing
        private bool _needsUIRefresh = false;

        public OptionChainTabPage()
        {
            Logger.Info("[OptionChainTabPage] Initializing modular UI with Rx streams");

            _rows = new ObservableCollection<OptionChainRow>();
            BuildUI();

            Loaded += OnTabPageLoaded;
            Unloaded += OnTabPageUnloaded;
        }

        private void BuildUI()
        {
            var dockPanel = new DockPanel { Background = new SolidColorBrush(Color.FromRgb(27, 27, 28)) };
            Content = dockPanel;

            _headerControl = new OptionChainHeaderControl();
            DockPanel.SetDock(_headerControl, Dock.Top);
            dockPanel.Children.Add(_headerControl);

            _statusBarControl = new OptionChainStatusBar();
            DockPanel.SetDock(_statusBarControl, Dock.Bottom);
            dockPanel.Children.Add(_statusBarControl);

            _listViewControl = new OptionChainListView { ItemsSource = _rows };
            _listViewControl.MouseLeftButtonUpEvent += OnListViewClick;
            dockPanel.Children.Add(_listViewControl);
        }

        private void OnTabPageLoaded(object sender, RoutedEventArgs e)
        {
            Logger.Info("[OptionChainTabPage] OnTabPageLoaded: Setting up Rx subscriptions");

            _subscriptions = new CompositeDisposable();
            var hub = MarketDataReactiveHub.Instance;

            // Subscribe to options generated events
            _subscriptions.Add(
                hub.OptionsGeneratedStream
                    .ObserveOnDispatcher()
                    .Subscribe(OnOptionsGenerated, ex => Logger.Error($"[OptionChainTabPage] OptionsGenerated error: {ex.Message}")));

            // Subscribe to batched option prices (100ms/50 items - hub handles backpressure)
            _subscriptions.Add(
                hub.OptionPriceBatchStream
                    .ObserveOnDispatcher()
                    .Subscribe(OnOptionPriceBatch, ex => Logger.Error($"[OptionChainTabPage] OptionPriceBatch error: {ex.Message}")));

            // Subscribe to option status updates (sampled every 100ms per symbol)
            _subscriptions.Add(
                hub.OptionStatusStream
                    .GroupBy(s => s.Symbol)
                    .SelectMany(g => g.Sample(TimeSpan.FromMilliseconds(100)))
                    .ObserveOnDispatcher()
                    .Subscribe(OnOptionStatus, ex => Logger.Error($"[OptionChainTabPage] OptionStatus error: {ex.Message}")));

            // Subscribe to symbol resolution events
            _subscriptions.Add(
                hub.SymbolResolvedStream
                    .ObserveOnDispatcher()
                    .Subscribe(OnSymbolResolved, ex => Logger.Error($"[OptionChainTabPage] SymbolResolved error: {ex.Message}")));

            // Subscribe to VWAP updates (sampled every 200ms per symbol)
            _subscriptions.Add(
                hub.VWAPStream
                    .GroupBy(v => v.Symbol)
                    .SelectMany(g => g.Sample(TimeSpan.FromMilliseconds(200)))
                    .ObserveOnDispatcher()
                    .Subscribe(OnVWAPUpdate, ex => Logger.Error($"[OptionChainTabPage] VWAP error: {ex.Message}")));

            // Subscribe to straddle price updates (batched every 200ms)
            _subscriptions.Add(
                hub.StraddlePriceStream
                    .Buffer(TimeSpan.FromMilliseconds(200))
                    .Where(batch => batch.Count > 0)
                    .ObserveOnDispatcher()
                    .Subscribe(OnStraddlePriceBatch, ex => Logger.Error($"[OptionChainTabPage] StraddlePrice error: {ex.Message}")));

            Logger.Info("[OptionChainTabPage] OnTabPageLoaded: Rx subscriptions active");
        }

        private void OnTabPageUnloaded(object sender, RoutedEventArgs e)
        {
            Logger.Info("[OptionChainTabPage] OnTabPageUnloaded: Disposing Rx subscriptions");
            _subscriptions?.Dispose();
            _subscriptions = null;
        }

        private void OnOptionsGenerated(OptionsGeneratedEvent evt)
        {
            _rows.Clear();
            _symbolToRowMap.Clear();
            _generatedToZerodhaMap.Clear();
            _straddleSymbolToRowMap.Clear();

            if (evt.Options == null || evt.Options.Count == 0) return;

            var first = evt.Options.First();
            _underlying = first.underlying;
            _expiry = first.expiry;

            _headerControl.Underlying = _underlying;
            _headerControl.Expiry = _expiry.HasValue ? _expiry.Value.ToString("dd-MMM-yyyy") : "---";

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

                if (_expiry.HasValue && ce != null && pe != null)
                {
                    string monthAbbr = _expiry.Value.ToString("MMM").ToUpper();
                    string straddleSymbol = $"{_underlying}{_expiry.Value:yy}{monthAbbr}{group.Key:F0}_STRDL";
                    row.StraddleSymbol = straddleSymbol;
                    _straddleSymbolToRowMap[straddleSymbol] = row;
                }

                _rows.Add(row);
            }

            _statusBarControl.StatusText = $"Loaded {_rows.Count} strikes for {_underlying}";
            _listViewControl.Refresh();
        }

        private void OnOptionPriceBatch(IList<OptionPriceUpdate> batch)
        {
            _needsUIRefresh = false;

            foreach (var update in batch)
            {
                // Also notify MarketAnalyzerLogic for ATM tracking
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
                    _needsUIRefresh = true;
                }
            }

            if (_needsUIRefresh)
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

            foreach (var row in _rows)
            {
                row.IsATM = false;
                maxPrice = Math.Max(maxPrice, Math.Max(row.CEPrice, row.PEPrice));
                if (row.CEPrice > 0 && row.PEPrice > 0)
                {
                    double s = row.CEPrice + row.PEPrice;
                    if (s < minStraddle) { minStraddle = s; atmRow = row; }
                }
            }

            if (maxPrice > 0)
            {
                double invMax = 100.0 / maxPrice;
                foreach (var row in _rows)
                {
                    row.CEHistogramWidth = row.CEPrice * invMax;
                    row.PEHistogramWidth = row.PEPrice * invMax;
                }
            }

            if (atmRow != null)
            {
                atmRow.IsATM = true;
                _headerControl.ATMStrike = $"{atmRow.Strike:F0} ({minStraddle:F2})";
                MarketAnalyzerLogic.Instance.SetATMStrike(_underlying, (decimal)atmRow.Strike);
            }
        }

        private void OnListViewClick(object sender, MouseButtonEventArgs e)
        {
            var row = _listViewControl.SelectedItem;
            if (row == null) return;

            var pos = e.GetPosition((UIElement)sender);
            string sym = null;
            if (pos.X < 235) sym = row.CESymbol;
            else if (pos.X > 315) sym = row.PESymbol;

            if (!string.IsNullOrEmpty(sym))
            {
                var nt = Instrument.GetInstrument(sym);
                if (nt != null) Instrument = nt;
                else _headerControl.SetSelectedInstrumentError($"{sym} (not in NT)");
            }
        }

        private void SubscribeForPersistence(string s)
        {
            if (_persistenceSubscribedSymbols.Contains(s)) return;
            var nt = Instrument.GetInstrument(s);
            if (nt == null) return;
            var adapter = Connector.Instance?.GetAdapter() as ZerodhaAdapter;
            adapter?.SubscribeMarketData(nt, (t, p, v, time, a5) => { });
            _persistenceSubscribedSymbols.Add(s);
        }

        public Instrument Instrument
        {
            get => _instrument;
            set { if (_instrument != value) { _instrument = value; if (value != null) _headerControl.SelectedInstrument = value.FullName; UpdateHeader(); } }
        }

        public override void Cleanup()
        {
            _subscriptions?.Dispose();
            base.Cleanup();
        }

        protected override string GetHeaderPart(string variable) => _instrument?.FullName ?? (_underlying != null ? $"{_underlying} Options" : "Option Chain");
        protected override void Restore(XElement element) { var attr = element.Attribute("LastInstrument"); if (attr != null) Instrument = Instrument.GetInstrument(attr.Value); }
        protected override void Save(XElement element) { if (_instrument != null) element.SetAttributeValue("LastInstrument", _instrument.FullName); }
        private void UpdateHeader() { Dispatcher.InvokeAsync(() => { try { base.RefreshHeader(); } catch { } }); }
    }
}
