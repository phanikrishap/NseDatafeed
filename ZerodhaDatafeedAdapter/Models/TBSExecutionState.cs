using System;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Runtime.CompilerServices;

namespace ZerodhaDatafeedAdapter.Models
{
    /// <summary>
    /// Execution status for a TBS straddle position
    /// </summary>
    public enum TBSExecutionStatus
    {
        Skipped,        // Entry time has passed
        Idle,           // Entry time is more than 5 minutes away
        Monitoring,     // Within 5 minutes of entry time (preparing to enter)
        Live,           // Position entered and still open
        SquaredOff      // Was live, now all positions closed
    }

    /// <summary>
    /// Status for individual leg
    /// </summary>
    public enum TBSLegStatus
    {
        Pending,        // Not yet entered
        Active,         // Position open
        SLHit,          // Stop-loss triggered
        TargetHit,      // Target reached
        Exited          // Manually exited or time-based exit
    }

    /// <summary>
    /// Represents a single leg (CE or PE) of a straddle
    /// </summary>
    public class TBSLegState : INotifyPropertyChanged
    {
        public event PropertyChangedEventHandler PropertyChanged;

        public string OptionType { get; set; } // "CE" or "PE"

        private string _symbol;
        public string Symbol
        {
            get => _symbol;
            set { _symbol = value; OnPropertyChanged(); OnPropertyChanged(nameof(SymbolDisplay)); }
        }

        /// <summary>
        /// Display text for symbol (shows "ATM" if not yet determined)
        /// </summary>
        public string SymbolDisplay => string.IsNullOrEmpty(Symbol) ? "ATM" : Symbol;

        private decimal _entryPrice;
        public decimal EntryPrice
        {
            get => _entryPrice;
            set { _entryPrice = value; OnPropertyChanged(); OnPropertyChanged(nameof(EntryPriceDisplay)); UpdatePnL(); }
        }

        public string EntryPriceDisplay => EntryPrice > 0 ? EntryPrice.ToString("F2") : "-";

        private decimal _currentPrice;
        public decimal CurrentPrice
        {
            get => _currentPrice;
            set { _currentPrice = value; OnPropertyChanged(); OnPropertyChanged(nameof(CurrentPriceDisplay)); UpdatePnL(); }
        }

        public string CurrentPriceDisplay => CurrentPrice > 0 ? CurrentPrice.ToString("F2") : "-";

        private decimal _slPrice;
        public decimal SLPrice
        {
            get => _slPrice;
            set { _slPrice = value; OnPropertyChanged(); OnPropertyChanged(nameof(SLPriceDisplay)); OnPropertyChanged(nameof(SLStatus)); }
        }

        public string SLPriceDisplay => SLPrice > 0 ? SLPrice.ToString("F2") : "-";

        private decimal _targetPrice;
        public decimal TargetPrice
        {
            get => _targetPrice;
            set { _targetPrice = value; OnPropertyChanged(); OnPropertyChanged(nameof(TargetPriceDisplay)); }
        }

        public string TargetPriceDisplay => TargetPrice > 0 ? TargetPrice.ToString("F2") : "-";

        private decimal _exitPrice;
        public decimal ExitPrice
        {
            get => _exitPrice;
            set { _exitPrice = value; OnPropertyChanged(); OnPropertyChanged(nameof(ExitPriceDisplay)); }
        }

        public string ExitPriceDisplay => ExitPrice > 0 ? ExitPrice.ToString("F2") : "-";

        private DateTime? _exitTime;
        public DateTime? ExitTime
        {
            get => _exitTime;
            set { _exitTime = value; OnPropertyChanged(); OnPropertyChanged(nameof(ExitTimeDisplay)); }
        }

        public string ExitTimeDisplay => ExitTime.HasValue ? ExitTime.Value.ToString("HH:mm:ss") : "-";

        private string _exitReason;
        public string ExitReason
        {
            get => _exitReason;
            set { _exitReason = value; OnPropertyChanged(); }
        }

        private decimal _pnl;
        public decimal PnL
        {
            get => _pnl;
            set { _pnl = value; OnPropertyChanged(); OnPropertyChanged(nameof(PnLDisplay)); }
        }

        public string PnLDisplay => PnL != 0 ? PnL.ToString("F2") : "-";

        private TBSLegStatus _status = TBSLegStatus.Pending;
        public TBSLegStatus Status
        {
            get => _status;
            set { _status = value; OnPropertyChanged(); OnPropertyChanged(nameof(StatusText)); }
        }

        public string StatusText => Status.ToString();

        /// <summary>
        /// SL status - shows how close to SL (e.g., "Safe", "Warning", "Hit")
        /// </summary>
        public string SLStatus
        {
            get
            {
                if (Status == TBSLegStatus.SLHit) return "HIT";
                if (Status == TBSLegStatus.Exited) return "-";
                if (EntryPrice <= 0 || SLPrice <= 0 || CurrentPrice <= 0) return "-";

                // For short position: SL is above entry, warning if current > 80% of way to SL
                decimal slDistance = SLPrice - EntryPrice;
                decimal currentDistance = CurrentPrice - EntryPrice;

                if (currentDistance >= slDistance) return "HIT";
                if (slDistance > 0 && currentDistance >= slDistance * 0.8m) return "WARNING";
                return "SAFE";
            }
        }

        /// <summary>
        /// Number of lots from config
        /// </summary>
        public int Quantity { get; set; } = 1;

        /// <summary>
        /// Lot size from instrument mapping (e.g., 75 for NIFTY, 15 for BANKNIFTY)
        /// </summary>
        public int LotSize { get; set; } = 1;

        /// <summary>
        /// Total quantity = Quantity * LotSize
        /// </summary>
        public int TotalQuantity => Quantity * LotSize;

        #region Stoxxo Integration Fields

        /// <summary>
        /// Stoxxo leg ID from IB_GetUserLegs
        /// </summary>
        public int StoxxoLegID { get; set; }

        /// <summary>
        /// Stoxxo filled quantity
        /// </summary>
        private int _stoxxoQty;
        public int StoxxoQty
        {
            get => _stoxxoQty;
            set { _stoxxoQty = value; OnPropertyChanged(); }
        }

        /// <summary>
        /// Stoxxo average entry price
        /// </summary>
        private decimal _stoxxoEntryPrice;
        public decimal StoxxoEntryPrice
        {
            get => _stoxxoEntryPrice;
            set { _stoxxoEntryPrice = value; OnPropertyChanged(); OnPropertyChanged(nameof(StoxxoEntryPriceDisplay)); }
        }

        public string StoxxoEntryPriceDisplay => StoxxoEntryPrice > 0 ? StoxxoEntryPrice.ToString("F2") : "-";

        /// <summary>
        /// Stoxxo average exit price
        /// </summary>
        private decimal _stoxxoExitPrice;
        public decimal StoxxoExitPrice
        {
            get => _stoxxoExitPrice;
            set { _stoxxoExitPrice = value; OnPropertyChanged(); OnPropertyChanged(nameof(StoxxoExitPriceDisplay)); }
        }

        public string StoxxoExitPriceDisplay => StoxxoExitPrice > 0 ? StoxxoExitPrice.ToString("F2") : "-";

        /// <summary>
        /// Stoxxo leg status (e.g., "Active", "Completed")
        /// </summary>
        private string _stoxxoStatus;
        public string StoxxoStatus
        {
            get => _stoxxoStatus;
            set { _stoxxoStatus = value; OnPropertyChanged(); OnPropertyChanged(nameof(StoxxoStatusDisplay)); }
        }

        public string StoxxoStatusDisplay => string.IsNullOrEmpty(StoxxoStatus) ? "-" : StoxxoStatus;

        #endregion

        private void UpdatePnL()
        {
            if (EntryPrice > 0 && CurrentPrice > 0 && Status == TBSLegStatus.Active)
            {
                // For short straddle: profit when current < entry
                // P&L = (Entry - Current) * Lots * LotSize
                PnL = (EntryPrice - CurrentPrice) * Quantity * LotSize;
            }
            else if (Status == TBSLegStatus.Exited || Status == TBSLegStatus.SLHit)
            {
                // For exited positions, use exit price
                if (EntryPrice > 0 && ExitPrice > 0)
                {
                    PnL = (EntryPrice - ExitPrice) * Quantity * LotSize;
                }
            }
        }

        /// <summary>
        /// Recalculate P&L (call when lot size changes)
        /// </summary>
        public void RecalculatePnL()
        {
            UpdatePnL();
            OnPropertyChanged(nameof(PnL));
            OnPropertyChanged(nameof(PnLDisplay));
        }

        protected void OnPropertyChanged([CallerMemberName] string propertyName = null)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }
    }

    /// <summary>
    /// Represents the real-time execution state of a TBS straddle position (tranche)
    /// </summary>
    public class TBSExecutionState : INotifyPropertyChanged
    {
        public event PropertyChangedEventHandler PropertyChanged;

        /// <summary>
        /// Reference to the configuration this execution is based on
        /// </summary>
        public TBSConfigEntry Config { get; set; }

        /// <summary>
        /// Unique tranche identifier (auto-incremented)
        /// </summary>
        private int _trancheId;
        public int TrancheId
        {
            get => _trancheId;
            set { _trancheId = value; OnPropertyChanged(); }
        }

        #region Stoxxo Integration Fields

        /// <summary>
        /// Stoxxo portfolio name assigned after placing order (e.g., "NF_MULTILEG_1")
        /// </summary>
        private string _stoxxoPortfolioName;
        public string StoxxoPortfolioName
        {
            get => _stoxxoPortfolioName;
            set { _stoxxoPortfolioName = value; OnPropertyChanged(); OnPropertyChanged(nameof(StoxxoPortfolioNameDisplay)); }
        }

        public string StoxxoPortfolioNameDisplay => string.IsNullOrEmpty(StoxxoPortfolioName) ? "-" : StoxxoPortfolioName;

        /// <summary>
        /// Stoxxo portfolio status (e.g., "UnderExecution", "Completed")
        /// </summary>
        private string _stoxxoStatus;
        public string StoxxoStatus
        {
            get => _stoxxoStatus;
            set { _stoxxoStatus = value; OnPropertyChanged(); OnPropertyChanged(nameof(StoxxoStatusDisplay)); }
        }

        public string StoxxoStatusDisplay => string.IsNullOrEmpty(StoxxoStatus) ? "-" : StoxxoStatus;

        /// <summary>
        /// Stoxxo P&L from IB_PortfolioMTM
        /// </summary>
        private decimal _stoxxoPnL;
        public decimal StoxxoPnL
        {
            get => _stoxxoPnL;
            set { _stoxxoPnL = value; OnPropertyChanged(); OnPropertyChanged(nameof(StoxxoPnLDisplay)); }
        }

        public string StoxxoPnLDisplay => StoxxoPnL != 0 ? StoxxoPnL.ToString("F2") : "-";

        /// <summary>
        /// Whether Stoxxo order has been placed for this tranche
        /// </summary>
        public bool StoxxoOrderPlaced { get; set; } = false;

        /// <summary>
        /// Whether legs have been reconciled with Stoxxo after entry
        /// </summary>
        public bool StoxxoReconciled { get; set; } = false;

        /// <summary>
        /// Whether SL modification has been sent to Stoxxo
        /// </summary>
        public bool StoxxoSLModified { get; set; } = false;

        /// <summary>
        /// Whether exit has been called on Stoxxo
        /// </summary>
        public bool StoxxoExitCalled { get; set; } = false;

        #endregion

        /// <summary>
        /// Strike price of the straddle
        /// </summary>
        private decimal _strike;
        public decimal Strike
        {
            get => _strike;
            set { _strike = value; OnPropertyChanged(); OnPropertyChanged(nameof(StrikeDisplay)); }
        }

        /// <summary>
        /// Display string for strike
        /// </summary>
        public string StrikeDisplay => Strike > 0 ? Strike.ToString("F0") : "ATM";

        /// <summary>
        /// Whether the strike is locked (once Live, strike cannot change)
        /// </summary>
        public bool StrikeLocked { get; set; } = false;

        /// <summary>
        /// Whether SL-to-cost (hedge_to_cost) has been applied after one leg hit SL
        /// </summary>
        public bool SLToCostApplied { get; set; } = false;

        /// <summary>
        /// Cached hedge action from config (e.g., "hedge_to_cost", "exit_both")
        /// </summary>
        public string HedgeAction { get; set; }

        /// <summary>
        /// Lot size from instrument mapping (e.g., 75 for NIFTY, 15 for BANKNIFTY)
        /// </summary>
        public int LotSize { get; set; } = 1;

        /// <summary>
        /// Number of lots from config
        /// </summary>
        public int Quantity { get; set; } = 1;

        /// <summary>
        /// Cached individual SL percentage from config
        /// </summary>
        public decimal IndividualSLPercent { get; set; }

        /// <summary>
        /// Cached combined SL percentage from config
        /// </summary>
        public decimal CombinedSLPercent { get; set; }

        /// <summary>
        /// Cached target profit percentage from config (e.g., 0.25 for 25%)
        /// Only applies when both legs are still open
        /// </summary>
        public decimal TargetPercent { get; set; }

        /// <summary>
        /// Combined entry premium (CE entry + PE entry)
        /// Used to calculate target threshold
        /// </summary>
        public decimal CombinedEntryPremium { get; set; }

        /// <summary>
        /// Target profit threshold in absolute value
        /// = CombinedEntryPremium * TargetPercent * Quantity * LotSize
        /// </summary>
        public decimal TargetProfitThreshold { get; set; }

        /// <summary>
        /// Whether target has been hit and both legs exited
        /// </summary>
        public bool TargetHit { get; set; } = false;

        /// <summary>
        /// Whether deployment is conditional on prior tranches being profitable.
        /// Cached from config.
        /// </summary>
        public bool ProfitCondition { get; set; } = false;

        /// <summary>
        /// Whether this tranche was skipped due to profit condition not being met
        /// (prior tranches P&L was <= 0 at entry time)
        /// </summary>
        public bool SkippedDueToProfitCondition { get; set; } = false;

        /// <summary>
        /// Whether this tranche was missed (entry time passed before system started).
        /// When true, status updates are frozen - won't transition to Live.
        /// </summary>
        public bool IsMissed { get; set; } = false;

        /// <summary>
        /// Cached exit time from config
        /// </summary>
        public TimeSpan ConfigExitTime { get; set; }

        /// <summary>
        /// Collection of legs (CE and PE)
        /// </summary>
        public ObservableCollection<TBSLegState> Legs { get; set; } = new ObservableCollection<TBSLegState>();

        /// <summary>
        /// Whether the collapsible grid is expanded
        /// </summary>
        private bool _isExpanded = false;
        public bool IsExpanded
        {
            get => _isExpanded;
            set { _isExpanded = value; OnPropertyChanged(); }
        }

        private decimal _combinedPnL;
        /// <summary>
        /// Combined P&L for the straddle
        /// </summary>
        public decimal CombinedPnL
        {
            get => _combinedPnL;
            set { _combinedPnL = value; OnPropertyChanged(); }
        }

        private TBSExecutionStatus _status = TBSExecutionStatus.Idle;
        /// <summary>
        /// Current execution status
        /// </summary>
        public TBSExecutionStatus Status
        {
            get => _status;
            set
            {
                _status = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(StatusText));
            }
        }

        /// <summary>
        /// Human-readable status text
        /// </summary>
        public string StatusText => Status.ToString();

        /// <summary>
        /// Entry time from config (for display as headline)
        /// </summary>
        public string EntryTimeDisplay => Config?.EntryTime.ToString(@"hh\:mm\:ss") ?? "--:--:--";

        /// <summary>
        /// Time when position was actually entered
        /// </summary>
        public DateTime? ActualEntryTime { get; set; }

        /// <summary>
        /// Time when position was exited
        /// </summary>
        public DateTime? ExitTime { get; set; }

        /// <summary>
        /// Exit time display (HH:mm:ss format)
        /// </summary>
        public string ExitTimeDisplay => ExitTime.HasValue ? ExitTime.Value.ToString("HH:mm:ss") : "-";

        /// <summary>
        /// Combined entry price (CE + PE entry prices) for display
        /// </summary>
        public string EntryPriceDisplay
        {
            get
            {
                if (Legs == null || Legs.Count < 2) return "-";
                var ce = Legs.FirstOrDefault(l => l.OptionType == "CE");
                var pe = Legs.FirstOrDefault(l => l.OptionType == "PE");
                if (ce?.EntryPrice > 0 && pe?.EntryPrice > 0)
                    return $"{ce.EntryPrice:F0}+{pe.EntryPrice:F0}";
                return "-";
            }
        }

        /// <summary>
        /// Combined exit price (CE + PE exit prices) for display
        /// </summary>
        public string ExitPriceDisplay
        {
            get
            {
                if (Legs == null || Legs.Count < 2) return "-";
                var ce = Legs.FirstOrDefault(l => l.OptionType == "CE");
                var pe = Legs.FirstOrDefault(l => l.OptionType == "PE");
                if (ce?.ExitPrice > 0 || pe?.ExitPrice > 0)
                {
                    string ceExit = ce?.ExitPrice > 0 ? $"{ce.ExitPrice:F0}" : "-";
                    string peExit = pe?.ExitPrice > 0 ? $"{pe.ExitPrice:F0}" : "-";
                    return $"{ceExit}+{peExit}";
                }
                return "-";
            }
        }

        /// <summary>
        /// Additional notes/messages about the execution
        /// </summary>
        private string _message;
        public string Message
        {
            get => _message;
            set { _message = value; OnPropertyChanged(); }
        }

        /// <summary>
        /// Update status based on current time. Returns true if status changed.
        /// </summary>
        /// <param name="currentTime">Current time of day</param>
        /// <param name="skipExitTimeCheck">When true, skip exit time logic (used during simulation)</param>
        public bool UpdateStatusBasedOnTime(TimeSpan currentTime, bool skipExitTimeCheck = false)
        {
            if (Config == null) return false;

            // If tranche was marked as Missed during initialization, don't allow status changes
            // This prevents missed tranches from transitioning to Live via timer updates
            if (IsMissed)
                return false;

            // If tranche was skipped due to profit condition not met, don't allow status changes
            // This prevents skipped tranches from transitioning back to Monitoring/Live
            if (SkippedDueToProfitCondition)
                return false;

            var oldStatus = Status;
            var entryTime = Config.EntryTime;
            // Use cached ConfigExitTime if available, otherwise fall back to Config.ExitTime
            var exitTime = ConfigExitTime != TimeSpan.Zero ? ConfigExitTime : Config.ExitTime;
            var timeDiff = entryTime - currentTime;

            // Safety check: if exit time is zero or before/equal to entry time, use default (15:25:00)
            if (exitTime == TimeSpan.Zero || exitTime <= entryTime)
            {
                exitTime = new TimeSpan(15, 25, 0); // Default exit time
            }

            // If already SquaredOff, nothing to do
            if (Status == TBSExecutionStatus.SquaredOff)
                return false;

            // If Live, only check for exit time (don't change to anything else)
            // Skip exit time check during simulation mode
            if (Status == TBSExecutionStatus.Live)
            {
                // Only transition to SquaredOff if strike was locked (positions were populated)
                // and we're NOT in simulation mode (skipExitTimeCheck = false)
                if (!skipExitTimeCheck && StrikeLocked && currentTime >= exitTime)
                {
                    Status = TBSExecutionStatus.SquaredOff;
                    Message = "Position exited (time-based)";
                    ExitTime = DateTime.Today.Add(exitTime);
                }
                return oldStatus != Status;
            }

            if (currentTime > entryTime)
            {
                // Entry time has passed
                if (Status == TBSExecutionStatus.Monitoring)
                {
                    // Was monitoring, now entry time passed -> go Live
                    Status = TBSExecutionStatus.Live;
                    Message = "Position entered";
                    ActualEntryTime = DateTime.Today.Add(entryTime);
                }
                else if (Status == TBSExecutionStatus.Idle)
                {
                    // Was idle when entry passed -> go Live (late start scenario)
                    Status = TBSExecutionStatus.Live;
                    Message = "Position entered (late)";
                    ActualEntryTime = DateTime.Today.Add(entryTime);
                }
                // Skipped stays Skipped
            }
            else if (timeDiff.TotalMinutes <= 5 && timeDiff.TotalMinutes >= 0)
            {
                // Within 5 minutes of entry (and entry hasn't passed)
                Status = TBSExecutionStatus.Monitoring;
                Message = $"Entry in {timeDiff.Minutes}m {timeDiff.Seconds}s";
            }
            else if (timeDiff.TotalMinutes > 5)
            {
                // More than 5 minutes away
                Status = TBSExecutionStatus.Idle;
                Message = $"Entry at {entryTime:hh\\:mm\\:ss}";
            }

            return oldStatus != Status;
        }

        /// <summary>
        /// Update combined P&L from legs
        /// </summary>
        public void UpdateCombinedPnL()
        {
            decimal total = 0;
            foreach (var leg in Legs)
            {
                total += leg.PnL;
            }
            CombinedPnL = total;
        }

        /// <summary>
        /// Check if all legs are closed
        /// </summary>
        public bool AllLegsClosed()
        {
            foreach (var leg in Legs)
            {
                if (leg.Status == TBSLegStatus.Active)
                    return false;
            }
            return Legs.Count > 0;
        }

        protected void OnPropertyChanged([CallerMemberName] string propertyName = null)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }
    }
}
