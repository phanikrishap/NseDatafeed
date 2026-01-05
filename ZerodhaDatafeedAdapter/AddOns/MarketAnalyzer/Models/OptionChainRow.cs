using System;
using System.ComponentModel;
using ZerodhaDatafeedAdapter.Models;

namespace ZerodhaDatafeedAdapter.AddOns.MarketAnalyzer.Models
{
    /// <summary>
    /// Row item for the Option Chain - represents a single strike with CE and PE data
    /// Implements INotifyPropertyChanged for granular UI updates without full refresh
    /// </summary>
    public class OptionChainRow : INotifyPropertyChanged
    {
        public event PropertyChangedEventHandler PropertyChanged;
        protected void OnPropertyChanged(string name) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

        public double Strike { get; set; }
        public string StrikeDisplay => Strike.ToString("F0");

        // CE (Call) data
        private string _ceLast = "---";
        public string CELast { get => _ceLast; set { if (_ceLast != value) { _ceLast = value; OnPropertyChanged(nameof(CELast)); } } }

        private string _ceStatus = "---";
        public string CEStatus { get => _ceStatus; set { if (_ceStatus != value) { _ceStatus = value; OnPropertyChanged(nameof(CEStatus)); } } }

        private double _cePrice;
        public double CEPrice { get => _cePrice; set { if (_cePrice != value) { _cePrice = value; OnPropertyChanged(nameof(CEPrice)); OnPropertyChanged(nameof(CEVWAPComparison)); NotifyStraddleChanged(); } } }

        public string CESymbol { get; set; }

        // PE (Put) data
        private string _peLast = "---";
        public string PELast { get => _peLast; set { if (_peLast != value) { _peLast = value; OnPropertyChanged(nameof(PELast)); } } }

        private string _peStatus = "---";
        public string PEStatus { get => _peStatus; set { if (_peStatus != value) { _peStatus = value; OnPropertyChanged(nameof(PEStatus)); } } }

        private double _pePrice;
        public double PEPrice { get => _pePrice; set { if (_pePrice != value) { _pePrice = value; OnPropertyChanged(nameof(PEPrice)); OnPropertyChanged(nameof(PEVWAPComparison)); NotifyStraddleChanged(); } } }

        public string PESymbol { get; set; }

        // Update times from websocket
        private string _ceUpdateTime = "---";
        public string CEUpdateTime { get => _ceUpdateTime; set { if (_ceUpdateTime != value) { _ceUpdateTime = value; OnPropertyChanged(nameof(CEUpdateTime)); } } }

        private string _peUpdateTime = "---";
        public string PEUpdateTime { get => _peUpdateTime; set { if (_peUpdateTime != value) { _peUpdateTime = value; OnPropertyChanged(nameof(PEUpdateTime)); } } }

        // Synthetic Straddle price from SyntheticStraddleService (live calculated)
        private double _syntheticStraddlePrice;
        public double SyntheticStraddlePrice { get => _syntheticStraddlePrice; set { if (_syntheticStraddlePrice != value) { _syntheticStraddlePrice = value; OnPropertyChanged(nameof(SyntheticStraddlePrice)); OnPropertyChanged(nameof(StraddleVWAPComparison)); NotifyStraddleChanged(); } } }

        private void NotifyStraddleChanged()
        {
            OnPropertyChanged(nameof(StraddlePrice));
            OnPropertyChanged(nameof(StraddleValue));
        }

        // Straddle = Synthetic straddle price (if available) or CE + PE fallback
        public string StraddlePrice => SyntheticStraddlePrice > 0
            ? SyntheticStraddlePrice.ToString("F2")
            : (CEPrice > 0 && PEPrice > 0)
                ? (CEPrice + PEPrice).ToString("F2")
                : "---";

        public double StraddleValue => SyntheticStraddlePrice > 0
            ? SyntheticStraddlePrice
            : (CEPrice > 0 && PEPrice > 0) ? CEPrice + PEPrice : double.MaxValue;

        // Straddle symbol for the synthetic instrument (e.g., NIFTY25DEC24000_STRDL)
        public string StraddleSymbol { get; set; }

        private bool _isATM;
        public bool IsATM { get => _isATM; set { if (_isATM != value) { _isATM = value; OnPropertyChanged(nameof(IsATM)); } } }

        // Histogram width percentage (0-100) for visual representation
        private double _ceHistogramWidth;
        public double CEHistogramWidth { get => _ceHistogramWidth; set { if (_ceHistogramWidth != value) { _ceHistogramWidth = value; OnPropertyChanged(nameof(CEHistogramWidth)); } } }

        private double _peHistogramWidth;
        public double PEHistogramWidth { get => _peHistogramWidth; set { if (_peHistogramWidth != value) { _peHistogramWidth = value; OnPropertyChanged(nameof(PEHistogramWidth)); } } }

        // VWAP data for CE
        private double _ceVWAP;
        public double CEVWAP
        {
            get => _ceVWAP;
            set
            {
                if (_ceVWAP != value)
                {
                    _ceVWAP = value;
                    OnPropertyChanged(nameof(CEVWAP));
                    OnPropertyChanged(nameof(CEVWAPDisplay));
                    OnPropertyChanged(nameof(CEVWAPComparison));
                }
            }
        }
        public string CEVWAPDisplay => _ceVWAP > 0 ? _ceVWAP.ToString("F2") : "---";
        // Returns: 1 if price > VWAP, -1 if price < VWAP, 0 if no data
        public int CEVWAPComparison => (_ceVWAP > 0 && _cePrice > 0) ? (_cePrice > _ceVWAP ? 1 : (_cePrice < _ceVWAP ? -1 : 0)) : 0;

        private int _ceVWAPPosition;  // -2, -1, 0, +1, +2 relative to VWAP bands
        public int CEVWAPPosition { get => _ceVWAPPosition; set { if (_ceVWAPPosition != value) { _ceVWAPPosition = value; OnPropertyChanged(nameof(CEVWAPPosition)); OnPropertyChanged(nameof(CEVWAPPositionDisplay)); } } }
        public string CEVWAPPositionDisplay => GetVWAPPositionText(_ceVWAPPosition, _cePrice, _ceVWAP);

        // VWAP data for PE
        private double _peVWAP;
        public double PEVWAP
        {
            get => _peVWAP;
            set
            {
                if (_peVWAP != value)
                {
                    _peVWAP = value;
                    OnPropertyChanged(nameof(PEVWAP));
                    OnPropertyChanged(nameof(PEVWAPDisplay));
                    OnPropertyChanged(nameof(PEVWAPComparison));
                }
            }
        }
        public string PEVWAPDisplay => _peVWAP > 0 ? _peVWAP.ToString("F2") : "---";
        // Returns: 1 if price > VWAP, -1 if price < VWAP, 0 if no data
        public int PEVWAPComparison => (_peVWAP > 0 && _pePrice > 0) ? (_pePrice > _peVWAP ? 1 : (_pePrice < _peVWAP ? -1 : 0)) : 0;

        private int _peVWAPPosition;
        public int PEVWAPPosition { get => _peVWAPPosition; set { if (_peVWAPPosition != value) { _peVWAPPosition = value; OnPropertyChanged(nameof(PEVWAPPosition)); OnPropertyChanged(nameof(PEVWAPPositionDisplay)); } } }
        public string PEVWAPPositionDisplay => GetVWAPPositionText(_peVWAPPosition, _pePrice, _peVWAP);

        // VWAP data for Straddle (synthetic instrument)
        private double _straddleVWAP;
        public double StraddleVWAP
        {
            get => _straddleVWAP;
            set
            {
                if (_straddleVWAP != value)
                {
                    _straddleVWAP = value;
                    OnPropertyChanged(nameof(StraddleVWAP));
                    OnPropertyChanged(nameof(StraddleVWAPDisplay));
                    OnPropertyChanged(nameof(StraddleVWAPComparison));
                }
            }
        }
        public string StraddleVWAPDisplay => _straddleVWAP > 0 ? _straddleVWAP.ToString("F2") : "---";
        // Returns: 1 if straddle price > VWAP, -1 if price < VWAP, 0 if no data
        public int StraddleVWAPComparison => (_straddleVWAP > 0 && StraddleValue < double.MaxValue) ? (StraddleValue > _straddleVWAP ? 1 : (StraddleValue < _straddleVWAP ? -1 : 0)) : 0;

        private static string GetVWAPPositionText(int position, double price, double vwap)
        {
            if (vwap <= 0 || price <= 0) return "---";
            double pct = ((price - vwap) / vwap) * 100;
            string sign = pct >= 0 ? "+" : "";
            switch (position)
            {
                case 2: return $"{sign}{pct:F1}% (>+2SD)";
                case 1: return $"{sign}{pct:F1}% (+1SD)";
                case -1: return $"{sign}{pct:F1}% (-1SD)";
                case -2: return $"{sign}{pct:F1}% (<-2SD)";
                default: return $"{sign}{pct:F1}%";
            }
        }
    }
}
