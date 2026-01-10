using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using System.Web;
using Newtonsoft.Json;
using ZerodhaDatafeedAdapter.AddOns.OptionSignals.Models;
using ZerodhaDatafeedAdapter.Logging;
using ZerodhaDatafeedAdapter.Services;

namespace ZerodhaDatafeedAdapter.AddOns.OptionSignals.Services
{
    /// <summary>
    /// Response from IB_MappedOrderMod bridge call.
    /// Returns an Integer. If value < 90000 = error, otherwise success.
    /// The returned request ID can be used for exit signals.
    /// </summary>
    public class MappedOrderResponse
    {
        public int RequestId { get; set; }
        public bool IsSuccess => RequestId >= 90000;
        public string ErrorMessage { get; set; }
    }

    /// <summary>
    /// Bridge service for executing Option Signals via Stoxo IB_MappedOrderMod function.
    ///
    /// Function Signature:
    /// IB_MappedOrderMod(Signal ID, Transaction Type, Source Symbol, Order Type, Trigger Price, Price, Quantity, Signal LTP, Strategy Tag)
    ///
    /// Transaction Types:
    /// - LE: Long Entry (Buy to Open)
    /// - LX: Long Exit (Sell to Close)
    /// - SE: Short Entry (Sell to Open)
    /// - SX: Short Exit (Buy to Close)
    ///
    /// Return: Integer >= 90000 = success, < 90000 = error (use IB_GetError for details)
    /// </summary>
    public class SignalBridgeService
    {
        private static readonly Lazy<SignalBridgeService> _instance =
            new Lazy<SignalBridgeService>(() => new SignalBridgeService());
        public static SignalBridgeService Instance => _instance.Value;

        private static readonly ILoggerService _log = LoggerFactory.OpSignals;
        private readonly HttpClient _httpClient;
        private int _nextOrderId = 100000; // Start from 100000 for unique IDs
        private readonly object _orderIdLock = new object();

        private SignalBridgeService()
        {
            _httpClient = new HttpClient();
            _httpClient.Timeout = TimeSpan.FromSeconds(30);
        }

        /// <summary>
        /// Gets the Stoxo base URL from StoxxoService config.
        /// </summary>
        private string BaseUrl => StoxxoService.Instance.Config.BaseUrl?.TrimEnd('/') ?? "http://localhost:8765";

        /// <summary>
        /// Gets the strategy tag from config.
        /// </summary>
        private string StrategyTag => StoxxoService.Instance.Config.StrategyTag ?? "NINJA";

        /// <summary>
        /// Generates a unique order ID for the signal.
        /// </summary>
        public int GenerateOrderId()
        {
            lock (_orderIdLock)
            {
                return _nextOrderId++;
            }
        }

        /// <summary>
        /// Places an entry order via IB_MappedOrderMod.
        /// </summary>
        /// <param name="signal">The signal to execute</param>
        /// <param name="orderType">Order type: MARKET, LIMIT, SL, SL-M (default: MARKET)</param>
        /// <returns>MappedOrderResponse with RequestId if successful</returns>
        public async Task<MappedOrderResponse> PlaceEntryOrderAsync(SignalRow signal, string orderType = "MARKET")
        {
            // Determine transaction type based on signal direction
            // Long = Buy option (LE), Short = Sell option (SE)
            string txnType = signal.Direction == SignalDirection.Long ? "LE" : "SE";

            return await PlaceMappedOrderAsync(
                signalId: signal.BridgeOrderId,
                txnType: txnType,
                sourceSymbol: signal.Symbol,
                orderType: orderType,
                triggerPrice: 0,
                price: signal.EntryPrice,
                quantity: signal.Quantity,
                signalLTP: signal.CurrentPrice,
                strategyTag: StrategyTag
            );
        }

        /// <summary>
        /// Places an exit order via IB_MappedOrderMod.
        /// Uses the same signal ID as entry for proper matching.
        /// </summary>
        /// <param name="signal">The signal to exit</param>
        /// <param name="exitPrice">Exit price (0 for market)</param>
        /// <param name="orderType">Order type: MARKET, LIMIT, SL, SL-M (default: MARKET)</param>
        public async Task<MappedOrderResponse> PlaceExitOrderAsync(SignalRow signal, double exitPrice = 0, string orderType = "MARKET")
        {
            // Determine transaction type for exit (opposite of entry)
            // Long exit = LX (Sell to close), Short exit = SX (Buy to close)
            string txnType = signal.Direction == SignalDirection.Long ? "LX" : "SX";

            return await PlaceMappedOrderAsync(
                signalId: signal.BridgeOrderId,
                txnType: txnType,
                sourceSymbol: signal.Symbol,
                orderType: orderType,
                triggerPrice: 0,
                price: exitPrice,
                quantity: signal.Quantity,
                signalLTP: signal.CurrentPrice,
                strategyTag: StrategyTag
            );
        }

        /// <summary>
        /// Calls IB_MappedOrderMod bridge function.
        ///
        /// Parameters:
        /// - Signal ID (int): Unique ID, 0 if not used. Use same ID for exit to match entry.
        /// - Transaction Type (string): LE, LX, SE, SX
        /// - Source Symbol (string): Symbol from charting platform (e.g., NIFTY24JAN26100CE)
        /// - Order Type (string): MARKET, LIMIT, SL, SL-M. Empty = use Symbol Mapping setting.
        /// - Trigger Price (decimal): For SL/SL-M orders. 0 to skip.
        /// - Price (decimal): Order price. Mandatory for LIMIT/SL. 0 for MARKET/SL-M.
        /// - Quantity (int): Number of lots. 0 = use Symbol Mapping setting.
        /// - Signal LTP (decimal): LTP from charting platform for reference.
        /// - Strategy Tag (string): Strategy tag for Stoxo routing rules.
        /// </summary>
        private async Task<MappedOrderResponse> PlaceMappedOrderAsync(
            int signalId,
            string txnType,
            string sourceSymbol,
            string orderType,
            double triggerPrice,
            double price,
            int quantity,
            double signalLTP,
            string strategyTag)
        {
            try
            {
                var queryParams = new Dictionary<string, string>
                {
                    { "SignalID", signalId.ToString() },
                    { "TransactionType", txnType },
                    { "SourceSymbol", sourceSymbol },
                    { "OrderType", orderType ?? "" },
                    { "TriggerPrice", triggerPrice.ToString("F2") },
                    { "Price", price.ToString("F2") },
                    { "Quantity", quantity.ToString() },
                    { "SignalLTP", signalLTP.ToString("F2") },
                    { "StrategyTag", strategyTag ?? "DEFAULT" }
                };

                string url = BuildUrl("IB_MappedOrderMod", queryParams);

                TerminalService.Instance.Info($"[SignalBridge] {txnType} {sourceSymbol} Qty={quantity} @ {(price > 0 ? price.ToString("F2") : "MKT")}");
                _log.Info($"[SignalBridgeService] IB_MappedOrderMod: {url}");

                var response = await _httpClient.GetStringAsync(url);
                _log.Info($"[SignalBridgeService] Response: {response}");

                // Parse response - should be an integer
                // >= 90000 = success (RequestId), < 90000 = error code
                if (int.TryParse(response.Trim(), out int requestId))
                {
                    var result = new MappedOrderResponse
                    {
                        RequestId = requestId,
                        ErrorMessage = requestId < 90000 ? $"Error code: {requestId}" : null
                    };

                    if (result.IsSuccess)
                    {
                        TerminalService.Instance.Signal($"[SignalBridge] Order placed: {txnType} {sourceSymbol} RequestID={requestId}");
                    }
                    else
                    {
                        TerminalService.Instance.Error($"[SignalBridge] Order failed: {txnType} {sourceSymbol} Error={requestId}");
                    }

                    return result;
                }

                // Try JSON response format
                try
                {
                    var jsonResponse = JsonConvert.DeserializeObject<dynamic>(response);
                    int resultId = jsonResponse?.response ?? jsonResponse?.requestId ?? 0;
                    string error = jsonResponse?.error?.ToString();

                    return new MappedOrderResponse
                    {
                        RequestId = resultId,
                        ErrorMessage = error
                    };
                }
                catch
                {
                    return new MappedOrderResponse
                    {
                        RequestId = 0,
                        ErrorMessage = $"Invalid response: {response}"
                    };
                }
            }
            catch (Exception ex)
            {
                _log.Error($"[SignalBridgeService] PlaceMappedOrderAsync error: {ex.Message}", ex);
                TerminalService.Instance.Error($"[SignalBridge] Exception: {ex.Message}");

                return new MappedOrderResponse
                {
                    RequestId = 0,
                    ErrorMessage = ex.Message
                };
            }
        }

        /// <summary>
        /// Gets error details for a failed order.
        /// </summary>
        public async Task<string> GetErrorAsync(int errorCode)
        {
            try
            {
                var queryParams = new Dictionary<string, string>
                {
                    { "ErrorCode", errorCode.ToString() }
                };

                string url = BuildUrl("IB_GetError", queryParams);
                var response = await _httpClient.GetStringAsync(url);
                return response.Trim();
            }
            catch (Exception ex)
            {
                return $"Failed to get error: {ex.Message}";
            }
        }

        /// <summary>
        /// Modifies an open order.
        ///
        /// IB_ModifyOrder (Unique ID / Request ID, Qty, Price, TriggerPrice, ProfitValue, StoplossValue, SLTrailingValue, TgtTrailingValue, BreakEvenPoint)
        ///
        /// Supply the parameter to modify, use default values for others (0 for numbers, "" for strings).
        /// </summary>
        public async Task<bool> ModifyOrderAsync(
            int requestId,
            int quantity = 0,
            double price = 0,
            double triggerPrice = 0,
            double profitValue = 0,
            double stoplossValue = 0,
            double slTrailingValue = 0,
            double tgtTrailingValue = 0,
            double breakEvenPoint = 0)
        {
            try
            {
                var queryParams = new Dictionary<string, string>
                {
                    { "UniqueID", requestId.ToString() },
                    { "Qty", quantity.ToString() },
                    { "Price", price.ToString("F2") },
                    { "TriggerPrice", triggerPrice.ToString("F2") },
                    { "ProfitValue", profitValue.ToString("F2") },
                    { "StoplossValue", stoplossValue.ToString("F2") },
                    { "SLTrailingValue", slTrailingValue.ToString("F2") },
                    { "TgtTrailingValue", tgtTrailingValue.ToString("F2") },
                    { "BreakEvenPoint", breakEvenPoint.ToString("F2") }
                };

                string url = BuildUrl("IB_ModifyOrder", queryParams);
                _log.Info($"[SignalBridgeService] IB_ModifyOrder: {url}");

                var response = await _httpClient.GetStringAsync(url);
                _log.Info($"[SignalBridgeService] ModifyOrder response: {response}");

                if (bool.TryParse(response.Trim().ToLower(), out bool result))
                    return result;

                return response.Trim().ToLower() == "true" || response.Contains("success");
            }
            catch (Exception ex)
            {
                _log.Error($"[SignalBridgeService] ModifyOrderAsync error: {ex.Message}", ex);
                return false;
            }
        }

        private string BuildUrl(string endpoint, Dictionary<string, string> queryParams)
        {
            var query = string.Join("&",
                System.Linq.Enumerable.Select(queryParams, kvp =>
                    $"{HttpUtility.UrlEncode(kvp.Key)}={HttpUtility.UrlEncode(kvp.Value)}"));

            return $"{BaseUrl}/{endpoint}?{query}";
        }
    }
}
