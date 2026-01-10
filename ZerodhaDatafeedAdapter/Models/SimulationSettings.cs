using System;
using Newtonsoft.Json;

namespace ZerodhaDatafeedAdapter.Models
{
    /// <summary>
    /// Simulation settings loaded from config.json.
    /// When Enabled is true, the adapter starts in simulation mode:
    /// - Skips live data connections (WebSocket, historical tick downloads)
    /// - Auto-opens the Simulation Engine window
    /// - Populates UI with these config values
    /// </summary>
    public class SimulationSettings
    {
        /// <summary>
        /// When true, adapter starts in simulation mode and skips live data routines
        /// </summary>
        [JsonProperty("Enabled")]
        public bool Enabled { get; set; } = false;

        /// <summary>
        /// The date to simulate (historical data date)
        /// </summary>
        [JsonProperty("SimulationDate")]
        public DateTime SimulationDate { get; set; } = DateTime.Today.AddDays(-1);

        /// <summary>
        /// Underlying index: "NIFTY" or "SENSEX"
        /// </summary>
        [JsonProperty("Underlying")]
        public string Underlying { get; set; } = "NIFTY";

        /// <summary>
        /// Expiry date for options
        /// </summary>
        [JsonProperty("ExpiryDate")]
        public DateTime ExpiryDate { get; set; } = DateTime.Today;

        /// <summary>
        /// Projected open price for ATM calculation
        /// </summary>
        [JsonProperty("ProjectedOpen")]
        public decimal ProjectedOpen { get; set; } = 24000m;

        /// <summary>
        /// Step size between strikes (50 for NIFTY, 100 for SENSEX)
        /// </summary>
        [JsonProperty("StepSize")]
        public int StepSize { get; set; } = 50;

        /// <summary>
        /// Number of strikes above and below ATM to load
        /// </summary>
        [JsonProperty("StrikeCount")]
        public int StrikeCount { get; set; } = 5;

        /// <summary>
        /// Start time for simulation (format: "HH:mm")
        /// </summary>
        [JsonProperty("TimeFrom")]
        public string TimeFrom { get; set; } = "09:15";

        /// <summary>
        /// End time for simulation (format: "HH:mm")
        /// </summary>
        [JsonProperty("TimeTo")]
        public string TimeTo { get; set; } = "15:30";

        /// <summary>
        /// Symbol prefix for database lookup (e.g., "NIFTY25JAN")
        /// </summary>
        [JsonProperty("SymbolPrefix")]
        public string SymbolPrefix { get; set; } = "";

        /// <summary>
        /// Playback speed multiplier (1, 2, 5, 10)
        /// </summary>
        [JsonProperty("SpeedMultiplier")]
        public int SpeedMultiplier { get; set; } = 1;

        /// <summary>
        /// Whether to auto-start playback after loading data
        /// </summary>
        [JsonProperty("AutoStart")]
        public bool AutoStart { get; set; } = false;

        /// <summary>
        /// Parses TimeFrom string to TimeSpan
        /// </summary>
        public TimeSpan GetTimeFrom()
        {
            if (TimeSpan.TryParse(TimeFrom, out var result))
                return result;
            return new TimeSpan(9, 15, 0);
        }

        /// <summary>
        /// Parses TimeTo string to TimeSpan
        /// </summary>
        public TimeSpan GetTimeTo()
        {
            if (TimeSpan.TryParse(TimeTo, out var result))
                return result;
            return new TimeSpan(15, 30, 0);
        }

        /// <summary>
        /// Converts settings to a SimulationConfig for the SimulationService
        /// </summary>
        public SimulationConfig ToSimulationConfig()
        {
            return new SimulationConfig
            {
                SimulationDate = SimulationDate,
                Underlying = Underlying,
                ExpiryDate = ExpiryDate,
                ProjectedOpen = ProjectedOpen,
                StepSize = StepSize,
                StrikeCount = StrikeCount,
                TimeFrom = GetTimeFrom(),
                TimeTo = GetTimeTo(),
                SymbolPrefix = SymbolPrefix,
                SpeedMultiplier = SpeedMultiplier
            };
        }
    }
}
