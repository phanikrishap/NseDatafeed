using System;

namespace ZerodhaDatafeedAdapter.Services.Trading
{
    /// <summary>
    /// Holds trading session context and configuration state.
    /// Extracted from TBSExecutionService for separation of concerns.
    /// </summary>
    public class TradingContext
    {
        private string _selectedUnderlying;
        private DateTime? _selectedExpiry;
        private decimal _atmStrike;
        private int _strikeStep;
        private bool _isMarketOpen;

        /// <summary>
        /// Currently selected underlying (e.g., NIFTY, BANKNIFTY).
        /// </summary>
        public string SelectedUnderlying
        {
            get => _selectedUnderlying;
            set
            {
                if (_selectedUnderlying != value)
                {
                    _selectedUnderlying = value;
                    UnderlyingChanged?.Invoke(this, value);
                }
            }
        }

        /// <summary>
        /// Currently selected expiry date.
        /// </summary>
        public DateTime? SelectedExpiry
        {
            get => _selectedExpiry;
            set
            {
                if (_selectedExpiry != value)
                {
                    _selectedExpiry = value;
                    ExpiryChanged?.Invoke(this, value);
                }
            }
        }

        /// <summary>
        /// Current ATM strike price.
        /// </summary>
        public decimal ATMStrike
        {
            get => _atmStrike;
            set
            {
                if (_atmStrike != value)
                {
                    _atmStrike = value;
                    ATMStrikeChanged?.Invoke(this, value);
                }
            }
        }

        /// <summary>
        /// Strike step size (e.g., 50, 100).
        /// </summary>
        public int StrikeStep
        {
            get => _strikeStep;
            set => _strikeStep = value;
        }

        /// <summary>
        /// Whether the market is currently open.
        /// </summary>
        public bool IsMarketOpen
        {
            get => _isMarketOpen;
            set
            {
                if (_isMarketOpen != value)
                {
                    _isMarketOpen = value;
                    MarketStatusChanged?.Invoke(this, value);
                }
            }
        }

        /// <summary>
        /// Event fired when the selected underlying changes.
        /// </summary>
        public event EventHandler<string> UnderlyingChanged;

        /// <summary>
        /// Event fired when the selected expiry changes.
        /// </summary>
        public event EventHandler<DateTime?> ExpiryChanged;

        /// <summary>
        /// Event fired when the ATM strike changes.
        /// </summary>
        public event EventHandler<decimal> ATMStrikeChanged;

        /// <summary>
        /// Event fired when the market status changes.
        /// </summary>
        public event EventHandler<bool> MarketStatusChanged;

        /// <summary>
        /// Sets the underlying.
        /// </summary>
        public void SetUnderlying(string underlying)
        {
            SelectedUnderlying = underlying;
        }

        /// <summary>
        /// Sets the expiry.
        /// </summary>
        public void SetExpiry(DateTime? expiry)
        {
            SelectedExpiry = expiry;
        }

        /// <summary>
        /// Sets the ATM strike.
        /// </summary>
        public void SetATMStrike(decimal strike)
        {
            ATMStrike = strike;
        }

        /// <summary>
        /// Updates market status.
        /// </summary>
        public void UpdateMarketStatus(bool isOpen)
        {
            IsMarketOpen = isOpen;
        }
    }
}
