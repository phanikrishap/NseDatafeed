using System;

namespace ZerodhaDatafeedAdapter.Services.Signals
{
    /// <summary>
    /// Holds signal generation context and configuration state.
    /// Extracted from SignalsOrchestrator for separation of concerns.
    /// </summary>
    public class SignalContext
    {
        private double _atmStrike;
        private int _strikeStep = 50;
        private DateTime? _selectedExpiry;
        private string _selectedUnderlying = "NIFTY";

        /// <summary>
        /// Current ATM strike for signal generation.
        /// </summary>
        public double ATMStrike
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
        /// Selected expiry for signal generation.
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
        /// Selected underlying (e.g., NIFTY, BANKNIFTY).
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
        /// Event fired when ATM strike changes.
        /// </summary>
        public event EventHandler<double> ATMStrikeChanged;

        /// <summary>
        /// Event fired when expiry changes.
        /// </summary>
        public event EventHandler<DateTime?> ExpiryChanged;

        /// <summary>
        /// Event fired when underlying changes.
        /// </summary>
        public event EventHandler<string> UnderlyingChanged;

        /// <summary>
        /// Checks if the configuration is valid for signal generation.
        /// </summary>
        public bool IsConfigurationValid()
        {
            return _selectedExpiry.HasValue
                && !string.IsNullOrEmpty(_selectedUnderlying)
                && _atmStrike > 0;
        }

        /// <summary>
        /// Sets market context for signal evaluation.
        /// </summary>
        public void SetMarketContext(double atmStrike, int strikeStep, DateTime? selectedExpiry, string underlying = null)
        {
            ATMStrike = atmStrike;
            StrikeStep = strikeStep;
            SelectedExpiry = selectedExpiry;
            if (!string.IsNullOrEmpty(underlying))
            {
                SelectedUnderlying = underlying;
            }
        }
    }
}
