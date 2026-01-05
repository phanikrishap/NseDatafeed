using System;
using System.Threading.Tasks;
using ZerodhaDatafeedAdapter.Logging;

namespace ZerodhaDatafeedAdapter.Helpers
{
    /// <summary>
    /// Extensions for Task to provide safer async execution patterns.
    /// </summary>
    public static class TaskExtensions
    {
        private static readonly ILoggerService _log = LoggerFactory.GetLogger(LogDomain.Main);

        /// <summary>
        /// Safely executes a Task in a fire-and-forget manner.
        /// Catches and logs any exceptions to prevent application crashes (crucial for .NET Framework).
        /// </summary>
        /// <param name="task">The task to execute</param>
        /// <param name="context">Context description for logging (e.g., "Connect", "GenerateOptions")</param>
        public static async void SafeFireAndForget(this Task task, string context = "AsyncOperation")
        {
            try
            {
                await task.ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _log.Error($"[SafeFireAndForget] CRITICAL ERROR in '{context}': {ex.Message}", ex);
                
                // Optional: In a trading app, you might want to notify the UI or a status bar here
                // via an event or global status service.
            }
        }

        /// <summary>
        /// Safely executes a Task in a fire-and-forget manner with a specific logger.
        /// </summary>
        public static async void SafeFireAndForget(this Task task, ILoggerService logger, string context = "AsyncOperation")
        {
            try
            {
                await task.ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                logger.Error($"[SafeFireAndForget] CRITICAL ERROR in '{context}': {ex.Message}", ex);
            }
        }
    }
}
