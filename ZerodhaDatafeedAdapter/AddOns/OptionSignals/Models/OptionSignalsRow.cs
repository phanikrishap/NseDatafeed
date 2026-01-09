using System;
using ZerodhaDatafeedAdapter.ViewModels;

namespace ZerodhaDatafeedAdapter.AddOns.OptionSignals.Models
{
    /// <summary>
    /// HVN Trend direction - Bullish when HvnB > HvnS, Bearish when HvnS > HvnB
    /// </summary>
    public enum HvnTrend
    {
        Neutral,
        Bullish,
        Bearish
    }

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

        // Session HVN fields
        private string _ceHvnBSess = "0";
        private string _ceHvnSSess = "0";
        private string _peHvnBSess = "0";
        private string _peHvnSSess = "0";

        // Rolling HVN fields
        private string _ceHvnBRoll = "0";
        private string _ceHvnSRoll = "0";
        private string _peHvnBRoll = "0";
        private string _peHvnSRoll = "0";

        // Trend fields
        private HvnTrend _ceTrendSess = HvnTrend.Neutral;
        private HvnTrend _ceTrendRoll = HvnTrend.Neutral;
        private HvnTrend _peTrendSess = HvnTrend.Neutral;
        private HvnTrend _peTrendRoll = HvnTrend.Neutral;

        // Trend onset times
        private string _ceTrendSessTime = "--:--:--";
        private string _ceTrendRollTime = "--:--:--";
        private string _peTrendSessTime = "--:--:--";
        private string _peTrendRollTime = "--:--:--";

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

        // Session HVN Properties
        public string CEHvnBSess
        {
            get => _ceHvnBSess;
            set { if (_ceHvnBSess != value) { _ceHvnBSess = value; OnPropertyChanged(); } }
        }

        public string CEHvnSSess
        {
            get => _ceHvnSSess;
            set { if (_ceHvnSSess != value) { _ceHvnSSess = value; OnPropertyChanged(); } }
        }

        public string PEHvnBSess
        {
            get => _peHvnBSess;
            set { if (_peHvnBSess != value) { _peHvnBSess = value; OnPropertyChanged(); } }
        }

        public string PEHvnSSess
        {
            get => _peHvnSSess;
            set { if (_peHvnSSess != value) { _peHvnSSess = value; OnPropertyChanged(); } }
        }

        // Rolling HVN Properties
        public string CEHvnBRoll
        {
            get => _ceHvnBRoll;
            set { if (_ceHvnBRoll != value) { _ceHvnBRoll = value; OnPropertyChanged(); } }
        }

        public string CEHvnSRoll
        {
            get => _ceHvnSRoll;
            set { if (_ceHvnSRoll != value) { _ceHvnSRoll = value; OnPropertyChanged(); } }
        }

        public string PEHvnBRoll
        {
            get => _peHvnBRoll;
            set { if (_peHvnBRoll != value) { _peHvnBRoll = value; OnPropertyChanged(); } }
        }

        public string PEHvnSRoll
        {
            get => _peHvnSRoll;
            set { if (_peHvnSRoll != value) { _peHvnSRoll = value; OnPropertyChanged(); } }
        }

        // Trend Properties
        public HvnTrend CETrendSess
        {
            get => _ceTrendSess;
            set { if (_ceTrendSess != value) { _ceTrendSess = value; OnPropertyChanged(); } }
        }

        public HvnTrend CETrendRoll
        {
            get => _ceTrendRoll;
            set { if (_ceTrendRoll != value) { _ceTrendRoll = value; OnPropertyChanged(); } }
        }

        public HvnTrend PETrendSess
        {
            get => _peTrendSess;
            set { if (_peTrendSess != value) { _peTrendSess = value; OnPropertyChanged(); } }
        }

        public HvnTrend PETrendRoll
        {
            get => _peTrendRoll;
            set { if (_peTrendRoll != value) { _peTrendRoll = value; OnPropertyChanged(); } }
        }

        // Trend Time Properties
        public string CETrendSessTime
        {
            get => _ceTrendSessTime;
            set { if (_ceTrendSessTime != value) { _ceTrendSessTime = value; OnPropertyChanged(); } }
        }

        public string CETrendRollTime
        {
            get => _ceTrendRollTime;
            set { if (_ceTrendRollTime != value) { _ceTrendRollTime = value; OnPropertyChanged(); } }
        }

        public string PETrendSessTime
        {
            get => _peTrendSessTime;
            set { if (_peTrendSessTime != value) { _peTrendSessTime = value; OnPropertyChanged(); } }
        }

        public string PETrendRollTime
        {
            get => _peTrendRollTime;
            set { if (_peTrendRollTime != value) { _peTrendRollTime = value; OnPropertyChanged(); } }
        }

        // Legacy properties for backward compatibility (mapped to Session)
        public string CEHvnB
        {
            get => _ceHvnBSess;
            set => CEHvnBSess = value;
        }

        public string CEHvnS
        {
            get => _ceHvnSSess;
            set => CEHvnSSess = value;
        }

        public string PEHvnB
        {
            get => _peHvnBSess;
            set => PEHvnBSess = value;
        }

        public string PEHvnS
        {
            get => _peHvnSSess;
            set => PEHvnSSess = value;
        }
    }
}
