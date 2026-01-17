using System;

namespace ZerodhaDatafeedAdapter.Models.Reactive
{
    /// <summary>
    /// Represents the current state of the simulation engine.
    /// Published via SimulationService.StateStream (BehaviorSubject).
    /// UI components subscribe to this stream for reactive updates.
    /// </summary>
    public class SimulationStateUpdate
    {
        /// <summary>
        /// Current simulation state (Idle, Loading, Ready, Playing, Paused, Completed, Error)
        /// </summary>
        public SimulationState State { get; set; }

        /// <summary>
        /// Human-readable status message
        /// </summary>
        public string StatusMessage { get; set; }

        /// <summary>
        /// Number of symbols successfully loaded
        /// </summary>
        public int LoadedSymbolCount { get; set; }

        /// <summary>
        /// Total number of symbols to load
        /// </summary>
        public int TotalSymbolCount { get; set; }

        /// <summary>
        /// Total tick count loaded
        /// </summary>
        public int TotalTickCount { get; set; }

        /// <summary>
        /// Number of prices injected during playback
        /// </summary>
        public int PricesInjectedCount { get; set; }

        /// <summary>
        /// Current simulation time during playback
        /// </summary>
        public DateTime CurrentSimTime { get; set; }

        /// <summary>
        /// Progress percentage (0-100) based on tick timeline position
        /// </summary>
        public double Progress { get; set; }

        /// <summary>
        /// Current playback speed multiplier
        /// </summary>
        public int SpeedMultiplier { get; set; }

        /// <summary>
        /// Timestamp when this state was published
        /// </summary>
        public DateTime Timestamp { get; set; }

        /// <summary>
        /// Display string for current simulation time
        /// </summary>
        public string CurrentSimTimeDisplay => CurrentSimTime != DateTime.MinValue
            ? CurrentSimTime.ToString("HH:mm:ss")
            : "--:--:--";

        /// <summary>
        /// True if simulation is in a running state (Playing)
        /// </summary>
        public bool IsPlaying => State == SimulationState.Playing;

        /// <summary>
        /// True if simulation can be started (Ready or Paused)
        /// </summary>
        public bool CanStart => State == SimulationState.Ready || State == SimulationState.Paused;

        /// <summary>
        /// True if simulation can be paused (Playing)
        /// </summary>
        public bool CanPause => State == SimulationState.Playing;

        /// <summary>
        /// True if simulation can be stopped (Playing or Paused)
        /// </summary>
        public bool CanStop => State == SimulationState.Playing || State == SimulationState.Paused;

        /// <summary>
        /// True if data is being loaded
        /// </summary>
        public bool IsLoading => State == SimulationState.Loading;

        /// <summary>
        /// Creates an idle state
        /// </summary>
        public static SimulationStateUpdate Idle => new SimulationStateUpdate
        {
            State = SimulationState.Idle,
            StatusMessage = "Idle",
            Timestamp = DateTime.Now
        };

        /// <summary>
        /// Creates a loading state with progress
        /// </summary>
        public static SimulationStateUpdate Loading(int loaded, int total, string message)
        {
            return new SimulationStateUpdate
            {
                State = SimulationState.Loading,
                StatusMessage = message,
                LoadedSymbolCount = loaded,
                TotalSymbolCount = total,
                Progress = total > 0 ? (loaded * 100.0 / total) : 0,
                Timestamp = DateTime.Now
            };
        }

        /// <summary>
        /// Creates a ready state
        /// </summary>
        public static SimulationStateUpdate Ready(int symbolCount, int tickCount)
        {
            return new SimulationStateUpdate
            {
                State = SimulationState.Ready,
                StatusMessage = $"Ready: {symbolCount} symbols, {tickCount:N0} ticks",
                LoadedSymbolCount = symbolCount,
                TotalSymbolCount = symbolCount,
                TotalTickCount = tickCount,
                Progress = 0,
                Timestamp = DateTime.Now
            };
        }

        /// <summary>
        /// Creates a playing state
        /// </summary>
        public static SimulationStateUpdate Playing(DateTime simTime, int injected, double progress, int speed)
        {
            return new SimulationStateUpdate
            {
                State = SimulationState.Playing,
                StatusMessage = $"Playing at {speed}x speed...",
                CurrentSimTime = simTime,
                PricesInjectedCount = injected,
                Progress = progress,
                SpeedMultiplier = speed,
                Timestamp = DateTime.Now
            };
        }

        /// <summary>
        /// Creates an error state
        /// </summary>
        public static SimulationStateUpdate Error(string message)
        {
            return new SimulationStateUpdate
            {
                State = SimulationState.Error,
                StatusMessage = message,
                Timestamp = DateTime.Now
            };
        }

        public override string ToString()
        {
            return $"[{State}] {StatusMessage} (Symbols: {LoadedSymbolCount}, Ticks: {TotalTickCount:N0})";
        }
    }
}
