using System;
using System.Threading.Tasks;
using ZerodhaDatafeedAdapter.AddOns.OptionSignals.Models;
using ZerodhaDatafeedAdapter.AddOns.OptionSignals.Services;
using ZerodhaDatafeedAdapter.Logging;

namespace ZerodhaDatafeedAdapter.Services.Signals
{
    /// <summary>
    /// Interface for executing signals via external platforms.
    /// </summary>
    public interface IExecutionBridge
    {
        Task<MappedOrderResponse> PlaceSignalOrderAsync(SignalRow signal);
        Task<MappedOrderResponse> ExitSignalOrderAsync(SignalRow signal, double exitPrice = 0, string orderType = "MARKET");
        Task<BridgeStatus> GetStatusAsync();
        int GenerateOrderId();
        Task<string> GetErrorAsync(int requestId);
    }

    /// <summary>
    /// Execution bridge using SignalBridgeService.
    /// Extracted from SignalsOrchestrator for separation of concerns.
    /// </summary>
    public class SignalExecutionBridge : IExecutionBridge
    {
        private readonly SignalBridgeService _bridgeService;
        private readonly ILoggerService _log;

        public SignalExecutionBridge()
        {
            _bridgeService = SignalBridgeService.Instance;
            _log = LoggerFactory.OpSignals;
        }

        /// <summary>
        /// Generates a unique order ID.
        /// </summary>
        public int GenerateOrderId()
        {
            return _bridgeService.GenerateOrderId();
        }

        /// <summary>
        /// Places an entry order for a signal.
        /// </summary>
        public async Task<MappedOrderResponse> PlaceSignalOrderAsync(SignalRow signal)
        {
            try
            {
                _log.Info($"[SignalExecutionBridge] Placing order: OrderId={signal.BridgeOrderId} {signal.Symbol}");

                var response = await _bridgeService.PlaceEntryOrderAsync(signal);

                if (response.IsSuccess)
                {
                    _log.Info($"[SignalExecutionBridge] Order placed successfully: RequestId={response.RequestId}");
                }
                else
                {
                    _log.Warn($"[SignalExecutionBridge] Order placement failed: {response.ErrorMessage}");
                }

                return response;
            }
            catch (Exception ex)
            {
                _log.Error($"[SignalExecutionBridge] PlaceSignalOrderAsync error: {ex.Message}", ex);
                return null;
            }
        }

        /// <summary>
        /// Places an exit order for a signal.
        /// </summary>
        public async Task<MappedOrderResponse> ExitSignalOrderAsync(SignalRow signal, double exitPrice = 0, string orderType = "MARKET")
        {
            try
            {
                _log.Info($"[SignalExecutionBridge] Placing exit order: OrderId={signal.BridgeOrderId} {signal.Symbol}");

                var response = await _bridgeService.PlaceExitOrderAsync(signal, exitPrice, orderType);

                if (response.IsSuccess)
                {
                    _log.Info($"[SignalExecutionBridge] Exit order placed successfully: RequestId={response.RequestId}");
                }
                else
                {
                    _log.Warn($"[SignalExecutionBridge] Exit order placement failed: {response.ErrorMessage}");
                }

                return response;
            }
            catch (Exception ex)
            {
                _log.Error($"[SignalExecutionBridge] ExitSignalOrderAsync error: {ex.Message}", ex);
                return null;
            }
        }

        /// <summary>
        /// Gets the current bridge status.
        /// </summary>
        public async Task<BridgeStatus> GetStatusAsync()
        {
            // Implementation would query the bridge service for status
            return await Task.FromResult(new BridgeStatus { IsConnected = true });
        }

        /// <summary>
        /// Gets detailed error message for a request ID.
        /// </summary>
        public async Task<string> GetErrorAsync(int requestId)
        {
            try
            {
                return await _bridgeService.GetErrorAsync(requestId);
            }
            catch (Exception ex)
            {
                _log.Error($"[SignalExecutionBridge] GetErrorAsync error: {ex.Message}", ex);
                return ex.Message;
            }
        }
    }

    /// <summary>
    /// Bridge status information.
    /// </summary>
    public class BridgeStatus
    {
        public bool IsConnected { get; set; }
        public string StatusMessage { get; set; }
    }
}
