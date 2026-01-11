using System;
using ZerodhaDatafeedAdapter.Helpers;
using ZerodhaDatafeedAdapter.ViewModels;

namespace ZerodhaDatafeedAdapter.AddOns.OptionSignals.Models
{
    /// <summary>
    /// Signal status for tracking position lifecycle.
    /// </summary>
    public enum SignalStatus
    {
        Pending,     // Signal generated, waiting for entry
        Active,      // Position is open
        Closed,      // Position has been closed
        Cancelled    // Signal was cancelled before entry
    }

    /// <summary>
    /// Signal direction.
    /// </summary>
    public enum SignalDirection
    {
        Long,
        Short
    }

    /// <summary>
    /// Moneyness classification for options.
    /// </summary>
    public enum Moneyness
    {
        DeepITM,    // > 3 strikes ITM
        ITM2,       // 2-3 strikes ITM
        ITM1,       // 1 strike ITM
        ATM,        // At the money
        OTM1,       // 1 strike OTM
        OTM2,       // 2-3 strikes OTM
        DeepOTM     // > 3 strikes OTM
    }

    /// <summary>
    /// Represents a trading signal in the Signals grid.
    /// Contains signal details, position info, and P&L tracking.
    /// </summary>
    public class SignalRow : ViewModelBase
    {
        private string _signalId;
        private DateTime _signalTime;
        private string _symbol;
        private double _strike;
        private string _optionType; // CE or PE
        private Moneyness _moneyness;
        private SignalDirection _direction;
        private SignalStatus _status;
        private int _quantity;
        private double _entryPrice;
        private double _currentPrice;
        private double _exitPrice;
        private DateTime? _entryTime;
        private DateTime? _exitTime;
        private double _unrealizedPnL;
        private double _realizedPnL;
        private string _strategyName;
        private string _signalReason;
        private int _dte; // Days to expiry

        private bool _isRealtime; // True if signal was generated in real-time (within 5 seconds of system time)
        private bool _executionTriggered; // True if execution platform was notified

        // Bridge order tracking
        private int _bridgeOrderId; // Unique order ID sent to Stoxo bridge (SignalID parameter)
        private int _bridgeRequestId; // Request ID returned by bridge (>= 90000 = success)
        private string _bridgeError; // Error message if bridge call failed

        // Indicator values at signal time
        private int _sessHvnB;
        private int _sessHvnS;
        private int _rollHvnB;
        private int _rollHvnS;
        private double _cdMomentum;
        private double _cdSmooth;
        private double _priceMomentum;
        private double _priceSmooth;
        private int _vwapScoreSess;
        private int _vwapScoreRoll;

        public string SignalId
        {
            get => _signalId;
            set { if (_signalId != value) { _signalId = value; OnPropertyChanged(); } }
        }

        public DateTime SignalTime
        {
            get => _signalTime;
            set { if (_signalTime != value) { _signalTime = value; OnPropertyChanged(); OnPropertyChanged(nameof(SignalTimeStr)); } }
        }

        public string SignalTimeStr => _signalTime.ToString("dd-MMM HH:mm:ss");

        public string Symbol
        {
            get => _symbol;
            set { if (_symbol != value) { _symbol = value; OnPropertyChanged(); } }
        }

        public double Strike
        {
            get => _strike;
            set { if (_strike != value) { _strike = value; OnPropertyChanged(); } }
        }

        public string OptionType
        {
            get => _optionType;
            set { if (_optionType != value) { _optionType = value; OnPropertyChanged(); } }
        }

        public Moneyness Moneyness
        {
            get => _moneyness;
            set { if (_moneyness != value) { _moneyness = value; OnPropertyChanged(); OnPropertyChanged(nameof(MoneynessStr)); } }
        }

        public string MoneynessStr => _moneyness.ToString();

        public SignalDirection Direction
        {
            get => _direction;
            set { if (_direction != value) { _direction = value; OnPropertyChanged(); OnPropertyChanged(nameof(DirectionStr)); } }
        }

        public string DirectionStr => _direction == SignalDirection.Long ? "BUY" : "SELL";

        public SignalStatus Status
        {
            get => _status;
            set { if (_status != value) { _status = value; OnPropertyChanged(); OnPropertyChanged(nameof(StatusStr)); } }
        }

        public string StatusStr => _status.ToString();

        public int Quantity
        {
            get => _quantity;
            set { if (_quantity != value) { _quantity = value; OnPropertyChanged(); RecalculatePnL(); } }
        }

        public double EntryPrice
        {
            get => _entryPrice;
            set { if (_entryPrice != value) { _entryPrice = value; OnPropertyChanged(); OnPropertyChanged(nameof(EntryPriceStr)); RecalculatePnL(); } }
        }

        public string EntryPriceStr => _entryPrice > 0 ? _entryPrice.ToString("F2") : "--";

        public double CurrentPrice
        {
            get => _currentPrice;
            set { if (_currentPrice != value) { _currentPrice = value; OnPropertyChanged(); OnPropertyChanged(nameof(CurrentPriceStr)); RecalculatePnL(); } }
        }

        public string CurrentPriceStr => _currentPrice > 0 ? _currentPrice.ToString("F2") : "--";

        public double ExitPrice
        {
            get => _exitPrice;
            set { if (_exitPrice != value) { _exitPrice = value; OnPropertyChanged(); OnPropertyChanged(nameof(ExitPriceStr)); RecalculatePnL(); } }
        }

        public string ExitPriceStr => _exitPrice > 0 ? _exitPrice.ToString("F2") : "--";

        public DateTime? EntryTime
        {
            get => _entryTime;
            set { if (_entryTime != value) { _entryTime = value; OnPropertyChanged(); OnPropertyChanged(nameof(EntryTimeStr)); } }
        }

        public string EntryTimeStr => _entryTime?.ToString("HH:mm:ss") ?? "--";

        public DateTime? ExitTime
        {
            get => _exitTime;
            set { if (_exitTime != value) { _exitTime = value; OnPropertyChanged(); OnPropertyChanged(nameof(ExitTimeStr)); } }
        }

        public string ExitTimeStr => _exitTime?.ToString("HH:mm:ss") ?? "--";

        public double UnrealizedPnL
        {
            get => _unrealizedPnL;
            set { if (_unrealizedPnL != value) { _unrealizedPnL = value; OnPropertyChanged(); OnPropertyChanged(nameof(UnrealizedPnLStr)); } }
        }

        public string UnrealizedPnLStr => _unrealizedPnL.ToString("F2");

        public double RealizedPnL
        {
            get => _realizedPnL;
            set { if (_realizedPnL != value) { _realizedPnL = value; OnPropertyChanged(); OnPropertyChanged(nameof(RealizedPnLStr)); } }
        }

        public string RealizedPnLStr => _realizedPnL.ToString("F2");

        public string StrategyName
        {
            get => _strategyName;
            set { if (_strategyName != value) { _strategyName = value; OnPropertyChanged(); } }
        }

        public string SignalReason
        {
            get => _signalReason;
            set { if (_signalReason != value) { _signalReason = value; OnPropertyChanged(); } }
        }

        public int DTE
        {
            get => _dte;
            set { if (_dte != value) { _dte = value; OnPropertyChanged(); } }
        }

        // Indicator values at signal time
        public int SessHvnB
        {
            get => _sessHvnB;
            set { if (_sessHvnB != value) { _sessHvnB = value; OnPropertyChanged(); } }
        }

        public int SessHvnS
        {
            get => _sessHvnS;
            set { if (_sessHvnS != value) { _sessHvnS = value; OnPropertyChanged(); } }
        }

        public int RollHvnB
        {
            get => _rollHvnB;
            set { if (_rollHvnB != value) { _rollHvnB = value; OnPropertyChanged(); } }
        }

        public int RollHvnS
        {
            get => _rollHvnS;
            set { if (_rollHvnS != value) { _rollHvnS = value; OnPropertyChanged(); } }
        }

        public double CDMomentum
        {
            get => _cdMomentum;
            set { if (_cdMomentum != value) { _cdMomentum = value; OnPropertyChanged(); } }
        }

        public double CDSmooth
        {
            get => _cdSmooth;
            set { if (_cdSmooth != value) { _cdSmooth = value; OnPropertyChanged(); } }
        }

        public double PriceMomentum
        {
            get => _priceMomentum;
            set { if (_priceMomentum != value) { _priceMomentum = value; OnPropertyChanged(); } }
        }

        public double PriceSmooth
        {
            get => _priceSmooth;
            set { if (_priceSmooth != value) { _priceSmooth = value; OnPropertyChanged(); } }
        }

        public int VwapScoreSess
        {
            get => _vwapScoreSess;
            set { if (_vwapScoreSess != value) { _vwapScoreSess = value; OnPropertyChanged(); } }
        }

        public int VwapScoreRoll
        {
            get => _vwapScoreRoll;
            set { if (_vwapScoreRoll != value) { _vwapScoreRoll = value; OnPropertyChanged(); } }
        }

        /// <summary>
        /// True if signal was generated in real-time (within 5 seconds of system time).
        /// Only real-time signals trigger the execution platform.
        /// </summary>
        public bool IsRealtime
        {
            get => _isRealtime;
            set { if (_isRealtime != value) { _isRealtime = value; OnPropertyChanged(); } }
        }

        /// <summary>
        /// True if execution platform was notified for this signal.
        /// </summary>
        public bool ExecutionTriggered
        {
            get => _executionTriggered;
            set { if (_executionTriggered != value) { _executionTriggered = value; OnPropertyChanged(); } }
        }

        /// <summary>
        /// Unique order ID sent to Stoxo bridge (SignalID parameter).
        /// Generated for each signal to enable proper entry/exit matching.
        /// </summary>
        public int BridgeOrderId
        {
            get => _bridgeOrderId;
            set { if (_bridgeOrderId != value) { _bridgeOrderId = value; OnPropertyChanged(); OnPropertyChanged(nameof(BridgeOrderIdStr)); } }
        }

        public string BridgeOrderIdStr => _bridgeOrderId > 0 ? _bridgeOrderId.ToString() : "--";

        /// <summary>
        /// Request ID returned by the bridge. >= 90000 indicates success.
        /// Can be used for order modification or exit.
        /// </summary>
        public int BridgeRequestId
        {
            get => _bridgeRequestId;
            set { if (_bridgeRequestId != value) { _bridgeRequestId = value; OnPropertyChanged(); OnPropertyChanged(nameof(BridgeRequestIdStr)); } }
        }

        public string BridgeRequestIdStr => _bridgeRequestId > 0 ? _bridgeRequestId.ToString() : "--";

        /// <summary>
        /// Error message if bridge call failed.
        /// </summary>
        public string BridgeError
        {
            get => _bridgeError;
            set { if (_bridgeError != value) { _bridgeError = value; OnPropertyChanged(); } }
        }

        /// <summary>
        /// Indicates if the bridge order was successfully placed.
        /// </summary>
        public bool IsBridgeOrderPlaced => _bridgeRequestId >= 90000;

        private void RecalculatePnL()
        {
            // Get lot size from instrument masters via SymbolHelper (caches DB values)
            string underlying = _symbol != null && _symbol.StartsWith("SENSEX", StringComparison.OrdinalIgnoreCase) ? "SENSEX" : "NIFTY";
            int lotSize = SymbolHelper.GetLotSize(underlying);

            if (_status == SignalStatus.Active && _entryPrice > 0 && _currentPrice > 0)
            {
                // Calculate unrealized P&L
                double priceDiff = _direction == SignalDirection.Long
                    ? _currentPrice - _entryPrice
                    : _entryPrice - _currentPrice;

                UnrealizedPnL = priceDiff * _quantity * lotSize;
            }
            else if (_status == SignalStatus.Closed && _entryPrice > 0 && _exitPrice > 0)
            {
                // Calculate realized P&L
                double priceDiff = _direction == SignalDirection.Long
                    ? _exitPrice - _entryPrice
                    : _entryPrice - _exitPrice;

                RealizedPnL = priceDiff * _quantity * lotSize;
                UnrealizedPnL = 0;
            }
        }

        /// <summary>
        /// Creates a unique signal ID based on strategy, symbol, and time.
        /// </summary>
        public static string GenerateSignalId(string strategy, string symbol, DateTime time)
        {
            return $"{strategy}_{symbol}_{time:HHmmss}";
        }
    }
}
