using System;
using ZerodhaDatafeedAdapter.ViewModels;

namespace ZerodhaDatafeedAdapter.AddOns.OptionSignals.Models
{
    public class OptionSignalsRow : ViewModelBase
    {
        private double _strike;
        private string _ceSymbol;
        private string _peSymbol;
        private string _ceTickTime = "--:--:--";
        private string _peTickTime = "--:--:--";
        private string _ceLtp = "0.00";
        private string _peLtp = "0.00";
        private string _ceAtrTime = "--:--:--";
        private string _peAtrTime = "--:--:--";
        private string _ceAtrLtp = "0.00";
        private string _peAtrLtp = "0.00";
        private bool _isAtm;

        public double Strike
        {
            get => _strike;
            set { if (_strike != value) { _strike = value; OnPropertyChanged(); } }
        }

        public string CESymbol
        {
            get => _ceSymbol;
            set { if (_ceSymbol != value) { _ceSymbol = value; OnPropertyChanged(); } }
        }

        public string PESymbol
        {
            get => _peSymbol;
            set { if (_peSymbol != value) { _peSymbol = value; OnPropertyChanged(); } }
        }

        // CE Metrics
        public string CETickTime
        {
            get => _ceTickTime;
            set { if (_ceTickTime != value) { _ceTickTime = value; OnPropertyChanged(); } }
        }

        public string CELTP
        {
            get => _ceLtp;
            set { if (_ceLtp != value) { _ceLtp = value; OnPropertyChanged(); } }
        }

        public string CEAtrTime
        {
            get => _ceAtrTime;
            set { if (_ceAtrTime != value) { _ceAtrTime = value; OnPropertyChanged(); } }
        }

        public string CEAtrLTP
        {
            get => _ceAtrLtp;
            set { if (_ceAtrLtp != value) { _ceAtrLtp = value; OnPropertyChanged(); } }
        }

        // PE Metrics
        public string PETickTime
        {
            get => _peTickTime;
            set { if (_peTickTime != value) { _peTickTime = value; OnPropertyChanged(); } }
        }

        public string PELTP
        {
            get => _peLtp;
            set { if (_peLtp != value) { _peLtp = value; OnPropertyChanged(); } }
        }

        public string PEAtrTime
        {
            get => _peAtrTime;
            set { if (_peAtrTime != value) { _peAtrTime = value; OnPropertyChanged(); } }
        }

        public string PEAtrLTP
        {
            get => _peAtrLtp;
            set { if (_peAtrLtp != value) { _peAtrLtp = value; OnPropertyChanged(); } }
        }

        public bool IsATM
        {
            get => _isAtm;
            set { if (_isAtm != value) { _isAtm = value; OnPropertyChanged(); } }
        }
    }
}
