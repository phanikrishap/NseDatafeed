using System;
using System.Threading.Tasks;
using ZerodhaDatafeedAdapter.Core;
using ZerodhaDatafeedAdapter.Logging;
using ZerodhaDatafeedAdapter.Models;
using ZerodhaDatafeedAdapter.Services.TBS;

namespace ZerodhaDatafeedAdapter.Services.Trading
{
    /// <summary>
    /// Interface for order routing to execution platforms.
    /// </summary>
    public interface IOrderRouter
    {
        Task<OrderResult> PlaceOrderAsync(TBSExecutionState state);
        Task<bool> ReconcileOrderAsync(TBSExecutionState state);
        Task<bool> ModifyStopLossAsync(TBSExecutionState state);
        Task<bool> ExitOrderAsync(TBSExecutionState state);
        Task PollAndUpdateStatusAsync(TBSExecutionState state);
    }

    /// <summary>
    /// Order router implementation using Stoxxo bridge.
    /// Extracted from TBSExecutionService for separation of concerns.
    /// </summary>
    public class StoxxoOrderRouter : IOrderRouter
    {
        private readonly StoxxoBridgeManager _bridgeManager;

        public StoxxoOrderRouter()
        {
            // Get shared StoxxoBridgeManager from ServiceFactory
            _bridgeManager = ServiceFactory.GetStoxxoBridgeManager();
        }

        /// <summary>
        /// Places an order via Stoxxo bridge.
        /// </summary>
        public async Task<OrderResult> PlaceOrderAsync(TBSExecutionState state)
        {
            try
            {
                TBSLogger.Info($"[StoxxoOrderRouter] Placing order for tranche #{state.TrancheId}");

                var portfolioName = await _bridgeManager.PlaceOrderAsync(state);

                if (!string.IsNullOrEmpty(portfolioName))
                {
                    TBSLogger.Info($"[StoxxoOrderRouter] Order placed successfully: {portfolioName}");
                    return new OrderResult
                    {
                        Success = true,
                        PortfolioName = portfolioName,
                        Message = $"Stoxxo order placed: {portfolioName}"
                    };
                }
                else
                {
                    TBSLogger.Warn($"[StoxxoOrderRouter] Order placement failed for tranche #{state.TrancheId}");
                    return new OrderResult
                    {
                        Success = false,
                        Message = "Stoxxo order failed"
                    };
                }
            }
            catch (Exception ex)
            {
                TBSLogger.Error($"[StoxxoOrderRouter] PlaceOrderAsync error: {ex.Message}");
                return new OrderResult
                {
                    Success = false,
                    Message = $"Stoxxo error: {ex.Message}"
                };
            }
        }

        /// <summary>
        /// Reconciles leg prices after order placement.
        /// </summary>
        public async Task<bool> ReconcileOrderAsync(TBSExecutionState state)
        {
            try
            {
                TBSLogger.Info($"[StoxxoOrderRouter] Reconciling legs for tranche #{state.TrancheId}");
                await _bridgeManager.ReconcileLegsAsync(state);
                return true;
            }
            catch (Exception ex)
            {
                TBSLogger.Error($"[StoxxoOrderRouter] ReconcileOrderAsync error: {ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// Modifies stop loss to cost (hedge action).
        /// </summary>
        public async Task<bool> ModifyStopLossAsync(TBSExecutionState state)
        {
            try
            {
                TBSLogger.Info($"[StoxxoOrderRouter] Modifying SL to cost for tranche #{state.TrancheId}");
                await _bridgeManager.ModifySLToCostAsync(state);
                return true;
            }
            catch (Exception ex)
            {
                TBSLogger.Error($"[StoxxoOrderRouter] ModifyStopLossAsync error: {ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// Exits an active order.
        /// </summary>
        public async Task<bool> ExitOrderAsync(TBSExecutionState state)
        {
            try
            {
                TBSLogger.Info($"[StoxxoOrderRouter] Exiting order for tranche #{state.TrancheId}");

                var success = await _bridgeManager.ExitOrderAsync(state);

                if (success)
                {
                    TBSLogger.Info($"[StoxxoOrderRouter] Order exited successfully");
                    return true;
                }
                else
                {
                    TBSLogger.Warn($"[StoxxoOrderRouter] Order exit failed");
                    return false;
                }
            }
            catch (Exception ex)
            {
                TBSLogger.Error($"[StoxxoOrderRouter] ExitOrderAsync error: {ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// Polls Stoxxo for order status updates.
        /// </summary>
        public async Task PollAndUpdateStatusAsync(TBSExecutionState state)
        {
            try
            {
                await _bridgeManager.PollAndUpdateStatusAsync(state);
            }
            catch (Exception ex)
            {
                TBSLogger.Error($"[StoxxoOrderRouter] PollAndUpdateStatusAsync error: {ex.Message}");
            }
        }
    }

    /// <summary>
    /// Result of an order placement operation.
    /// </summary>
    public class OrderResult
    {
        public bool Success { get; set; }
        public string PortfolioName { get; set; }
        public string Message { get; set; }
    }
}
