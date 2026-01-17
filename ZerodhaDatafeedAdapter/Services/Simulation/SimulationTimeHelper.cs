using System;
using ZerodhaDatafeedAdapter.Services;

namespace ZerodhaDatafeedAdapter.Services.Simulation
{
    /// <summary>
    /// Centralized helper for getting current time based on simulation or real-time mode.
    /// Use this across all modules that need to be simulation-aware.
    /// </summary>
    public static class SimulationTimeHelper
    {
        /// <summary>
        /// Gets the current DateTime - simulation time if in simulation mode, otherwise real time.
        /// </summary>
        public static DateTime Now
        {
            get
            {
                var simService = SimulationService.Instance;
                if (simService.IsSimulationActive && simService.CurrentSimTime != DateTime.MinValue)
                {
                    return simService.CurrentSimTime;
                }
                return DateTime.Now;
            }
        }

        /// <summary>
        /// Gets the current TimeOfDay - simulation time if in simulation mode, otherwise real time.
        /// Most useful for TBS and other time-based triggers.
        /// </summary>
        public static TimeSpan TimeOfDay
        {
            get
            {
                var simService = SimulationService.Instance;
                if (simService.IsSimulationActive && simService.CurrentSimTime != DateTime.MinValue)
                {
                    return simService.CurrentSimTime.TimeOfDay;
                }
                return DateTime.Now.TimeOfDay;
            }
        }

        /// <summary>
        /// Gets today's date - simulation date if in simulation mode, otherwise real date.
        /// </summary>
        public static DateTime Today
        {
            get
            {
                var simService = SimulationService.Instance;
                if (simService.IsSimulationActive && simService.CurrentSimTime != DateTime.MinValue)
                {
                    return simService.CurrentSimTime.Date;
                }
                return DateTime.Today;
            }
        }

        /// <summary>
        /// Returns true if currently in simulation mode with active playback.
        /// </summary>
        public static bool IsSimulationActive => SimulationService.Instance.IsSimulationActive;

        /// <summary>
        /// Returns true if simulation mode is enabled (may or may not be actively playing).
        /// </summary>
        public static bool IsSimulationMode => SimulationService.Instance.IsSimulationMode;

        /// <summary>
        /// Gets elapsed time since a reference point, accounting for simulation mode.
        /// In simulation mode, this uses simulation time difference.
        /// </summary>
        /// <param name="referenceTime">The reference time to measure from</param>
        /// <returns>TimeSpan representing elapsed time</returns>
        public static TimeSpan GetElapsedSince(DateTime referenceTime)
        {
            return Now - referenceTime;
        }

        /// <summary>
        /// Formats current time for display (HH:mm:ss).
        /// </summary>
        public static string CurrentTimeDisplay => Now.ToString("HH:mm:ss");

        /// <summary>
        /// Formats current time for display with milliseconds (HH:mm:ss.fff).
        /// </summary>
        public static string CurrentTimeDisplayMs => Now.ToString("HH:mm:ss.fff");
    }
}
