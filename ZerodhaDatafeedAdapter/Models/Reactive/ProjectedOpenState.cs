using System;

namespace ZerodhaDatafeedAdapter.Models.Reactive
{
    /// <summary>
    /// Represents the calculated projected open state for NIFTY and SENSEX.
    /// Calculated once when GIFT_NIFTY change% and prior closes are available.
    /// </summary>
    public class ProjectedOpenState
    {
        /// <summary>
        /// Projected opening price for NIFTY 50
        /// Formula: NiftyClose * (1 + GiftChangePercent/100)
        /// </summary>
        public double NiftyProjectedOpen { get; set; }

        /// <summary>
        /// Projected opening price for SENSEX
        /// Formula: SensexClose * (1 + GiftChangePercent/100)
        /// </summary>
        public double SensexProjectedOpen { get; set; }

        /// <summary>
        /// GIFT NIFTY change percentage used for calculation
        /// </summary>
        public double GiftChangePercent { get; set; }

        /// <summary>
        /// Indicates whether the projected opens have been calculated
        /// </summary>
        public bool IsComplete { get; set; }

        /// <summary>
        /// Timestamp when this calculation was performed
        /// </summary>
        public DateTime CalculatedAt { get; set; }

        /// <summary>
        /// The underlying that was selected for option generation (NIFTY or SENSEX)
        /// </summary>
        public string SelectedUnderlying { get; set; }

        /// <summary>
        /// Days to expiry for the selected underlying
        /// </summary>
        public int DTE { get; set; }

        /// <summary>
        /// Creates an empty/initial state
        /// </summary>
        public static ProjectedOpenState Empty => new ProjectedOpenState
        {
            IsComplete = false,
            CalculatedAt = DateTime.MinValue
        };

        public override string ToString()
        {
            if (!IsComplete)
                return "Projected Opens: Not yet calculated";

            return $"Projected Opens: NIFTY={NiftyProjectedOpen:F2}, SENSEX={SensexProjectedOpen:F2}, GiftChg={GiftChangePercent:F2}%";
        }
    }
}
