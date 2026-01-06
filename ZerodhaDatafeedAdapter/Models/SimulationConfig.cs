using System;
using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace ZerodhaDatafeedAdapter.Models
{
    /// <summary>
    /// Configuration for a simulation session
    /// </summary>
    public class SimulationConfig : INotifyPropertyChanged
    {
        public event PropertyChangedEventHandler PropertyChanged;

        private DateTime _simulationDate = DateTime.Today;
        /// <summary>
        /// The date to load historical data from
        /// </summary>
        public DateTime SimulationDate
        {
            get => _simulationDate;
            set { _simulationDate = value; OnPropertyChanged(); }
        }

        private TimeSpan _timeFrom = new TimeSpan(9, 15, 0);
        /// <summary>
        /// Start time for simulation (e.g., 09:15:00)
        /// </summary>
        public TimeSpan TimeFrom
        {
            get => _timeFrom;
            set { _timeFrom = value; OnPropertyChanged(); }
        }

        private TimeSpan _timeTo = new TimeSpan(9, 30, 0);
        /// <summary>
        /// End time for simulation (e.g., 09:30:00)
        /// </summary>
        public TimeSpan TimeTo
        {
            get => _timeTo;
            set { _timeTo = value; OnPropertyChanged(); }
        }

        private string _underlying = "NIFTY";
        /// <summary>
        /// Underlying index (NIFTY or SENSEX)
        /// </summary>
        public string Underlying
        {
            get => _underlying;
            set { _underlying = value; OnPropertyChanged(); OnPropertyChanged(nameof(StepSize)); }
        }

        private DateTime _expiryDate = DateTime.Today;
        /// <summary>
        /// Expiry date for options
        /// </summary>
        public DateTime ExpiryDate
        {
            get => _expiryDate;
            set { _expiryDate = value; OnPropertyChanged(); }
        }

        private decimal _projectedOpen;
        /// <summary>
        /// Projected open price for ATM calculation
        /// </summary>
        public decimal ProjectedOpen
        {
            get => _projectedOpen;
            set { _projectedOpen = value; OnPropertyChanged(); OnPropertyChanged(nameof(ATMStrike)); }
        }

        private int _speedMultiplier = 1;
        /// <summary>
        /// Playback speed multiplier (1x, 2x, 5x, 10x)
        /// </summary>
        public int SpeedMultiplier
        {
            get => _speedMultiplier;
            set { _speedMultiplier = value > 0 ? value : 1; OnPropertyChanged(); }
        }

        private int _strikeCount = 5;
        /// <summary>
        /// Number of strikes above and below ATM to load (default 5, total = 2*N+1 = 11 strikes)
        /// </summary>
        public int StrikeCount
        {
            get => _strikeCount;
            set { _strikeCount = value > 0 ? value : 5; OnPropertyChanged(); OnPropertyChanged(nameof(TotalSymbolCount)); }
        }

        private int _stepSize = 50;
        /// <summary>
        /// Step size between strikes (e.g., 50 for NIFTY, 100 for SENSEX)
        /// </summary>
        public int StepSize
        {
            get => _stepSize;
            set { _stepSize = value > 0 ? value : 50; OnPropertyChanged(); OnPropertyChanged(nameof(ATMStrike)); }
        }

        private string _symbolPrefix = "";
        /// <summary>
        /// Prefix to prepend to option symbols for database lookup
        /// e.g., "NIFTY25DEC" becomes the prefix, and symbols become "NIFTY25DEC24000CE"
        /// </summary>
        public string SymbolPrefix
        {
            get => _symbolPrefix;
            set { _symbolPrefix = value ?? ""; OnPropertyChanged(); }
        }

        /// <summary>
        /// Calculated DTE (Days to Expiry) from SimulationDate to ExpiryDate.
        /// This represents what DTE would actually be on the simulated day.
        /// </summary>
        public int CalculatedDTE
        {
            get
            {
                int dte = (ExpiryDate.Date - SimulationDate.Date).Days;
                return dte < 0 ? 0 : dte;
            }
        }

        /// <summary>
        /// Total number of option symbols to load (CE + PE for each strike)
        /// </summary>
        public int TotalSymbolCount => (2 * StrikeCount + 1) * 2;

        /// <summary>
        /// Calculated ATM strike based on projected open and step size
        /// </summary>
        public decimal ATMStrike
        {
            get
            {
                if (ProjectedOpen <= 0 || StepSize <= 0) return 0;
                return Math.Round(ProjectedOpen / StepSize) * StepSize;
            }
        }

        /// <summary>
        /// Total duration of simulation in minutes
        /// </summary>
        public int TotalMinutes => (int)(TimeTo - TimeFrom).TotalMinutes;

        protected void OnPropertyChanged([CallerMemberName] string propertyName = null)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }
    }

    /// <summary>
    /// Represents a single historical bar (1-minute OHLCV)
    /// </summary>
    public class BarData
    {
        public DateTime Time { get; set; }
        public double Open { get; set; }
        public double High { get; set; }
        public double Low { get; set; }
        public double Close { get; set; }
        public long Volume { get; set; }

        public override string ToString()
        {
            return $"{Time:HH:mm} O={Open:F2} H={High:F2} L={Low:F2} C={Close:F2} V={Volume}";
        }
    }

    /// <summary>
    /// Represents a single tick (price at a specific timestamp)
    /// </summary>
    public class TickData
    {
        public DateTime Time { get; set; }
        public double Price { get; set; }
        public long Volume { get; set; }

        public override string ToString()
        {
            return $"{Time:HH:mm:ss.fff} P={Price:F2} V={Volume}";
        }
    }

    /// <summary>
    /// Simulation playback state
    /// </summary>
    public enum SimulationState
    {
        Idle,
        Loading,
        Ready,
        Playing,
        Paused,
        Completed,
        Error
    }
}
