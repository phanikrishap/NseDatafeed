using System;

namespace ZerodhaDatafeedAdapter.Models
{
    /// <summary>
    /// Represents a Time-Based Straddle configuration entry from tbsConfig.xlsx
    /// </summary>
    public class TBSConfigEntry
    {
        /// <summary>
        /// Underlying symbol (e.g., "NIFTY", "BANKNIFTY")
        /// </summary>
        public string Underlying { get; set; }

        /// <summary>
        /// Days to expiry filter for selecting the expiry
        /// </summary>
        public int DTE { get; set; }

        /// <summary>
        /// Time of day to enter the straddle position
        /// </summary>
        public TimeSpan EntryTime { get; set; }

        /// <summary>
        /// Time of day to exit the straddle position
        /// </summary>
        public TimeSpan ExitTime { get; set; }

        /// <summary>
        /// Individual leg stop-loss percentage (e.g., 0.50 = 50%)
        /// </summary>
        public decimal IndividualSL { get; set; }

        /// <summary>
        /// Combined straddle stop-loss percentage
        /// </summary>
        public decimal CombinedSL { get; set; }

        /// <summary>
        /// Target profit percentage for combined premium (e.g., 0.25 = 25%)
        /// Only triggers exit when BOTH legs are still open
        /// </summary>
        public decimal TargetPercent { get; set; }

        /// <summary>
        /// Action to take when individual SL is hit (e.g., "hedge_to_cost", "exit_both")
        /// </summary>
        public string HedgeAction { get; set; }

        /// <summary>
        /// Quantity/lot size for the position
        /// </summary>
        public int Quantity { get; set; }

        /// <summary>
        /// Whether this config is active/enabled
        /// </summary>
        public bool IsActive { get; set; } = true;

        /// <summary>
        /// Whether deployment is conditional on prior tranches being profitable.
        /// If TRUE: Only deploy if cumulative P&L of all prior deployed tranches > 0
        /// First tranche always deploys regardless of this setting.
        /// </summary>
        public bool ProfitCondition { get; set; } = false;

        public override string ToString()
        {
            return $"{Underlying} DTE={DTE} Entry={EntryTime:hh\\:mm} Exit={ExitTime:hh\\:mm} IndSL={IndividualSL:P0} CombSL={CombinedSL:P0} Tgt={TargetPercent:P0} Action={HedgeAction} Qty={Quantity}";
        }
    }
}
