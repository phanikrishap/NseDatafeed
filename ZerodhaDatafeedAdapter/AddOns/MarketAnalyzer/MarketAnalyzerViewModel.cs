using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Reactive.Linq;
using System.Windows.Input;
using NinjaTrader.Cbi;
using ZerodhaDatafeedAdapter.Logging;
using ZerodhaDatafeedAdapter.Services.Analysis;
using ZerodhaDatafeedAdapter.Services.MarketData;
using ZerodhaDatafeedAdapter.Services; // For HolidayCalendarService
using ZerodhaDatafeedAdapter.Models.Reactive;
using ZerodhaDatafeedAdapter.Models; // For TickerData

namespace ZerodhaDatafeedAdapter.AddOns.MarketAnalyzer
{
    /// <summary>
    /// Row item for the Market Analyzer ListView
    /// </summary>
    public class AnalyzerRow : INotifyPropertyChanged
    {
        private string _symbol;
        private string _internalSymbol;
        private string _last;
        private string _priorClose;
        private string _change;
        private string _projOpen;
        private string _expiry;
        private string _lastUpdate;
        private string _status;
        private bool _isPositive;
        private bool _isOption;

        public string Symbol { get => _symbol; set { _symbol = value; OnPropertyChanged(nameof(Symbol)); } }
        public string InternalSymbol { get => _internalSymbol; set { _internalSymbol = value; OnPropertyChanged(nameof(InternalSymbol)); } }
        public string Last { get => _last; set { _last = value; OnPropertyChanged(nameof(Last)); } }
        public string PriorClose { get => _priorClose; set { _priorClose = value; OnPropertyChanged(nameof(PriorClose)); } }
        public string Change { get => _change; set { _change = value; OnPropertyChanged(nameof(Change)); } }
        public string ProjOpen { get => _projOpen; set { _projOpen = value; OnPropertyChanged(nameof(ProjOpen)); } }
        public string Expiry { get => _expiry; set { _expiry = value; OnPropertyChanged(nameof(Expiry)); } }
        public string LastUpdate { get => _lastUpdate; set { _lastUpdate = value; OnPropertyChanged(nameof(LastUpdate)); } }
        public string Status { get => _status; set { _status = value; OnPropertyChanged(nameof(Status)); } }
        public bool IsPositive { get => _isPositive; set { _isPositive = value; OnPropertyChanged(nameof(IsPositive)); } }
        public bool IsOption { get => _isOption; set { _isOption = value; OnPropertyChanged(nameof(IsOption)); } }

        // Internal values for calculations
        public double LastValue { get; set; }
        public double PriorCloseValue { get; set; }

        public event PropertyChangedEventHandler PropertyChanged;
        protected void OnPropertyChanged(string name) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }

    public class NiftyFuturesVPMetricsViewModel : INotifyPropertyChanged
    {
        // Session
        private string _poc = "---";
        private string _vah = "---";
        private string _val = "---";
        private string _vwap = "---";
        private string _hvnCount = "0";
        private string _barCount = "0";
        private string _hvnBuy = "0";
        private string _hvnSell = "0";
        private string _relHvnBuy = "---";
        private string _relHvnSell = "---";
        private string _cumHvnBuy = "---";
        private string _cumHvnSell = "---";
        private string _valWidth = "---";
        private string _relValWidth = "---";
        private string _cumValWidth = "---";

        // Rolling
        private string _rollHvnBuy = "0";
        private string _rollHvnSell = "0";
        private string _relRollHvnBuy = "---";
        private string _relRollHvnSell = "---";
        private string _cumRollHvnBuy = "---";
        private string _cumRollHvnSell = "---";
        private string _rollValWidth = "---";
        private string _relRollValWidth = "---";
        private string _cumRollValWidth = "---";
        
        private string _status = " - Loading...";

        public string POC { get => _poc; set { _poc = value; OnPropertyChanged(nameof(POC)); } }
        public string VAH { get => _vah; set { _vah = value; OnPropertyChanged(nameof(VAH)); } }
        public string VAL { get => _val; set { _val = value; OnPropertyChanged(nameof(VAL)); } }
        public string VWAP { get => _vwap; set { _vwap = value; OnPropertyChanged(nameof(VWAP)); } }
        public string HVNCount { get => _hvnCount; set { _hvnCount = value; OnPropertyChanged(nameof(HVNCount)); } }
        public string BarCount { get => _barCount; set { _barCount = value; OnPropertyChanged(nameof(BarCount)); } }
        
        public string HVNBuy { get => _hvnBuy; set { _hvnBuy = value; OnPropertyChanged(nameof(HVNBuy)); } }
        public string HVNSell { get => _hvnSell; set { _hvnSell = value; OnPropertyChanged(nameof(HVNSell)); } }
        public string RelHVNBuy { get => _relHvnBuy; set { _relHvnBuy = value; OnPropertyChanged(nameof(RelHVNBuy)); } }
        public string RelHVNSell { get => _relHvnSell; set { _relHvnSell = value; OnPropertyChanged(nameof(RelHVNSell)); } }
        public string CumHVNBuy { get => _cumHvnBuy; set { _cumHvnBuy = value; OnPropertyChanged(nameof(CumHVNBuy)); } }
        public string CumHVNSell { get => _cumHvnSell; set { _cumHvnSell = value; OnPropertyChanged(nameof(CumHVNSell)); } }
        
        public string ValueWidth { get => _valWidth; set { _valWidth = value; OnPropertyChanged(nameof(ValueWidth)); } }
        public string RelValueWidth { get => _relValWidth; set { _relValWidth = value; OnPropertyChanged(nameof(RelValueWidth)); } }
        public string CumValueWidth { get => _cumValWidth; set { _cumValWidth = value; OnPropertyChanged(nameof(CumValueWidth)); } }

        public string RollingHVNBuy { get => _rollHvnBuy; set { _rollHvnBuy = value; OnPropertyChanged(nameof(RollingHVNBuy)); } }
        public string RollingHVNSell { get => _rollHvnSell; set { _rollHvnSell = value; OnPropertyChanged(nameof(RollingHVNSell)); } }
        public string RelRollingHVNBuy { get => _relRollHvnBuy; set { _relRollHvnBuy = value; OnPropertyChanged(nameof(RelRollingHVNBuy)); } }
        public string RelRollingHVNSell { get => _relRollHvnSell; set { _relRollHvnSell = value; OnPropertyChanged(nameof(RelRollingHVNSell)); } }
        public string CumRollingHVNBuy { get => _cumRollHvnBuy; set { _cumRollHvnBuy = value; OnPropertyChanged(nameof(CumRollingHVNBuy)); } }
        public string CumRollingHVNSell { get => _cumRollHvnSell; set { _cumRollHvnSell = value; OnPropertyChanged(nameof(CumRollingHVNSell)); } }
        
        public string RollingValueWidth { get => _rollValWidth; set { _rollValWidth = value; OnPropertyChanged(nameof(RollingValueWidth)); } }
        public string RelRollingValueWidth { get => _relRollValWidth; set { _relRollValWidth = value; OnPropertyChanged(nameof(RelRollingValueWidth)); } }
        public string CumRollingValueWidth { get => _cumRollValWidth; set { _cumRollValWidth = value; OnPropertyChanged(nameof(CumRollingValueWidth)); } }

        public string Status { get => _status; set { _status = value; OnPropertyChanged(nameof(Status)); } }

        public event PropertyChangedEventHandler PropertyChanged;
        protected void OnPropertyChanged(string name) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }

    public class MarketAnalyzerViewModel : INotifyPropertyChanged
    {
        private ObservableCollection<AnalyzerRow> _rows;
        private string _statusText = "Waiting for data...";
        private string _priorDateText;
        private NiftyFuturesVPMetricsViewModel _vpMetrics;
        
        private IDisposable _projectedOpenSubscription;
        private IDisposable _vpMetricsSubscription;
        private IDisposable _optionsGeneratedSubscription;

        public event PropertyChangedEventHandler PropertyChanged;
        
        public MarketAnalyzerViewModel()
        {
            _rows = new ObservableCollection<AnalyzerRow>();
            _vpMetrics = new NiftyFuturesVPMetricsViewModel();
            
            // Initialize with default rows
             _rows.Add(new AnalyzerRow { Symbol = "GIFT NIFTY", Last = "---", PriorClose = "---", Change = "---", ProjOpen = "N/A", Expiry = "---", Status = "Pending", IsPositive = true, IsOption = false });
             _rows.Add(new AnalyzerRow { Symbol = "NIFTY 50", Last = "---", PriorClose = "---", Change = "---", ProjOpen = "---", Expiry = "---", Status = "Pending", IsPositive = true, IsOption = false });
             _rows.Add(new AnalyzerRow { Symbol = "SENSEX", Last = "---", PriorClose = "---", Change = "---", ProjOpen = "---", Expiry = "---", Status = "Pending", IsPositive = true, IsOption = false });

             // Nifty_I row
             var logic = MarketAnalyzerLogic.Instance;
             string niftyFutInternal = !string.IsNullOrEmpty(logic.NiftyFuturesSymbol) ? logic.NiftyFuturesSymbol : "NIFTY_I";
             string niftyFutExpiry = logic.NiftyFuturesExpiry != default ? logic.NiftyFuturesExpiry.ToString("dd-MMM") : "---";
             _rows.Add(new AnalyzerRow { Symbol = "NIFTY_I", InternalSymbol = niftyFutInternal, Last = "---", PriorClose = "---", Change = "---", ProjOpen = "N/A", Expiry = niftyFutExpiry, Status = "Pending", IsPositive = true, IsOption = false });

             var priorDate = HolidayCalendarService.Instance.GetPriorWorkingDay();
             PriorDateText = priorDate.ToString("dd-MMM-yyyy (ddd)");
        }

        public ObservableCollection<AnalyzerRow> Rows => _rows;
        public NiftyFuturesVPMetricsViewModel VPMetrics => _vpMetrics;

        public string StatusText
        {
            get => _statusText;
            set { _statusText = value; OnPropertyChanged(nameof(StatusText)); }
        }

        public string PriorDateText
        {
            get => _priorDateText;
            set { _priorDateText = value; OnPropertyChanged(nameof(PriorDateText)); }
        }

        public void StartServices()
        {
             Logger.Info("[MarketAnalyzerViewModel] Starting services...");
             try 
             {
                MarketAnalyzerService.Instance.Start();
                StatusText = "Service started - connecting...";
                
                // Subscribe to events
                MarketAnalyzerLogic.Instance.StatusUpdated += OnLogicStatusUpdated;
                MarketAnalyzerLogic.Instance.TickerUpdated += OnTickerUpdated;
                MarketAnalyzerLogic.Instance.HistoricalDataStatusChanged += OnHistoricalDataStatusChanged;
                
                 var hub = MarketDataReactiveHub.Instance;
                 _projectedOpenSubscription = hub.ProjectedOpenStream
                    .Where(state => state.IsComplete)
                    .Subscribe(OnProjectedOpenCalculated);

                 _optionsGeneratedSubscription = hub.OptionsGeneratedStream
                    .Subscribe(OnOptionsGenerated);

                // Start Metrics Service
                StartMetricsService();
             }
             catch(Exception ex)
             {
                 Logger.Error($"[MarketAnalyzerViewModel] Error starting services: {ex.Message}", ex);
                 StatusText = $"Error: {ex.Message}";
             }
        }
        
        public void StopServices()
        {
            Logger.Info("[MarketAnalyzerViewModel] Stopping services...");
            
            MarketAnalyzerLogic.Instance.StatusUpdated -= OnLogicStatusUpdated;
            MarketAnalyzerLogic.Instance.TickerUpdated -= OnTickerUpdated;
            MarketAnalyzerLogic.Instance.HistoricalDataStatusChanged -= OnHistoricalDataStatusChanged;
            
            _projectedOpenSubscription?.Dispose();
            _optionsGeneratedSubscription?.Dispose();
            _vpMetricsSubscription?.Dispose();
            
            try { NiftyFuturesMetricsService.Instance.Stop(); } catch {}
        }

        private async void StartMetricsService()
        {
             try
             {
                VPMetrics.Status = " - Starting...";
                _vpMetricsSubscription = NiftyFuturesMetricsService.Instance.MetricsStream
                    .Where(m => m != null)
                    .Subscribe(OnMetricsUpdated);
                
                await NiftyFuturesMetricsService.Instance.StartAsync();
             }
             catch(Exception ex)
             {
                 Logger.Error($"[MarketAnalyzerViewModel] Metrics Service Error: {ex.Message}", ex);
                 VPMetrics.Status = $" - Error: {ex.Message}";
             }
        }

        private void OnMetricsUpdated(NiftyFuturesVPMetrics metrics)
        {
            if (!metrics.IsValid) { VPMetrics.Status = " - No data"; return; }
            
            var vm = VPMetrics;
            vm.POC = metrics.POC.ToString("F2");
            vm.VAH = metrics.VAH.ToString("F2");
            vm.VAL = metrics.VAL.ToString("F2");
            vm.VWAP = metrics.VWAP.ToString("F2");
            vm.HVNCount = (metrics.HVNs?.Count ?? 0).ToString();
            vm.BarCount = metrics.BarCount.ToString();
            
            vm.HVNBuy = metrics.HVNBuyCount.ToString();
            vm.HVNSell = metrics.HVNSellCount.ToString();
            
            vm.RelHVNBuy = metrics.RelHVNBuy > 0 ? $"{metrics.RelHVNBuy:F0}" : "---";
            vm.RelHVNSell = metrics.RelHVNSell > 0 ? $"{metrics.RelHVNSell:F0}" : "---";
            vm.CumHVNBuy = metrics.CumHVNBuyRank > 0 ? $"{metrics.CumHVNBuyRank:F0}" : "---";
            vm.CumHVNSell = metrics.CumHVNSellRank > 0 ? $"{metrics.CumHVNSellRank:F0}" : "---";
            
            double w = metrics.VAH - metrics.VAL;
            vm.ValueWidth = $"{w:F0}";
            vm.RelValueWidth = metrics.RelValueWidth > 0 ? $"{metrics.RelValueWidth:F0}" : "---";
            vm.CumValueWidth = metrics.CumValueWidthRank > 0 ? $"{metrics.CumValueWidthRank:F0}" : "---";
            
            vm.RollingHVNBuy = metrics.RollingHVNBuyCount.ToString();
            vm.RollingHVNSell = metrics.RollingHVNSellCount.ToString();
            vm.RelRollingHVNBuy = metrics.RelHVNBuyRolling > 0 ? $"{metrics.RelHVNBuyRolling:F0}" : "---";
            vm.RelRollingHVNSell = metrics.RelHVNSellRolling > 0 ? $"{metrics.RelHVNSellRolling:F0}" : "---";
            vm.CumRollingHVNBuy = metrics.CumHVNBuyRollingRank > 0 ? $"{metrics.CumHVNBuyRollingRank:F0}" : "---";
            vm.CumRollingHVNSell = metrics.CumHVNSellRollingRank > 0 ? $"{metrics.CumHVNSellRollingRank:F0}" : "---";
            
            double rw = metrics.RollingVAH - metrics.RollingVAL;
            vm.RollingValueWidth = $"{rw:F0}";
            vm.RelRollingValueWidth = metrics.RelValueWidthRolling > 0 ? $"{metrics.RelValueWidthRolling:F0}" : "---";
            vm.CumRollingValueWidth = metrics.CumValueWidthRollingRank > 0 ? $"{metrics.CumValueWidthRollingRank:F0}" : "---";
            
            vm.Status = $" - Updated {metrics.LastUpdate:HH:mm:ss}";
        }
        
        private void OnTickerUpdated(string symbol)
        {
            try
            {
                var logic = MarketAnalyzerLogic.Instance;
                // Reuse existing logic from original file, but updating ObservableCollection
                // The logic for GIFT NIFTY, NIFTY 50, SENSEX, NIFTY_I
                // Copy-paste logic from Window but adapted for ViewModel context
                
                if (symbol == "GIFT NIFTY")
                {
                    var ticker = logic.GiftNiftyTicker;
                    if (ticker != null)
                    {
                        var row = _rows.FirstOrDefault(r => r.Symbol == "GIFT NIFTY");
                        if (row != null)
                        {
                            row.LastValue = ticker.CurrentPrice;
                            row.Last = ticker.LastPriceDisplay;
                            row.LastUpdate = ticker.LastUpdateTimeDisplay;
                            if (ticker.NetChangePercent != 0)
                            {
                                row.Change = ticker.NetChangePercentDisplay;
                                row.IsPositive = ticker.IsPositive;
                                if (ticker.Close > 0)
                                {
                                    row.PriorCloseValue = ticker.Close;
                                    row.PriorClose = ticker.Close.ToString("F2");
                                }
                                TryCalculateProjectedOpensOnce();
                            }
                        }
                    }
                }
                else if (symbol == "NIFTY" || symbol == "NIFTY 50")
                {
                    UpdateIndexRow("NIFTY 50", logic.NiftyTicker);
                }
                else if (symbol == "SENSEX")
                {
                    UpdateIndexRow("SENSEX", logic.SensexTicker);
                }
                else if (symbol == "NIFTY_I")
                {
                    var ticker = logic.NiftyFuturesTicker;
                     if (ticker != null)
                    {
                        var row = _rows.FirstOrDefault(r => r.Symbol == "NIFTY_I");
                        if (row != null)
                        {
                            row.LastValue = ticker.CurrentPrice;
                            row.Last = ticker.LastPriceDisplay;
                            row.LastUpdate = ticker.LastUpdateTimeDisplay;
                            if (!string.IsNullOrEmpty(logic.NiftyFuturesSymbol))
                            {
                                row.InternalSymbol = logic.NiftyFuturesSymbol;
                                if(logic.NiftyFuturesExpiry != default)
                                    row.Expiry = logic.NiftyFuturesExpiry.ToString("dd-MMM");
                            }
                            if (ticker.Close > 0 && row.PriorCloseValue == 0)
                            {
                                row.PriorCloseValue = ticker.Close;
                                row.PriorClose = ticker.Close.ToString("F2");
                            }
                            CalculateChange(row);
                        }
                    }
                }
                StatusText = $"Last update: {DateTime.Now:HH:mm:ss}";
            }
            catch(Exception ex)
            {
                Logger.Error($"[ViewModel] OnTickerUpdated Error: {ex.Message}");
            }
        }
        
        private void UpdateIndexRow(string symbolName, TickerData ticker)
        {
            if (ticker == null) return;
            var row = _rows.FirstOrDefault(r => r.Symbol == symbolName);
            if (row != null)
            {
                row.LastValue = ticker.CurrentPrice;
                row.Last = ticker.LastPriceDisplay;
                row.LastUpdate = ticker.LastUpdateTimeDisplay;
                if (ticker.Close > 0 && row.PriorCloseValue == 0)
                {
                    row.PriorCloseValue = ticker.Close;
                    row.PriorClose = ticker.Close.ToString("F2");
                    TryCalculateProjectedOpensOnce();
                }
                CalculateChange(row);
            }
        }
        
        private void CalculateChange(AnalyzerRow row)
        {
            if (row.PriorCloseValue > 0)
            {
                double chgPercent = (row.LastValue - row.PriorCloseValue) / row.PriorCloseValue * 100;
                row.Change = $"{chgPercent:+0.00;-0.00;0.00}%";
                row.IsPositive = chgPercent >= 0;
            }
        }
        
        private void TryCalculateProjectedOpensOnce()
        {
             var logic = MarketAnalyzerLogic.Instance;
             var giftTicker = logic.GiftNiftyTicker;
             if (giftTicker == null || giftTicker.NetChangePercent == 0) return;
             
             double giftChgDecimal = giftTicker.NetChangePercent / 100.0;
             var niftyRow = _rows.FirstOrDefault(r => r.Symbol == "NIFTY 50");
             var sensexRow = _rows.FirstOrDefault(r => r.Symbol == "SENSEX");
             
             if (niftyRow != null && niftyRow.PriorCloseValue > 0 && niftyRow.ProjOpen == "---")
             {
                 niftyRow.ProjOpen = (niftyRow.PriorCloseValue * (1 + giftChgDecimal)).ToString("F0");
             }
             if (sensexRow != null && sensexRow.PriorCloseValue > 0 && sensexRow.ProjOpen == "---")
             {
                 sensexRow.ProjOpen = (sensexRow.PriorCloseValue * (1 + giftChgDecimal)).ToString("F0");
             }
        }

        private void OnProjectedOpenCalculated(ProjectedOpenState state)
        {
            var niftyRow = _rows.FirstOrDefault(r => r.Symbol == "NIFTY 50");
            var sensexRow = _rows.FirstOrDefault(r => r.Symbol == "SENSEX");
             if (niftyRow != null && niftyRow.ProjOpen == "---") niftyRow.ProjOpen = state.NiftyProjectedOpen.ToString("F0");
             if (sensexRow != null && sensexRow.ProjOpen == "---") sensexRow.ProjOpen = state.SensexProjectedOpen.ToString("F0");
        }

        private void OnOptionsGenerated(OptionsGeneratedEvent evt)
        {
             if(evt != null)
             {
                 StatusText = $"Generated {evt.Options.Count} {evt.SelectedUnderlying} options for {evt.SelectedExpiry:dd-MMM} (DTE: {evt.DTE})";
             }
        }

        private void OnLogicStatusUpdated(string msg) { /* Log only */ }
        
        private void OnHistoricalDataStatusChanged(string symbol, string status)
        {
            string target = symbol;
            if (symbol.Equals("GIFT_NIFTY", StringComparison.OrdinalIgnoreCase)) target = "GIFT NIFTY";
            else if (symbol.Equals("NIFTY", StringComparison.OrdinalIgnoreCase)) target = "NIFTY 50";
            else if (symbol.Equals("NIFTY_I", StringComparison.OrdinalIgnoreCase)) target = "NIFTY_I"; // Or NIFTY_I from list
            
            var row = _rows.FirstOrDefault(r => r.Symbol.Equals(target, StringComparison.OrdinalIgnoreCase) || r.Symbol == target);
            if (row != null) row.Status = status;
        }

        protected void OnPropertyChanged(string name) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }
}
