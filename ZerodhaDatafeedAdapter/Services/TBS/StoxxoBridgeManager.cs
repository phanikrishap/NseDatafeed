using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Reactive.Linq;
using System.Threading;
using System.Threading.Tasks;
using ZerodhaDatafeedAdapter.Logging;
using ZerodhaDatafeedAdapter.Models;

namespace ZerodhaDatafeedAdapter.Services.TBS
{
    /// <summary>
    /// Manages Stoxxo API integration for TBS execution.
    /// Handles order placement, reconciliation, SL modification, and exit operations.
    /// Extracted from TBSExecutionService for better separation of concerns.
    /// </summary>
    public class StoxxoBridgeManager
    {
        #region Fields

        private readonly StoxxoService _stoxxoService;

        // Per-tranche locks to prevent concurrent reconciliation
        private readonly ConcurrentDictionary<int, SemaphoreSlim> _reconcileLocks = new ConcurrentDictionary<int, SemaphoreSlim>();

        #endregion

        #region Constructor

        public StoxxoBridgeManager()
        {
            _stoxxoService = StoxxoService.Instance;
        }

        public StoxxoBridgeManager(StoxxoService stoxxoService)
        {
            _stoxxoService = stoxxoService ?? throw new ArgumentNullException(nameof(stoxxoService));
        }

        #endregion

        #region Order Placement

        /// <summary>
        /// Place a Stoxxo multi-leg order for a TBS tranche
        /// </summary>
        /// <param name="state">The execution state for the tranche</param>
        /// <returns>Portfolio name if successful, null otherwise</returns>
        public async Task<string> PlaceOrderAsync(TBSExecutionState state)
        {
            if (state == null) throw new ArgumentNullException(nameof(state));

            try
            {
                var underlying = state.Config?.Underlying ?? "NIFTY";
                int lots = state.Quantity;

                string combinedLoss = state.CombinedSLPercent > 0
                    ? StoxxoHelper.FormatPercentage(state.CombinedSLPercent * 100)
                    : "0";

                string legSL = state.IndividualSLPercent > 0
                    ? StoxxoHelper.FormatPercentage(state.IndividualSLPercent * 100)
                    : "0";

                int slToCost = state.HedgeAction?.Contains("hedge_to_cost") == true ? 1 : 0;
                int startSeconds = StoxxoHelper.TimeSpanToSeconds(state.Config.EntryTime);

                string portLegs = StoxxoHelper.BuildPortLegs(
                    lots: lots,
                    slPercent: state.IndividualSLPercent,
                    strike: "ATM"
                );

                TBSLogger.Info($"[StoxxoBridge] Placing order for Tranche #{state.TrancheId}: {underlying}, {lots} lots");

                var result = await _stoxxoService.PlaceMultiLegOrderAdv(
                    underlying, lots, combinedLoss, legSL, slToCost, startSeconds,
                    endSeconds: 0, sqOffSeconds: 0, portLegs: portLegs);

                if (!string.IsNullOrEmpty(result))
                {
                    TBSLogger.Info($"[StoxxoBridge] Order placed for Tranche #{state.TrancheId}: {result}");
                    return result;
                }
                else
                {
                    TBSLogger.Warn($"[StoxxoBridge] Order placement returned empty for Tranche #{state.TrancheId}");
                    return null;
                }
            }
            catch (Exception ex)
            {
                TBSLogger.Error($"[StoxxoBridge] PlaceOrderAsync error for Tranche #{state.TrancheId}: {ex.Message}", ex);
                throw;
            }
        }

        #endregion

        #region Reconciliation

        private const int RECONCILE_RETRY_INTERVAL_MS = 2000;  // Retry every 2 seconds
        private const int RECONCILE_MAX_RETRIES = 15;          // Max 15 retries = 30 seconds total

        /// <summary>
        /// Reconcile Stoxxo legs with internal TBS state using Rx retry pattern.
        /// Stoxxo executes BUY legs first, then SELL legs ~1 second later.
        /// This method retries until both CE and PE SELL legs are mapped (up to 30 seconds).
        /// Uses per-tranche locking to prevent concurrent reconciliation race conditions.
        /// </summary>
        /// <param name="state">The execution state to reconcile</param>
        /// <returns>Number of legs reconciled</returns>
        public async Task<int> ReconcileLegsAsync(TBSExecutionState state)
        {
            if (state == null) throw new ArgumentNullException(nameof(state));
            if (string.IsNullOrEmpty(state.StoxxoPortfolioName)) return 0;

            // Get or create a lock for this tranche to prevent concurrent reconciliation
            var trancheLock = _reconcileLocks.GetOrAdd(state.TrancheId, _ => new SemaphoreSlim(1, 1));

            // Try to acquire lock without blocking - if another reconciliation is in progress, skip
            if (!await trancheLock.WaitAsync(0))
            {
                TBSLogger.Info($"[StoxxoBridge] ReconcileLegsAsync SKIPPED for Tranche #{state.TrancheId} - another reconciliation in progress");
                return CountMappedLegs(state);
            }

            try
            {
                TBSLogger.Info($"[StoxxoBridge] ReconcileLegsAsync START for Tranche #{state.TrancheId}, Portfolio={state.StoxxoPortfolioName}");

                // Use Rx to retry reconciliation until we get both SELL legs mapped
                var result = await Observable
                    .Interval(TimeSpan.FromMilliseconds(RECONCILE_RETRY_INTERVAL_MS))
                    .StartWith(0) // Start immediately
                    .Take(RECONCILE_MAX_RETRIES)
                    .Select(async attempt =>
                    {
                        TBSLogger.Info($"[StoxxoBridge] ReconcileLegsAsync attempt {attempt + 1}/{RECONCILE_MAX_RETRIES} for Tranche #{state.TrancheId}");
                        return await TryReconcileOnceAsync(state);
                    })
                    .SelectMany(task => task)
                    .TakeWhile(mappedCount => mappedCount < 2) // Stop when both legs mapped
                    .LastOrDefaultAsync();

                // Check if we got both legs mapped
                int finalMapped = CountMappedLegs(state);
                if (finalMapped >= 2)
                {
                    TBSLogger.Info($"[StoxxoBridge] ReconcileLegsAsync SUCCESS: Both CE and PE legs mapped for Tranche #{state.TrancheId}");
                }
                else
                {
                    TBSLogger.Warn($"[StoxxoBridge] ReconcileLegsAsync TIMEOUT: Only {finalMapped}/2 legs mapped after {RECONCILE_MAX_RETRIES} attempts for Tranche #{state.TrancheId}");
                }

                return finalMapped;
            }
            catch (Exception ex)
            {
                TBSLogger.Error($"[StoxxoBridge] ReconcileLegsAsync error for Tranche #{state.TrancheId}: {ex.Message}", ex);
                return 0;
            }
            finally
            {
                trancheLock.Release();
            }
        }

        /// <summary>
        /// Single reconciliation attempt - fetches legs and tries to map them
        /// </summary>
        private async Task<int> TryReconcileOnceAsync(TBSExecutionState state)
        {
            try
            {
                var legs = await _stoxxoService.GetUserLegs(state.StoxxoPortfolioName, onlyActiveLegs: true);

                TBSLogger.Info($"[StoxxoBridge] TryReconcileOnceAsync: GetUserLegs returned {legs?.Count ?? 0} legs for Portfolio={state.StoxxoPortfolioName}");

                if (legs == null || legs.Count == 0)
                {
                    TBSLogger.Warn($"[StoxxoBridge] TryReconcileOnceAsync: No legs found for reconciliation: {state.StoxxoPortfolioName}");
                    return 0;
                }

                // Log each leg received before mapping
                for (int i = 0; i < legs.Count; i++)
                {
                    var leg = legs[i];
                    TBSLogger.Info($"[StoxxoBridge] TryReconcileOnceAsync: Received Leg[{i}]: LegID={leg.LegID}, Ins={leg.Instrument}, Txn={leg.Txn}, Strike={leg.Strike}, EntryQty={leg.EntryFilledQty}, AvgEntry={leg.AvgEntryPrice}, Status={leg.Status}");
                }

                int mappedCount = MapStoxxoLegsToInternal(state, legs);
                TBSLogger.Info($"[StoxxoBridge] TryReconcileOnceAsync: {legs.Count} API legs received, {mappedCount} legs mapped to TBS state for Tranche #{state.TrancheId}");
                return mappedCount;
            }
            catch (Exception ex)
            {
                TBSLogger.Error($"[StoxxoBridge] TryReconcileOnceAsync error for Tranche #{state.TrancheId}: {ex.Message}", ex);
                return 0;
            }
        }

        /// <summary>
        /// Count how many legs have been successfully mapped (have StoxxoLegID set)
        /// </summary>
        private int CountMappedLegs(TBSExecutionState state)
        {
            return state.Legs.Count(l => l.StoxxoLegID > 0);
        }

        /// <summary>
        /// Map Stoxxo leg data to internal TBS leg state.
        /// IMPORTANT: Stoxxo is the source of truth for strike - if Stoxxo executed at a different
        /// ATM strike, we realign TBS state to match Stoxxo's actual executed strike.
        /// </summary>
        /// <returns>Number of legs successfully mapped</returns>
        private int MapStoxxoLegsToInternal(TBSExecutionState state, List<StoxxoUserLeg> stoxxoLegs)
        {
            TBSLogger.Info($"[StoxxoBridge] MapStoxxoLegsToInternal: Processing {stoxxoLegs.Count} Stoxxo legs for Tranche #{state.TrancheId}, TBS Strike={state.Strike}");

            // Log all legs before filtering
            foreach (var leg in stoxxoLegs)
            {
                TBSLogger.Info($"[StoxxoBridge] MapStoxxoLegsToInternal: ALL LEG - LegID={leg.LegID}, Txn={leg.Txn}, Ins={leg.Instrument}, Strike={leg.Strike}, Symbol={leg.Symbol}");
            }

            var sellLegs = stoxxoLegs
                .Where(l => l.Txn?.Equals("Sell", StringComparison.OrdinalIgnoreCase) == true)
                .ToList();

            TBSLogger.Info($"[StoxxoBridge] MapStoxxoLegsToInternal: Found {sellLegs.Count} SELL legs (filtering by Txn=Sell)");

            // Log filtered sell legs
            foreach (var leg in sellLegs)
            {
                TBSLogger.Info($"[StoxxoBridge] MapStoxxoLegsToInternal: SELL LEG - LegID={leg.LegID}, Ins={leg.Instrument}, Strike={leg.Strike}, EntryQty={leg.EntryFilledQty}, Symbol={leg.Symbol}");
            }

            var ceLeg = sellLegs.FirstOrDefault(l => l.Instrument?.Equals("CE", StringComparison.OrdinalIgnoreCase) == true);
            var peLeg = sellLegs.FirstOrDefault(l => l.Instrument?.Equals("PE", StringComparison.OrdinalIgnoreCase) == true);

            TBSLogger.Info($"[StoxxoBridge] MapStoxxoLegsToInternal: CE Leg Found={ceLeg != null}, PE Leg Found={peLeg != null}");

            if (ceLeg != null)
                TBSLogger.Info($"[StoxxoBridge] MapStoxxoLegsToInternal: CE LEG DETAIL - LegID={ceLeg.LegID}, Strike={ceLeg.Strike}, EntryQty={ceLeg.EntryFilledQty}, AvgEntry={ceLeg.AvgEntryPrice}, Symbol={ceLeg.Symbol}");
            if (peLeg != null)
                TBSLogger.Info($"[StoxxoBridge] MapStoxxoLegsToInternal: PE LEG DETAIL - LegID={peLeg.LegID}, Strike={peLeg.Strike}, EntryQty={peLeg.EntryFilledQty}, AvgEntry={peLeg.AvgEntryPrice}, Symbol={peLeg.Symbol}");

            // STRIKE REALIGNMENT: Check if Stoxxo executed at a different strike than TBS assumed
            // Stoxxo is the source of truth - we need to monitor the ACTUAL executed strike
            decimal stoxxoStrike = 0;
            if (ceLeg != null && ceLeg.Strike > 0)
                stoxxoStrike = ceLeg.Strike;
            else if (peLeg != null && peLeg.Strike > 0)
                stoxxoStrike = peLeg.Strike;

            if (stoxxoStrike > 0 && state.Strike > 0 && stoxxoStrike != state.Strike)
            {
                TBSLogger.Warn($"[StoxxoBridge] STRIKE MISMATCH DETECTED for Tranche #{state.TrancheId}: " +
                    $"TBS assumed Strike={state.Strike}, Stoxxo executed at Strike={stoxxoStrike}");
                TBSLogger.Warn($"[StoxxoBridge] REALIGNING Tranche #{state.TrancheId} strike from {state.Strike} to {stoxxoStrike} (Stoxxo is source of truth)");

                // Realign the TBS state to Stoxxo's actual executed strike
                RealignStrikeToStoxxo(state, stoxxoStrike);
            }

            int mappedCount = 0;
            foreach (var leg in state.Legs)
            {
                var stoxxoLeg = leg.OptionType == "CE" ? ceLeg : peLeg;
                if (stoxxoLeg != null)
                {
                    leg.StoxxoLegID = stoxxoLeg.LegID;
                    leg.StoxxoQty = stoxxoLeg.EntryFilledQty;
                    leg.StoxxoEntryPrice = stoxxoLeg.AvgEntryPrice;
                    leg.StoxxoExitPrice = stoxxoLeg.AvgExitPrice;
                    leg.StoxxoStatus = stoxxoLeg.Status;
                    mappedCount++;
                    TBSLogger.Info($"[StoxxoBridge] MapStoxxoLegsToInternal: MAPPED {leg.OptionType} -> StoxxoLegID={leg.StoxxoLegID}, Qty={leg.StoxxoQty}, EntryPrice={leg.StoxxoEntryPrice}, Symbol={leg.Symbol}");
                }
                else
                {
                    TBSLogger.Warn($"[StoxxoBridge] MapStoxxoLegsToInternal: NO MATCH for {leg.OptionType} leg in Stoxxo response");
                }
            }

            TBSLogger.Info($"[StoxxoBridge] MapStoxxoLegsToInternal COMPLETE: {mappedCount}/{state.Legs.Count} TBS legs mapped, Final Strike={state.Strike}");
            return mappedCount;
        }

        /// <summary>
        /// Realign TBS state strike and leg symbols to match Stoxxo's actual executed strike.
        /// This ensures we're monitoring stop-losses for the correct options.
        /// </summary>
        private void RealignStrikeToStoxxo(TBSExecutionState state, decimal stoxxoStrike)
        {
            decimal oldStrike = state.Strike;
            state.Strike = stoxxoStrike;

            // Update leg symbols to match new strike
            var underlying = state.Config?.Underlying ?? "NIFTY";

            // Get expiry from MarketAnalyzerLogic (same pattern as TBSExecutionService.UpdateLegSymbols)
            DateTime? expiry = null;
            var selectedExpiryStr = Services.Analysis.MarketAnalyzerLogic.Instance.SelectedExpiry;
            if (!string.IsNullOrEmpty(selectedExpiryStr) && DateTime.TryParse(selectedExpiryStr, out var parsed))
                expiry = parsed;

            if (!expiry.HasValue)
            {
                TBSLogger.Warn($"[StoxxoBridge] RealignStrikeToStoxxo: Could not get expiry for {underlying}, symbols not updated");
                return;
            }

            // Get isMonthlyExpiry flag
            bool isMonthlyExpiry = Services.Analysis.MarketAnalyzerLogic.Instance.SelectedIsMonthlyExpiry;

            // If underlying doesn't match, try to determine from cached expiries
            if (Services.Analysis.MarketAnalyzerLogic.Instance.SelectedUnderlying != underlying)
            {
                var expiries = Services.Analysis.MarketAnalyzerLogic.Instance.GetCachedExpiries(underlying);
                if (expiries != null && expiries.Count > 0)
                {
                    isMonthlyExpiry = Helpers.SymbolHelper.IsMonthlyExpiry(expiry.Value, expiries);
                }
            }

            foreach (var leg in state.Legs)
            {
                string oldSymbol = leg.Symbol;
                string newSymbol = Helpers.SymbolHelper.BuildOptionSymbol(underlying, expiry.Value, stoxxoStrike, leg.OptionType, isMonthlyExpiry);
                leg.Symbol = newSymbol;
                TBSLogger.Info($"[StoxxoBridge] RealignStrikeToStoxxo: Tranche #{state.TrancheId} {leg.OptionType} symbol changed: {oldSymbol} -> {newSymbol}");
            }

            TBSLogger.Info($"[StoxxoBridge] RealignStrikeToStoxxo COMPLETE: Tranche #{state.TrancheId} realigned from Strike={oldStrike} to Strike={stoxxoStrike}");
        }

        #endregion

        #region Leg Updates

        /// <summary>
        /// Update Stoxxo leg details for a tranche.
        /// Also validates strike consistency - if Stoxxo reports a different strike, realigns TBS state.
        /// </summary>
        public async Task UpdateLegDetailsAsync(TBSExecutionState state)
        {
            if (state == null) throw new ArgumentNullException(nameof(state));
            if (string.IsNullOrEmpty(state.StoxxoPortfolioName)) return;

            try
            {
                var legs = await _stoxxoService.GetUserLegs(state.StoxxoPortfolioName, onlyActiveLegs: false);
                if (legs == null || legs.Count == 0) return;

                var sellLegs = legs
                    .Where(l => l.Txn?.Equals("Sell", StringComparison.OrdinalIgnoreCase) == true)
                    .ToList();

                // Check for strike mismatch on every update (Stoxxo is source of truth)
                var ceLeg = sellLegs.FirstOrDefault(l => l.Instrument?.Equals("CE", StringComparison.OrdinalIgnoreCase) == true);
                var peLeg = sellLegs.FirstOrDefault(l => l.Instrument?.Equals("PE", StringComparison.OrdinalIgnoreCase) == true);

                decimal stoxxoStrike = 0;
                if (ceLeg != null && ceLeg.Strike > 0)
                    stoxxoStrike = ceLeg.Strike;
                else if (peLeg != null && peLeg.Strike > 0)
                    stoxxoStrike = peLeg.Strike;

                if (stoxxoStrike > 0 && state.Strike > 0 && stoxxoStrike != state.Strike)
                {
                    TBSLogger.Warn($"[StoxxoBridge] UpdateLegDetailsAsync: STRIKE MISMATCH for Tranche #{state.TrancheId}: " +
                        $"TBS Strike={state.Strike}, Stoxxo Strike={stoxxoStrike} - REALIGNING");
                    RealignStrikeToStoxxo(state, stoxxoStrike);
                }

                foreach (var leg in state.Legs)
                {
                    var stoxxoLeg = sellLegs.FirstOrDefault(l =>
                        l.Instrument?.Equals(leg.OptionType, StringComparison.OrdinalIgnoreCase) == true);

                    if (stoxxoLeg != null)
                    {
                        leg.StoxxoEntryPrice = stoxxoLeg.AvgEntryPrice;
                        leg.StoxxoExitPrice = stoxxoLeg.AvgExitPrice;
                        leg.StoxxoStatus = stoxxoLeg.Status;
                    }
                }
            }
            catch (Exception ex)
            {
                TBSLogger.Debug($"[StoxxoBridge] UpdateLegDetailsAsync error for Tranche #{state.TrancheId}: {ex.Message}");
            }
        }

        #endregion

        #region SL Modification

        /// <summary>
        /// Modify Stoxxo SL to cost (hedge to cost action)
        /// </summary>
        /// <param name="state">The execution state</param>
        /// <returns>True if all modifications succeeded</returns>
        public async Task<bool> ModifySLToCostAsync(TBSExecutionState state)
        {
            if (state == null) throw new ArgumentNullException(nameof(state));
            if (string.IsNullOrEmpty(state.StoxxoPortfolioName)) return false;

            try
            {
                bool allSuccess = true;

                foreach (var leg in state.Legs.Where(l => l.Status == TBSLegStatus.Active))
                {
                    string legFilter = $"INS:{leg.OptionType}";
                    string slValue = leg.EntryPrice.ToString("F2");

                    var success = await _stoxxoService.ModifyPortfolio(
                        state.StoxxoPortfolioName, "LegSL", slValue, legFilter);

                    if (success)
                    {
                        TBSLogger.Info($"[StoxxoBridge] SL modified to cost for Tranche #{state.TrancheId} {leg.OptionType}");
                    }
                    else
                    {
                        TBSLogger.Warn($"[StoxxoBridge] SL modification failed for Tranche #{state.TrancheId} {leg.OptionType}");
                        allSuccess = false;
                    }
                }

                return allSuccess;
            }
            catch (Exception ex)
            {
                TBSLogger.Error($"[StoxxoBridge] ModifySLToCostAsync error for Tranche #{state.TrancheId}: {ex.Message}", ex);
                return false;
            }
        }

        #endregion

        #region Exit Operations

        /// <summary>
        /// Exit a Stoxxo portfolio
        /// </summary>
        /// <param name="state">The execution state</param>
        /// <returns>True if exit was successful</returns>
        public async Task<bool> ExitOrderAsync(TBSExecutionState state)
        {
            if (state == null) throw new ArgumentNullException(nameof(state));
            if (string.IsNullOrEmpty(state.StoxxoPortfolioName)) return false;

            try
            {
                var success = await _stoxxoService.ExitMultiLegOrder(state.StoxxoPortfolioName);

                if (success)
                {
                    TBSLogger.Info($"[StoxxoBridge] Exit successful for Tranche #{state.TrancheId}");
                }
                else
                {
                    TBSLogger.Warn($"[StoxxoBridge] Exit failed for Tranche #{state.TrancheId}");
                }

                return success;
            }
            catch (Exception ex)
            {
                TBSLogger.Error($"[StoxxoBridge] ExitOrderAsync error for Tranche #{state.TrancheId}: {ex.Message}", ex);
                return false;
            }
        }

        #endregion

        #region Status Polling

        /// <summary>
        /// Get portfolio status from Stoxxo
        /// </summary>
        public async Task<StoxxoPortfolioStatus> GetPortfolioStatusAsync(string portfolioName)
        {
            if (string.IsNullOrEmpty(portfolioName)) return StoxxoPortfolioStatus.Unknown;

            try
            {
                return await _stoxxoService.GetPortfolioStatus(portfolioName);
            }
            catch (Exception ex)
            {
                TBSLogger.Debug($"[StoxxoBridge] GetPortfolioStatusAsync error: {ex.Message}");
                return StoxxoPortfolioStatus.Unknown;
            }
        }

        /// <summary>
        /// Get portfolio MTM from Stoxxo
        /// </summary>
        public async Task<decimal> GetPortfolioMTMAsync(string portfolioName)
        {
            if (string.IsNullOrEmpty(portfolioName)) return 0;

            try
            {
                return await _stoxxoService.GetPortfolioMTM(portfolioName);
            }
            catch (Exception ex)
            {
                TBSLogger.Debug($"[StoxxoBridge] GetPortfolioMTMAsync error: {ex.Message}");
                return 0;
            }
        }

        /// <summary>
        /// Poll and update status for a tranche
        /// </summary>
        public async Task PollAndUpdateStatusAsync(TBSExecutionState state)
        {
            if (state == null || string.IsNullOrEmpty(state.StoxxoPortfolioName)) return;

            try
            {
                var status = await GetPortfolioStatusAsync(state.StoxxoPortfolioName);
                if (status != StoxxoPortfolioStatus.Unknown)
                {
                    state.StoxxoStatus = status.ToString();
                }

                var mtm = await GetPortfolioMTMAsync(state.StoxxoPortfolioName);
                state.StoxxoPnL = mtm;

                if (state.StoxxoReconciled)
                {
                    await UpdateLegDetailsAsync(state);
                }
            }
            catch (Exception ex)
            {
                TBSLogger.Debug($"[StoxxoBridge] PollAndUpdateStatusAsync error for Tranche #{state.TrancheId}: {ex.Message}");
            }
        }

        #endregion
    }
}
