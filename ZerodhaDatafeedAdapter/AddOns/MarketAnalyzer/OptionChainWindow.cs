using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Xml.Linq;
using NinjaTrader.Cbi;
using NinjaTrader.Gui.Tools;
using ZerodhaDatafeedAdapter.Helpers;
using ZerodhaDatafeedAdapter.Models;
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
    /// Modularized Option Chain Tab Page.
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

        private readonly Dictionary<string, (double price, DateTime timestamp)> _pendingPriceUpdates = new Dictionary<string, (double, DateTime)>();
        private readonly Dictionary<string, string> _pendingStatusUpdates = new Dictionary<string, string>();
        private readonly Dictionary<string, (double price, double ce, double pe)> _pendingStraddleUpdates = new Dictionary<string, (double, double, double)>();
        private readonly Dictionary<string, VWAPData> _pendingVWAPUpdates = new Dictionary<string, VWAPData>();
        private readonly object _throttleLock = new object();
        private System.Windows.Threading.DispatcherTimer _uiUpdateTimer;

        public OptionChainTabPage()
        {
            Logger.Info("[OptionChainTabPage] Initializing modular UI");
            
            _rows = new ObservableCollection<OptionChainRow>();
            BuildUI();
            InitializeThrottling();
            
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

        private void InitializeThrottling()
        {
            _uiUpdateTimer = new System.Windows.Threading.DispatcherTimer();
            _uiUpdateTimer.Interval = TimeSpan.FromMilliseconds(500);
            _uiUpdateTimer.Tick += OnUiUpdateTimerTick;
            _uiUpdateTimer.Start();
        }

        private void OnTabPageLoaded(object sender, RoutedEventArgs e)
        {
            SubscriptionManager.Instance.OptionPriceUpdated += OnOptionPriceUpdated;
            SubscriptionManager.Instance.OptionStatusUpdated += OnOptionStatusUpdated;
            SubscriptionManager.Instance.SymbolResolved += OnSymbolResolved;
            MarketAnalyzerLogic.Instance.OptionsGenerated += OnOptionsGenerated;
            VWAPDataCache.Instance.VWAPUpdated += OnVWAPUpdated;

            var adapter = Connector.Instance.GetAdapter() as ZerodhaAdapter;
            if (adapter?.SyntheticStraddleService != null)
                adapter.SyntheticStraddleService.StraddlePriceCalculated += OnStraddlePriceCalculated;
        }

        private void OnTabPageUnloaded(object sender, RoutedEventArgs e)
        {
            _uiUpdateTimer?.Stop();
            SubscriptionManager.Instance.OptionPriceUpdated -= OnOptionPriceUpdated;
            SubscriptionManager.Instance.OptionStatusUpdated -= OnOptionStatusUpdated;
            SubscriptionManager.Instance.SymbolResolved -= OnSymbolResolved;
            MarketAnalyzerLogic.Instance.OptionsGenerated -= OnOptionsGenerated;
            VWAPDataCache.Instance.VWAPUpdated -= OnVWAPUpdated;

            var adapter = Connector.Instance.GetAdapter() as ZerodhaAdapter;
            if (adapter?.SyntheticStraddleService != null)
                adapter.SyntheticStraddleService.StraddlePriceCalculated -= OnStraddlePriceCalculated;
        }

        private void OnOptionsGenerated(List<MappedInstrument> options)
        {
            Dispatcher.InvokeAsync(() =>
            {
                _rows.Clear();
                _symbolToRowMap.Clear();
                _generatedToZerodhaMap.Clear();
                _straddleSymbolToRowMap.Clear();

                if (options.Count == 0) return;

                var first = options.First();
                _underlying = first.underlying;
                _expiry = first.expiry;

                _headerControl.Underlying = _underlying;
                _headerControl.Expiry = _expiry.HasValue ? _expiry.Value.ToString("dd-MMM-yyyy") : "---";

                var strikeGroups = options.Where(o => o.strike.HasValue).GroupBy(o => o.strike.Value).OrderBy(g => g.Key);

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
            });
        }

        private void OnUiUpdateTimerTick(object sender, EventArgs e)
        {
            bool needsRefresh = false;
            lock (_throttleLock)
            {
                if (_pendingPriceUpdates.Count == 0 && _pendingStatusUpdates.Count == 0 && _pendingStraddleUpdates.Count == 0 && _pendingVWAPUpdates.Count == 0)
                    return;

                foreach (var kvp in _pendingPriceUpdates)
                {
                    if (_symbolToRowMap.TryGetValue(kvp.Key, out var mapping))
                    {
                        var (row, optType) = mapping;
                        var (price, ts) = kvp.Value;
                        string timeStr = DateTimeHelper.ClampToMarketHours(ts).ToString("HH:mm:ss");
                        if (optType == "CE") { row.CELast = price.ToString("F2"); row.CEPrice = price; row.CEUpdateTime = timeStr; }
                        else { row.PELast = price.ToString("F2"); row.PEPrice = price; row.PEUpdateTime = timeStr; }
                        needsRefresh = true;
                    }
                }
                _pendingPriceUpdates.Clear();

                foreach (var kvp in _pendingStatusUpdates)
                {
                    if (_symbolToRowMap.TryGetValue(kvp.Key, out var mapping))
                    {
                        if (mapping.optionType == "CE") mapping.row.CEStatus = kvp.Value;
                        else mapping.row.PEStatus = kvp.Value;
                        needsRefresh = true;
                    }
                }
                _pendingStatusUpdates.Clear();

                foreach (var kvp in _pendingStraddleUpdates)
                {
                    if (_straddleSymbolToRowMap.TryGetValue(kvp.Key, out var row))
                    {
                        row.SyntheticStraddlePrice = kvp.Value.price;
                        needsRefresh = true;
                    }
                }
                _pendingStraddleUpdates.Clear();

                foreach (var kvp in _pendingVWAPUpdates)
                {
                    var vwap = kvp.Value;
                    if (_symbolToRowMap.TryGetValue(kvp.Key, out var mapping))
                    {
                        if (mapping.optionType == "CE") { mapping.row.CEVWAP = vwap.VWAP; if (mapping.row.CEPrice > 0) mapping.row.CEVWAPPosition = vwap.GetPosition(mapping.row.CEPrice); }
                        else { mapping.row.PEVWAP = vwap.VWAP; if (mapping.row.PEPrice > 0) mapping.row.PEVWAPPosition = vwap.GetPosition(mapping.row.PEPrice); }
                        needsRefresh = true;
                    }
                    else if (_straddleSymbolToRowMap.TryGetValue(kvp.Key, out var strRow))
                    {
                        strRow.StraddleVWAP = vwap.VWAP;
                        needsRefresh = true;
                    }
                }
                _pendingVWAPUpdates.Clear();
            }

            if (needsRefresh) UpdateATMAndHistograms();
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

        private void OnOptionPriceUpdated(string s, double p)
        {
            MarketAnalyzerLogic.Instance.UpdateOptionPrice(s, (decimal)p, DateTime.Now);
            lock (_throttleLock) _pendingPriceUpdates[s] = (p, DateTime.Now);
        }

        private void OnOptionStatusUpdated(string s, string st) { lock (_throttleLock) _pendingStatusUpdates[s] = st; }
        private void OnVWAPUpdated(string s, VWAPData v) { lock (_throttleLock) _pendingVWAPUpdates[s] = v; }
        private void OnStraddlePriceCalculated(string s, double p, double c, double pe) { lock (_throttleLock) _pendingStraddleUpdates[s] = (p, c, pe); }
        private void OnSymbolResolved(string g, string z)
        {
            Dispatcher.InvokeAsync(() => {
                _generatedToZerodhaMap[g] = z;
                if (_symbolToRowMap.TryGetValue(g, out var m)) { _symbolToRowMap[z] = m; SubscribeForPersistence(z); }
            });
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

        public override void Cleanup() { _uiUpdateTimer?.Stop(); base.Cleanup(); }
        protected override string GetHeaderPart(string variable) => _instrument?.FullName ?? (_underlying != null ? $"{_underlying} Options" : "Option Chain");
        protected override void Restore(XElement element) { var attr = element.Attribute("LastInstrument"); if (attr != null) Instrument = Instrument.GetInstrument(attr.Value); }
        protected override void Save(XElement element) { if (_instrument != null) element.SetAttributeValue("LastInstrument", _instrument.FullName); }
        private void UpdateHeader() { Dispatcher.InvokeAsync(() => { try { base.RefreshHeader(); } catch { } }); }
    }
}
