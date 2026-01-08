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

        // Composite Profile (1D, 3D, 5D, 10D)
        private string _compPOC1D = "---", _compPOC3D = "---", _compPOC5D = "---", _compPOC10D = "---";
        private string _compVAH1D = "---", _compVAH3D = "---", _compVAH5D = "---", _compVAH10D = "---";
        private string _compVAL1D = "---", _compVAL3D = "---", _compVAL5D = "---", _compVAL10D = "---";
        private string _compRng1D = "---", _compRng3D = "---", _compRng5D = "---", _compRng10D = "---";
        private string _cVsAvg1D = "---", _cVsAvg3D = "---", _cVsAvg5D = "---", _cVsAvg10D = "---";
        private string _rollRng1D = "---", _rollRng3D = "---", _rollRng5D = "---", _rollRng10D = "---";
        private string _rVsAvg1D = "---", _rVsAvg3D = "---", _rVsAvg5D = "---", _rVsAvg10D = "---";
        private string _yearlyHigh = "---", _yearlyLow = "---", _yearlyHighDate = "---", _yearlyLowDate = "---";
        private string _control = "---", _migration = "---";
        private string _dailyBarCount = "0";

        // Prior EOD fields (D-2, D-3, D-4 ranges for each period)
        private string _d2Rng1D = "---", _d2Rng3D = "---", _d2Rng5D = "---", _d2Rng10D = "---";
        private string _d2Pct1D = "---", _d2Pct3D = "---", _d2Pct5D = "---", _d2Pct10D = "---";
        private string _d3Rng1D = "---", _d3Rng3D = "---", _d3Rng5D = "---", _d3Rng10D = "---";
        private string _d3Pct1D = "---", _d3Pct3D = "---", _d3Pct5D = "---", _d3Pct10D = "---";
        private string _d4Rng1D = "---", _d4Rng3D = "---", _d4Rng5D = "---", _d4Rng10D = "---";
        private string _d4Pct1D = "---", _d4Pct3D = "---", _d4Pct5D = "---", _d4Pct10D = "---";

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

        // Composite Profile Properties
        public string CompPOC1D { get => _compPOC1D; set { _compPOC1D = value; OnPropertyChanged(nameof(CompPOC1D)); } }
        public string CompPOC3D { get => _compPOC3D; set { _compPOC3D = value; OnPropertyChanged(nameof(CompPOC3D)); } }
        public string CompPOC5D { get => _compPOC5D; set { _compPOC5D = value; OnPropertyChanged(nameof(CompPOC5D)); } }
        public string CompPOC10D { get => _compPOC10D; set { _compPOC10D = value; OnPropertyChanged(nameof(CompPOC10D)); } }

        public string CompVAH1D { get => _compVAH1D; set { _compVAH1D = value; OnPropertyChanged(nameof(CompVAH1D)); } }
        public string CompVAH3D { get => _compVAH3D; set { _compVAH3D = value; OnPropertyChanged(nameof(CompVAH3D)); } }
        public string CompVAH5D { get => _compVAH5D; set { _compVAH5D = value; OnPropertyChanged(nameof(CompVAH5D)); } }
        public string CompVAH10D { get => _compVAH10D; set { _compVAH10D = value; OnPropertyChanged(nameof(CompVAH10D)); } }

        public string CompVAL1D { get => _compVAL1D; set { _compVAL1D = value; OnPropertyChanged(nameof(CompVAL1D)); } }
        public string CompVAL3D { get => _compVAL3D; set { _compVAL3D = value; OnPropertyChanged(nameof(CompVAL3D)); } }
        public string CompVAL5D { get => _compVAL5D; set { _compVAL5D = value; OnPropertyChanged(nameof(CompVAL5D)); } }
        public string CompVAL10D { get => _compVAL10D; set { _compVAL10D = value; OnPropertyChanged(nameof(CompVAL10D)); } }

        public string CompRng1D { get => _compRng1D; set { _compRng1D = value; OnPropertyChanged(nameof(CompRng1D)); } }
        public string CompRng3D { get => _compRng3D; set { _compRng3D = value; OnPropertyChanged(nameof(CompRng3D)); } }
        public string CompRng5D { get => _compRng5D; set { _compRng5D = value; OnPropertyChanged(nameof(CompRng5D)); } }
        public string CompRng10D { get => _compRng10D; set { _compRng10D = value; OnPropertyChanged(nameof(CompRng10D)); } }

        public string CVsAvg1D { get => _cVsAvg1D; set { _cVsAvg1D = value; OnPropertyChanged(nameof(CVsAvg1D)); } }
        public string CVsAvg3D { get => _cVsAvg3D; set { _cVsAvg3D = value; OnPropertyChanged(nameof(CVsAvg3D)); } }
        public string CVsAvg5D { get => _cVsAvg5D; set { _cVsAvg5D = value; OnPropertyChanged(nameof(CVsAvg5D)); } }
        public string CVsAvg10D { get => _cVsAvg10D; set { _cVsAvg10D = value; OnPropertyChanged(nameof(CVsAvg10D)); } }

        public string RollRng1D { get => _rollRng1D; set { _rollRng1D = value; OnPropertyChanged(nameof(RollRng1D)); } }
        public string RollRng3D { get => _rollRng3D; set { _rollRng3D = value; OnPropertyChanged(nameof(RollRng3D)); } }
        public string RollRng5D { get => _rollRng5D; set { _rollRng5D = value; OnPropertyChanged(nameof(RollRng5D)); } }
        public string RollRng10D { get => _rollRng10D; set { _rollRng10D = value; OnPropertyChanged(nameof(RollRng10D)); } }

        public string RVsAvg1D { get => _rVsAvg1D; set { _rVsAvg1D = value; OnPropertyChanged(nameof(RVsAvg1D)); } }
        public string RVsAvg3D { get => _rVsAvg3D; set { _rVsAvg3D = value; OnPropertyChanged(nameof(RVsAvg3D)); } }
        public string RVsAvg5D { get => _rVsAvg5D; set { _rVsAvg5D = value; OnPropertyChanged(nameof(RVsAvg5D)); } }
        public string RVsAvg10D { get => _rVsAvg10D; set { _rVsAvg10D = value; OnPropertyChanged(nameof(RVsAvg10D)); } }

        public string YearlyHigh { get => _yearlyHigh; set { _yearlyHigh = value; OnPropertyChanged(nameof(YearlyHigh)); } }
        public string YearlyLow { get => _yearlyLow; set { _yearlyLow = value; OnPropertyChanged(nameof(YearlyLow)); } }
        public string YearlyHighDate { get => _yearlyHighDate; set { _yearlyHighDate = value; OnPropertyChanged(nameof(YearlyHighDate)); } }
        public string YearlyLowDate { get => _yearlyLowDate; set { _yearlyLowDate = value; OnPropertyChanged(nameof(YearlyLowDate)); } }

        public string Control { get => _control; set { _control = value; OnPropertyChanged(nameof(Control)); } }
        public string Migration { get => _migration; set { _migration = value; OnPropertyChanged(nameof(Migration)); } }
        public string DailyBarCount { get => _dailyBarCount; set { _dailyBarCount = value; OnPropertyChanged(nameof(DailyBarCount)); } }

        // Prior EOD Properties (D-2, D-3, D-4)
        public string D2Rng1D { get => _d2Rng1D; set { _d2Rng1D = value; OnPropertyChanged(nameof(D2Rng1D)); } }
        public string D2Rng3D { get => _d2Rng3D; set { _d2Rng3D = value; OnPropertyChanged(nameof(D2Rng3D)); } }
        public string D2Rng5D { get => _d2Rng5D; set { _d2Rng5D = value; OnPropertyChanged(nameof(D2Rng5D)); } }
        public string D2Rng10D { get => _d2Rng10D; set { _d2Rng10D = value; OnPropertyChanged(nameof(D2Rng10D)); } }
        public string D2Pct1D { get => _d2Pct1D; set { _d2Pct1D = value; OnPropertyChanged(nameof(D2Pct1D)); } }
        public string D2Pct3D { get => _d2Pct3D; set { _d2Pct3D = value; OnPropertyChanged(nameof(D2Pct3D)); } }
        public string D2Pct5D { get => _d2Pct5D; set { _d2Pct5D = value; OnPropertyChanged(nameof(D2Pct5D)); } }
        public string D2Pct10D { get => _d2Pct10D; set { _d2Pct10D = value; OnPropertyChanged(nameof(D2Pct10D)); } }

        public string D3Rng1D { get => _d3Rng1D; set { _d3Rng1D = value; OnPropertyChanged(nameof(D3Rng1D)); } }
        public string D3Rng3D { get => _d3Rng3D; set { _d3Rng3D = value; OnPropertyChanged(nameof(D3Rng3D)); } }
        public string D3Rng5D { get => _d3Rng5D; set { _d3Rng5D = value; OnPropertyChanged(nameof(D3Rng5D)); } }
        public string D3Rng10D { get => _d3Rng10D; set { _d3Rng10D = value; OnPropertyChanged(nameof(D3Rng10D)); } }
        public string D3Pct1D { get => _d3Pct1D; set { _d3Pct1D = value; OnPropertyChanged(nameof(D3Pct1D)); } }
        public string D3Pct3D { get => _d3Pct3D; set { _d3Pct3D = value; OnPropertyChanged(nameof(D3Pct3D)); } }
        public string D3Pct5D { get => _d3Pct5D; set { _d3Pct5D = value; OnPropertyChanged(nameof(D3Pct5D)); } }
        public string D3Pct10D { get => _d3Pct10D; set { _d3Pct10D = value; OnPropertyChanged(nameof(D3Pct10D)); } }

        public string D4Rng1D { get => _d4Rng1D; set { _d4Rng1D = value; OnPropertyChanged(nameof(D4Rng1D)); } }
        public string D4Rng3D { get => _d4Rng3D; set { _d4Rng3D = value; OnPropertyChanged(nameof(D4Rng3D)); } }
        public string D4Rng5D { get => _d4Rng5D; set { _d4Rng5D = value; OnPropertyChanged(nameof(D4Rng5D)); } }
        public string D4Rng10D { get => _d4Rng10D; set { _d4Rng10D = value; OnPropertyChanged(nameof(D4Rng10D)); } }
        public string D4Pct1D { get => _d4Pct1D; set { _d4Pct1D = value; OnPropertyChanged(nameof(D4Pct1D)); } }
        public string D4Pct3D { get => _d4Pct3D; set { _d4Pct3D = value; OnPropertyChanged(nameof(D4Pct3D)); } }
        public string D4Pct5D { get => _d4Pct5D; set { _d4Pct5D = value; OnPropertyChanged(nameof(D4Pct5D)); } }
        public string D4Pct10D { get => _d4Pct10D; set { _d4Pct10D = value; OnPropertyChanged(nameof(D4Pct10D)); } }

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

            // Composite Profile Metrics
            var comp = metrics.Composite;
            if (comp != null && comp.IsValid)
            {
                vm.CompPOC1D = comp.POC_1D > 0 ? $"{comp.POC_1D:F0}" : "---";
                vm.CompPOC3D = comp.POC_3D > 0 ? $"{comp.POC_3D:F0}" : "---";
                vm.CompPOC5D = comp.POC_5D > 0 ? $"{comp.POC_5D:F0}" : "---";
                vm.CompPOC10D = comp.POC_10D > 0 ? $"{comp.POC_10D:F0}" : "---";

                vm.CompVAH1D = comp.VAH_1D > 0 ? $"{comp.VAH_1D:F0}" : "---";
                vm.CompVAH3D = comp.VAH_3D > 0 ? $"{comp.VAH_3D:F0}" : "---";
                vm.CompVAH5D = comp.VAH_5D > 0 ? $"{comp.VAH_5D:F0}" : "---";
                vm.CompVAH10D = comp.VAH_10D > 0 ? $"{comp.VAH_10D:F0}" : "---";

                vm.CompVAL1D = comp.VAL_1D > 0 ? $"{comp.VAL_1D:F0}" : "---";
                vm.CompVAL3D = comp.VAL_3D > 0 ? $"{comp.VAL_3D:F0}" : "---";
                vm.CompVAL5D = comp.VAL_5D > 0 ? $"{comp.VAL_5D:F0}" : "---";
                vm.CompVAL10D = comp.VAL_10D > 0 ? $"{comp.VAL_10D:F0}" : "---";

                vm.CompRng1D = comp.CompRange_1D > 0 ? $"{comp.CompRange_1D:F0}" : "---";
                vm.CompRng3D = comp.CompRange_3D > 0 ? $"{comp.CompRange_3D:F0}" : "---";
                vm.CompRng5D = comp.CompRange_5D > 0 ? $"{comp.CompRange_5D:F0}" : "---";
                vm.CompRng10D = comp.CompRange_10D > 0 ? $"{comp.CompRange_10D:F0}" : "---";

                vm.CVsAvg1D = comp.CVsAvg_1D > 0 ? $"{comp.CVsAvg_1D:F0}%" : "---";
                vm.CVsAvg3D = comp.CVsAvg_3D > 0 ? $"{comp.CVsAvg_3D:F0}%" : "---";
                vm.CVsAvg5D = comp.CVsAvg_5D > 0 ? $"{comp.CVsAvg_5D:F0}%" : "---";
                vm.CVsAvg10D = comp.CVsAvg_10D > 0 ? $"{comp.CVsAvg_10D:F0}%" : "---";

                vm.RollRng1D = comp.RollRange_1D > 0 ? $"{comp.RollRange_1D:F0}" : "---";
                vm.RollRng3D = comp.RollRange_3D > 0 ? $"{comp.RollRange_3D:F0}" : "---";
                vm.RollRng5D = comp.RollRange_5D > 0 ? $"{comp.RollRange_5D:F0}" : "---";
                vm.RollRng10D = comp.RollRange_10D > 0 ? $"{comp.RollRange_10D:F0}" : "---";

                vm.RVsAvg1D = comp.RVsAvg_1D > 0 ? $"{comp.RVsAvg_1D:F0}%" : "---";
                vm.RVsAvg3D = comp.RVsAvg_3D > 0 ? $"{comp.RVsAvg_3D:F0}%" : "---";
                vm.RVsAvg5D = comp.RVsAvg_5D > 0 ? $"{comp.RVsAvg_5D:F0}%" : "---";
                vm.RVsAvg10D = comp.RVsAvg_10D > 0 ? $"{comp.RVsAvg_10D:F0}%" : "---";

                vm.YearlyHigh = comp.YearlyExtremes?.YearlyHigh > 0 ? $"{comp.YearlyExtremes.YearlyHigh:F0}" : "---";
                vm.YearlyLow = comp.YearlyExtremes?.YearlyLow > 0 ? $"{comp.YearlyExtremes.YearlyLow:F0}" : "---";
                vm.YearlyHighDate = comp.YearlyExtremes?.YearlyHighDate != default ? comp.YearlyExtremes.YearlyHighDate.ToString("dd-MMM") : "---";
                vm.YearlyLowDate = comp.YearlyExtremes?.YearlyLowDate != default ? comp.YearlyExtremes.YearlyLowDate.ToString("dd-MMM") : "---";

                vm.Control = comp.Control ?? "---";
                vm.Migration = comp.Migration ?? "---";
                vm.DailyBarCount = comp.DailyBarCount.ToString();

                // Prior EOD D-2
                vm.D2Rng1D = comp.D2_1DRange > 0 ? $"{comp.D2_1DRange:F0}" : "---";
                vm.D2Rng3D = comp.D2_3DRange > 0 ? $"{comp.D2_3DRange:F0}" : "---";
                vm.D2Rng5D = comp.D2_5DRange > 0 ? $"{comp.D2_5DRange:F0}" : "---";
                vm.D2Rng10D = comp.D2_10DRange > 0 ? $"{comp.D2_10DRange:F0}" : "---";
                vm.D2Pct1D = comp.D2_1DVsAvg > 0 ? $"{comp.D2_1DVsAvg:F0}%" : "---";
                vm.D2Pct3D = comp.D2_3DVsAvg > 0 ? $"{comp.D2_3DVsAvg:F0}%" : "---";
                vm.D2Pct5D = comp.D2_5DVsAvg > 0 ? $"{comp.D2_5DVsAvg:F0}%" : "---";
                vm.D2Pct10D = comp.D2_10DVsAvg > 0 ? $"{comp.D2_10DVsAvg:F0}%" : "---";

                // Prior EOD D-3
                vm.D3Rng1D = comp.D3_1DRange > 0 ? $"{comp.D3_1DRange:F0}" : "---";
                vm.D3Rng3D = comp.D3_3DRange > 0 ? $"{comp.D3_3DRange:F0}" : "---";
                vm.D3Rng5D = comp.D3_5DRange > 0 ? $"{comp.D3_5DRange:F0}" : "---";
                vm.D3Rng10D = comp.D3_10DRange > 0 ? $"{comp.D3_10DRange:F0}" : "---";
                vm.D3Pct1D = comp.D3_1DVsAvg > 0 ? $"{comp.D3_1DVsAvg:F0}%" : "---";
                vm.D3Pct3D = comp.D3_3DVsAvg > 0 ? $"{comp.D3_3DVsAvg:F0}%" : "---";
                vm.D3Pct5D = comp.D3_5DVsAvg > 0 ? $"{comp.D3_5DVsAvg:F0}%" : "---";
                vm.D3Pct10D = comp.D3_10DVsAvg > 0 ? $"{comp.D3_10DVsAvg:F0}%" : "---";

                // Prior EOD D-4
                vm.D4Rng1D = comp.D4_1DRange > 0 ? $"{comp.D4_1DRange:F0}" : "---";
                vm.D4Rng3D = comp.D4_3DRange > 0 ? $"{comp.D4_3DRange:F0}" : "---";
                vm.D4Rng5D = comp.D4_5DRange > 0 ? $"{comp.D4_5DRange:F0}" : "---";
                vm.D4Rng10D = comp.D4_10DRange > 0 ? $"{comp.D4_10DRange:F0}" : "---";
                vm.D4Pct1D = comp.D4_1DVsAvg > 0 ? $"{comp.D4_1DVsAvg:F0}%" : "---";
                vm.D4Pct3D = comp.D4_3DVsAvg > 0 ? $"{comp.D4_3DVsAvg:F0}%" : "---";
                vm.D4Pct5D = comp.D4_5DVsAvg > 0 ? $"{comp.D4_5DVsAvg:F0}%" : "---";
                vm.D4Pct10D = comp.D4_10DVsAvg > 0 ? $"{comp.D4_10DVsAvg:F0}%" : "---";
            }

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
