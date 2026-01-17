using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using ZerodhaDatafeedAdapter.Models;

namespace ZerodhaDatafeedAdapter.Services.Trading
{
    /// <summary>
    /// Tracks P&L across all TBS execution tranches.
    /// Extracted from TBSExecutionService for separation of concerns.
    /// </summary>
    public class PnLTracker
    {
        private readonly ConcurrentDictionary<int, TBSExecutionState> _executionStates;

        public PnLTracker(ConcurrentDictionary<int, TBSExecutionState> executionStates)
        {
            _executionStates = executionStates ?? throw new ArgumentNullException(nameof(executionStates));
        }

        /// <summary>
        /// Gets total P&L across all Live and SquaredOff tranches (excluding missed).
        /// </summary>
        public decimal GetTotalPnL()
        {
            return _executionStates.Values
                .Where(s => s.Status == TBSExecutionStatus.Live || s.Status == TBSExecutionStatus.SquaredOff)
                .Where(s => !s.IsMissed)
                .Sum(s => s.CombinedPnL);
        }

        /// <summary>
        /// Gets P&L for a specific tranche.
        /// </summary>
        public decimal GetTranchePnL(int trancheId)
        {
            if (_executionStates.TryGetValue(trancheId, out var state))
            {
                return state.CombinedPnL;
            }
            return 0m;
        }

        /// <summary>
        /// Updates position P&L for a specific leg.
        /// </summary>
        public void UpdatePositionPnL(int trancheId, string legOptionType, decimal pnl)
        {
            if (_executionStates.TryGetValue(trancheId, out var state))
            {
                var leg = state.Legs.FirstOrDefault(l => l.OptionType == legOptionType);
                if (leg != null)
                {
                    // P&L is computed from entry and current prices elsewhere
                    // This method is here for future extensibility
                }
            }
        }

        /// <summary>
        /// Gets a snapshot of current P&L state.
        /// </summary>
        public PnLSnapshot GetSnapshot()
        {
            var snapshot = new PnLSnapshot
            {
                Timestamp = DateTime.Now,
                TotalPnL = GetTotalPnL(),
                TranchePnLs = new Dictionary<int, decimal>()
            };

            foreach (var kvp in _executionStates)
            {
                if (kvp.Value.Status == TBSExecutionStatus.Live || kvp.Value.Status == TBSExecutionStatus.SquaredOff)
                {
                    snapshot.TranchePnLs[kvp.Key] = kvp.Value.CombinedPnL;
                }
            }

            return snapshot;
        }

        /// <summary>
        /// Gets the count of live tranches.
        /// </summary>
        public int GetLiveCount()
        {
            return _executionStates.Values.Count(s => s.Status == TBSExecutionStatus.Live);
        }

        /// <summary>
        /// Gets the count of monitoring tranches.
        /// </summary>
        public int GetMonitoringCount()
        {
            return _executionStates.Values.Count(s => s.Status == TBSExecutionStatus.Monitoring);
        }
    }

    /// <summary>
    /// Snapshot of P&L state at a specific point in time.
    /// </summary>
    public class PnLSnapshot
    {
        public DateTime Timestamp { get; set; }
        public decimal TotalPnL { get; set; }
        public Dictionary<int, decimal> TranchePnLs { get; set; }
    }
}
