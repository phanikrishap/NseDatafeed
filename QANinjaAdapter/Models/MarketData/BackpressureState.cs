namespace QANinjaAdapter.Models.MarketData
{
    /// <summary>
    /// Backpressure management states for intelligent queue control.
    /// Used by tick processors to manage queue depth and prevent memory exhaustion.
    /// </summary>
    public enum BackpressureState
    {
        /// <summary>Normal operation - all ticks accepted</summary>
        Normal = 0,

        /// <summary>Warning level - selective dropping of low-priority symbols</summary>
        Warning = 1,

        /// <summary>Critical level - aggressive dropping and oldest tick removal</summary>
        Critical = 2,

        /// <summary>Emergency level - only essential symbols accepted</summary>
        Emergency = 3,

        /// <summary>Maximum capacity - reject all new ticks</summary>
        Maximum = 4
    }
}
