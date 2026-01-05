using System;
using System.Collections.Generic;

namespace ZerodhaDatafeedAdapter.Models.Reactive
{
    /// <summary>
    /// Event data emitted when options are generated based on projected opens.
    /// Contains the list of option instruments to subscribe to.
    /// </summary>
    public class OptionsGeneratedEvent
    {
        /// <summary>
        /// List of generated option instruments (typically 122: 61 strikes x 2 types)
        /// </summary>
        public List<MappedInstrument> Options { get; set; }

        /// <summary>
        /// The selected underlying for option generation (NIFTY or SENSEX)
        /// </summary>
        public string SelectedUnderlying { get; set; }

        /// <summary>
        /// The expiry date of the generated options
        /// </summary>
        public DateTime SelectedExpiry { get; set; }

        /// <summary>
        /// Days to expiry (0DTE, 1DTE, etc.)
        /// </summary>
        public int DTE { get; set; }

        /// <summary>
        /// ATM strike price based on projected open
        /// </summary>
        public double ATMStrike { get; set; }

        /// <summary>
        /// Timestamp when options were generated
        /// </summary>
        public DateTime GeneratedAt { get; set; }

        /// <summary>
        /// The projected open price used for ATM calculation
        /// </summary>
        public double ProjectedOpenUsed { get; set; }

        public OptionsGeneratedEvent()
        {
            Options = new List<MappedInstrument>();
            GeneratedAt = DateTime.Now;
        }

        public override string ToString()
        {
            return $"Options Generated: {SelectedUnderlying} {SelectedExpiry:dd-MMM-yyyy} (DTE={DTE}), ATM={ATMStrike}, Count={Options?.Count ?? 0}";
        }
    }
}
