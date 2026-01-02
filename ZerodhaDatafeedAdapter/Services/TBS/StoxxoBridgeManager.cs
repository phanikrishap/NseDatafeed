using System;
using System.Collections.Generic;
using System.Linq;
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

        /// <summary>
        /// Reconcile Stoxxo legs with internal TBS state
        /// </summary>
        /// <param name="state">The execution state to reconcile</param>
        /// <returns>Number of legs reconciled</returns>
        public async Task<int> ReconcileLegsAsync(TBSExecutionState state)
        {
            if (state == null) throw new ArgumentNullException(nameof(state));
            if (string.IsNullOrEmpty(state.StoxxoPortfolioName)) return 0;

            try
            {
                var legs = await _stoxxoService.GetUserLegs(state.StoxxoPortfolioName, onlyActiveLegs: true);
                if (legs == null || legs.Count == 0)
                {
                    TBSLogger.Debug($"[StoxxoBridge] No legs found for reconciliation: {state.StoxxoPortfolioName}");
                    return 0;
                }

                MapStoxxoLegsToInternal(state, legs);
                TBSLogger.Info($"[StoxxoBridge] Reconciled {legs.Count} legs for Tranche #{state.TrancheId}");
                return legs.Count;
            }
            catch (Exception ex)
            {
                TBSLogger.Error($"[StoxxoBridge] ReconcileLegsAsync error for Tranche #{state.TrancheId}: {ex.Message}", ex);
                return 0;
            }
        }

        /// <summary>
        /// Map Stoxxo leg data to internal TBS leg state
        /// </summary>
        private void MapStoxxoLegsToInternal(TBSExecutionState state, List<StoxxoUserLeg> stoxxoLegs)
        {
            var sellLegs = stoxxoLegs
                .Where(l => l.Txn?.Equals("Sell", StringComparison.OrdinalIgnoreCase) == true)
                .ToList();

            var ceLeg = sellLegs.FirstOrDefault(l => l.Instrument?.Equals("CE", StringComparison.OrdinalIgnoreCase) == true);
            var peLeg = sellLegs.FirstOrDefault(l => l.Instrument?.Equals("PE", StringComparison.OrdinalIgnoreCase) == true);

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
                }
            }
        }

        #endregion

        #region Leg Updates

        /// <summary>
        /// Update Stoxxo leg details for a tranche
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
