using System;

namespace ZerodhaDatafeedAdapter.Models.Reactive
{
    /// <summary>
    /// Represents a status update for an option subscription workflow.
    /// Tracks progress through: Pending → Cached → Done
    /// </summary>
    public class OptionStatusUpdate
    {
        /// <summary>
        /// Option symbol
        /// </summary>
        public string Symbol { get; set; }

        /// <summary>
        /// Status message (e.g., "Pending", "Cached (123)", "Done (456)", "Error: ...")
        /// </summary>
        public string Status { get; set; }

        /// <summary>
        /// Timestamp when this status was set
        /// </summary>
        public DateTime Timestamp { get; set; }

        /// <summary>
        /// Whether the status indicates completion
        /// </summary>
        public bool IsComplete => Status?.StartsWith("Done") == true;

        /// <summary>
        /// Whether the status indicates an error
        /// </summary>
        public bool IsError => Status?.StartsWith("Error") == true;

        public override string ToString()
        {
            return $"{Symbol}: {Status}";
        }
    }
}
