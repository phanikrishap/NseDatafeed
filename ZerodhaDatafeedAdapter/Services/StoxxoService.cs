using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Threading.Tasks;
using System.Web;
using Newtonsoft.Json;
using ZerodhaDatafeedAdapter.Models;

namespace ZerodhaDatafeedAdapter.Services
{
    /// <summary>
    /// HTTP client service for Stoxxo bridge API integration
    /// </summary>
    public class StoxxoService
    {
        private static StoxxoService _instance;
        private static readonly object _lock = new object();

        private readonly HttpClient _httpClient;
        private StoxxoConfig _config;
        private bool _isConfigLoaded = false;

        public static StoxxoService Instance
        {
            get
            {
                if (_instance == null)
                {
                    lock (_lock)
                    {
                        if (_instance == null)
                            _instance = new StoxxoService();
                    }
                }
                return _instance;
            }
        }

        /// <summary>
        /// Current Stoxxo configuration
        /// </summary>
        public StoxxoConfig Config
        {
            get
            {
                if (!_isConfigLoaded)
                    LoadConfig();
                return _config;
            }
        }

        private StoxxoService()
        {
            _httpClient = new HttpClient();
            _httpClient.Timeout = TimeSpan.FromSeconds(30);
            _config = new StoxxoConfig();
        }

        /// <summary>
        /// Load Stoxxo configuration from config.json
        /// </summary>
        public void LoadConfig()
        {
            try
            {
                string documentsPath = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
                string configPath = Path.Combine(documentsPath, "NinjaTrader 8", "ZerodhaAdapter", "config.json");

                if (File.Exists(configPath))
                {
                    string json = File.ReadAllText(configPath);
                    var configRoot = JsonConvert.DeserializeObject<Dictionary<string, object>>(json);

                    if (configRoot != null && configRoot.ContainsKey("Stoxxo"))
                    {
                        var stoxxoJson = JsonConvert.SerializeObject(configRoot["Stoxxo"]);
                        _config = JsonConvert.DeserializeObject<StoxxoConfig>(stoxxoJson) ?? new StoxxoConfig();
                        Logger.Info($"[StoxxoService] Loaded config: BaseUrl={_config.BaseUrl}, NiftyPortfolio={_config.NiftyPortfolioName}, SensexPortfolio={_config.SensexPortfolioName}");
                    }
                    else
                    {
                        Logger.Warn("[StoxxoService] Stoxxo section not found in config.json, using defaults");
                        _config = new StoxxoConfig();
                    }
                }
                else
                {
                    Logger.Warn($"[StoxxoService] Config file not found: {configPath}, using defaults");
                    _config = new StoxxoConfig();
                }

                _isConfigLoaded = true;
            }
            catch (Exception ex)
            {
                Logger.Error($"[StoxxoService] Error loading config: {ex.Message}", ex);
                _config = new StoxxoConfig();
                _isConfigLoaded = true;
            }
        }

        /// <summary>
        /// Reload configuration
        /// </summary>
        public void ReloadConfig()
        {
            _isConfigLoaded = false;
            LoadConfig();
        }

        #region API Methods

        /// <summary>
        /// Place a multi-leg order with advanced options including start time and custom leg details
        /// </summary>
        /// <param name="underlying">Symbol like NIFTY or SENSEX</param>
        /// <param name="lots">Number of lots</param>
        /// <param name="combinedLoss">Combined stop-loss (e.g., "5000" or "20P" for percentage)</param>
        /// <param name="legSL">Individual leg SL (e.g., "50P") - applied to all legs if no PortLegs specified</param>
        /// <param name="slToCost">1 to enable SL-to-cost, 0 to disable</param>
        /// <param name="startSeconds">Entry time in seconds (e.g., 33300 for 09:15:00)</param>
        /// <param name="endSeconds">End time in seconds (0 to skip)</param>
        /// <param name="sqOffSeconds">Square-off time in seconds (0 to skip)</param>
        /// <param name="portLegs">Custom leg details in Stoxxo format (e.g., "Strike:ATM|Txn:SELL|Ins:PE|Expiry:CW|Lots:2|SL:Premium:50P||Strike:ATM|Txn:SELL|Ins:CE|Expiry:CW|Lots:2|SL:Premium:50P")</param>
        /// <returns>Portfolio name returned by Stoxxo, or null on failure</returns>
        public async Task<string> PlaceMultiLegOrderAdv(
            string underlying,
            int lots,
            string combinedLoss = "0",
            string legSL = "0",
            int slToCost = 1,
            int startSeconds = 0,
            int endSeconds = 0,
            int sqOffSeconds = 0,
            string portLegs = null)
        {
            try
            {
                string portfolioName = Config.GetPortfolioName(underlying);

                var queryParams = new Dictionary<string, string>
                {
                    { "OptionPortfolioName", portfolioName },
                    { "StrategyTag", Config.StrategyTag },
                    { "Symbol", underlying },
                    { "Product", Config.Product },
                    { "CombinedProfit", "0" },
                    { "CombinedLoss", combinedLoss },
                    { "LegTarget", "0" },
                    { "LegSL", legSL },
                    { "Lots", lots.ToString() },
                    { "NoDuplicateOrderForSeconds", Config.NoDuplicateOrderForSeconds.ToString() },
                    { "EntryPrice", "0" },
                    { "SLtoCost", slToCost == 1 ? "Yes" : "No" }
                };

                // Add time parameters if specified
                if (startSeconds > 0)
                    queryParams["StartSeconds"] = startSeconds.ToString();
                if (endSeconds > 0)
                    queryParams["EndSeconds"] = endSeconds.ToString();
                if (sqOffSeconds > 0)
                    queryParams["SqOffSeconds"] = sqOffSeconds.ToString();

                // Add custom leg details if specified
                if (!string.IsNullOrEmpty(portLegs))
                    queryParams["PortLegs"] = portLegs;

                string url = BuildUrl("PlaceMultiLegOrderAdv", queryParams);
                Logger.Info($"[StoxxoService] PlaceMultiLegOrderAdv: {url}");

                var response = await _httpClient.GetStringAsync(url);
                Logger.Info($"[StoxxoService] PlaceMultiLegOrderAdv response: {response}");

                var result = JsonConvert.DeserializeObject<StoxxoResponse>(response);
                if (result != null && result.IsSuccess)
                {
                    return result.response; // This is the portfolio name like "NF_MULTILEG_1"
                }

                Logger.Error($"[StoxxoService] PlaceMultiLegOrderAdv failed: {result?.error ?? "Unknown error"}");
                return null;
            }
            catch (Exception ex)
            {
                Logger.Error($"[StoxxoService] PlaceMultiLegOrderAdv error: {ex.Message}", ex);
                return null;
            }
        }

        /// <summary>
        /// Get all legs of a portfolio
        /// </summary>
        public async Task<List<StoxxoLeg>> GetLegs(string portfolioName)
        {
            try
            {
                var queryParams = new Dictionary<string, string>
                {
                    { "OptionPortfolioName", portfolioName }
                };

                string url = BuildUrl("GetLegs", queryParams);
                Logger.Debug($"[StoxxoService] GetLegs: {url}");

                var response = await _httpClient.GetStringAsync(url);
                var result = JsonConvert.DeserializeObject<StoxxoResponse>(response);

                if (result != null && result.IsSuccess && !string.IsNullOrEmpty(result.response))
                {
                    // Legs are separated by || (double pipes), each leg's fields separated by | (single pipe)
                    var legStrings = result.response.Split(new[] { "||" }, StringSplitOptions.RemoveEmptyEntries);
                    var legs = new List<StoxxoLeg>();

                    foreach (var legStr in legStrings)
                    {
                        var leg = StoxxoLeg.Parse(legStr.Trim());
                        if (leg != null)
                            legs.Add(leg);
                    }

                    Logger.Debug($"[StoxxoService] GetLegs returned {legs.Count} legs");
                    return legs;
                }

                Logger.Warn($"[StoxxoService] GetLegs failed: {result?.error ?? "No data"}");
                return new List<StoxxoLeg>();
            }
            catch (Exception ex)
            {
                Logger.Error($"[StoxxoService] GetLegs error: {ex.Message}", ex);
                return new List<StoxxoLeg>();
            }
        }

        /// <summary>
        /// Get user legs with execution details
        /// </summary>
        /// <param name="portfolioName">Portfolio name to query</param>
        /// <param name="onlyActiveLegs">If true, only return active legs</param>
        /// <param name="user">User identifier (e.g., "SIM1"). If blank, first user of portfolio is used.</param>
        public async Task<List<StoxxoUserLeg>> GetUserLegs(string portfolioName, bool onlyActiveLegs = false, string user = "SIM1")
        {
            try
            {
                var queryParams = new Dictionary<string, string>
                {
                    { "OptionPortfolioName", portfolioName }
                };

                // Add User parameter - defaults to SIM1 per user requirement
                if (!string.IsNullOrEmpty(user))
                    queryParams["User"] = user;

                if (onlyActiveLegs)
                    queryParams["OnlyActiveLegs"] = "true";

                string url = BuildUrl("GetUserLegs", queryParams);
                Logger.Debug($"[StoxxoService] GetUserLegs: {url}");

                var response = await _httpClient.GetStringAsync(url);
                var result = JsonConvert.DeserializeObject<StoxxoResponse>(response);

                if (result != null && result.IsSuccess && !string.IsNullOrEmpty(result.response))
                {
                    // Legs are separated by tilde (~), each leg's fields separated by pipe (|)
                    // Per Stoxxo API docs: "Pipeline separated values with each leg details separated by a tilde (~)"
                    var legs = new List<StoxxoUserLeg>();

                    var legStrings = result.response.Split(new[] { '~' }, StringSplitOptions.RemoveEmptyEntries);
                    Logger.Info($"[StoxxoService] GetUserLegs parsing {legStrings.Length} leg strings from response");

                    foreach (var legStr in legStrings)
                    {
                        if (string.IsNullOrWhiteSpace(legStr))
                            continue;

                        var leg = StoxxoUserLeg.Parse(legStr.Trim());
                        if (leg != null)
                        {
                            legs.Add(leg);
                            Logger.Debug($"[StoxxoService] Parsed leg: LegID={leg.LegID}, Ins={leg.Instrument}, Txn={leg.Txn}, EntryQty={leg.EntryFilledQty}, AvgEntry={leg.AvgEntryPrice}, Status={leg.Status}");
                        }
                        else
                        {
                            Logger.Warn($"[StoxxoService] Failed to parse leg string: {legStr.Substring(0, Math.Min(100, legStr.Length))}...");
                        }
                    }

                    Logger.Debug($"[StoxxoService] GetUserLegs returned {legs.Count} legs");
                    return legs;
                }

                Logger.Warn($"[StoxxoService] GetUserLegs failed: {result?.error ?? "No data"}");
                return new List<StoxxoUserLeg>();
            }
            catch (Exception ex)
            {
                Logger.Error($"[StoxxoService] GetUserLegs error: {ex.Message}", ex);
                return new List<StoxxoUserLeg>();
            }
        }

        /// <summary>
        /// Modify portfolio settings (e.g., change SL)
        /// </summary>
        /// <param name="portfolioName">Portfolio name from PlaceMultiLegOrder</param>
        /// <param name="optField">Field to modify: CombinedSL, CombinedTgt, LegSL, LegTgt</param>
        /// <param name="data">New value (e.g., "0" or "20P")</param>
        /// <param name="leg">Leg identifier (e.g., "INS:CE" for all CE legs), required for leg-level fields</param>
        public async Task<bool> ModifyPortfolio(string portfolioName, string optField, string data, string leg = null)
        {
            try
            {
                var queryParams = new Dictionary<string, string>
                {
                    { "OptionPortfolioName", portfolioName },
                    { "OptField", optField },
                    { "Data", data }
                };

                if (!string.IsNullOrEmpty(leg))
                    queryParams["Leg"] = leg;

                string url = BuildUrl("ModifyPortfolio", queryParams);
                Logger.Info($"[StoxxoService] ModifyPortfolio: {url}");

                var response = await _httpClient.GetStringAsync(url);
                Logger.Info($"[StoxxoService] ModifyPortfolio response: {response}");

                // Response is "true" or "false" for bool functions
                if (bool.TryParse(response.Trim().ToLower(), out bool result))
                    return result;

                // Also check JSON response format
                var jsonResult = JsonConvert.DeserializeObject<StoxxoResponse>(response);
                return jsonResult?.IsSuccess ?? false;
            }
            catch (Exception ex)
            {
                Logger.Error($"[StoxxoService] ModifyPortfolio error: {ex.Message}", ex);
                return false;
            }
        }

        /// <summary>
        /// Exit entire portfolio
        /// </summary>
        public async Task<bool> ExitMultiLegOrder(string portfolioName)
        {
            try
            {
                var queryParams = new Dictionary<string, string>
                {
                    { "OptionPortfolioName", portfolioName }
                };

                string url = BuildUrl("ExitMultiLegOrder", queryParams);
                Logger.Info($"[StoxxoService] ExitMultiLegOrder: {url}");

                var response = await _httpClient.GetStringAsync(url);
                Logger.Info($"[StoxxoService] ExitMultiLegOrder response: {response}");

                if (bool.TryParse(response.Trim().ToLower(), out bool result))
                    return result;

                var jsonResult = JsonConvert.DeserializeObject<StoxxoResponse>(response);
                return jsonResult?.IsSuccess ?? false;
            }
            catch (Exception ex)
            {
                Logger.Error($"[StoxxoService] ExitMultiLegOrder error: {ex.Message}", ex);
                return false;
            }
        }

        /// <summary>
        /// Get portfolio status
        /// </summary>
        public async Task<StoxxoPortfolioStatus> GetPortfolioStatus(string portfolioName)
        {
            try
            {
                var queryParams = new Dictionary<string, string>
                {
                    { "OptionPortfolioName", portfolioName }
                };

                string url = BuildUrl("PortfolioStatus", queryParams);
                Logger.Debug($"[StoxxoService] PortfolioStatus: {url}");

                var response = await _httpClient.GetStringAsync(url);

                // Response is the status string directly or JSON with status
                var statusStr = response.Trim().Trim('"');

                // Try JSON parse first
                try
                {
                    var jsonResult = JsonConvert.DeserializeObject<StoxxoResponse>(response);
                    if (jsonResult?.IsSuccess == true)
                        statusStr = jsonResult.response;
                }
                catch { }

                return StoxxoHelper.ParseStatus(statusStr);
            }
            catch (Exception ex)
            {
                Logger.Error($"[StoxxoService] PortfolioStatus error: {ex.Message}", ex);
                return StoxxoPortfolioStatus.Unknown;
            }
        }

        /// <summary>
        /// Get portfolio MTM (Mark-to-Market P&L)
        /// </summary>
        public async Task<decimal> GetPortfolioMTM(string portfolioName)
        {
            try
            {
                var queryParams = new Dictionary<string, string>
                {
                    { "OptionPortfolioName", portfolioName }
                };

                string url = BuildUrl("PortfolioMTM", queryParams);
                Logger.Debug($"[StoxxoService] PortfolioMTM: {url}");

                var response = await _httpClient.GetStringAsync(url);

                // Response is a float value directly or JSON
                var mtmStr = response.Trim().Trim('"');

                // Try JSON parse first
                try
                {
                    var jsonResult = JsonConvert.DeserializeObject<StoxxoResponse>(response);
                    if (jsonResult?.IsSuccess == true)
                        mtmStr = jsonResult.response;
                }
                catch { }

                if (decimal.TryParse(mtmStr, out decimal mtm))
                    return mtm;

                return 0;
            }
            catch (Exception ex)
            {
                Logger.Error($"[StoxxoService] PortfolioMTM error: {ex.Message}", ex);
                return 0;
            }
        }

        /// <summary>
        /// Get combined premium of portfolio
        /// </summary>
        public async Task<decimal> GetCombinedPremium(string portfolioName)
        {
            try
            {
                var queryParams = new Dictionary<string, string>
                {
                    { "OptionPortfolioName", portfolioName }
                };

                string url = BuildUrl("CombinedPremium", queryParams);
                Logger.Debug($"[StoxxoService] CombinedPremium: {url}");

                var response = await _httpClient.GetStringAsync(url);
                var premiumStr = response.Trim().Trim('"');

                try
                {
                    var jsonResult = JsonConvert.DeserializeObject<StoxxoResponse>(response);
                    if (jsonResult?.IsSuccess == true)
                        premiumStr = jsonResult.response;
                }
                catch { }

                if (decimal.TryParse(premiumStr, out decimal premium))
                    return premium;

                return 0;
            }
            catch (Exception ex)
            {
                Logger.Error($"[StoxxoService] CombinedPremium error: {ex.Message}", ex);
                return 0;
            }
        }

        /// <summary>
        /// Square off specific leg(s)
        /// </summary>
        /// <param name="portfolioName">Portfolio name</param>
        /// <param name="legFilter">Leg identifier (e.g., "INS:CE", "Strike:25000|Txn:SELL|Ins:CE")</param>
        public async Task<bool> SqOffLeg(string portfolioName, string legFilter)
        {
            try
            {
                var queryParams = new Dictionary<string, string>
                {
                    { "OptionPortfolioName", portfolioName },
                    { "Leg", legFilter }
                };

                string url = BuildUrl("SqOffLeg", queryParams);
                Logger.Info($"[StoxxoService] SqOffLeg: {url}");

                var response = await _httpClient.GetStringAsync(url);
                Logger.Info($"[StoxxoService] SqOffLeg response: {response}");

                if (bool.TryParse(response.Trim().ToLower(), out bool result))
                    return result;

                var jsonResult = JsonConvert.DeserializeObject<StoxxoResponse>(response);
                return jsonResult?.IsSuccess ?? false;
            }
            catch (Exception ex)
            {
                Logger.Error($"[StoxxoService] SqOffLeg error: {ex.Message}", ex);
                return false;
            }
        }

        #endregion

        #region Helper Methods

        private string BuildUrl(string endpoint, Dictionary<string, string> queryParams)
        {
            var baseUrl = Config.BaseUrl.TrimEnd('/');
            var query = string.Join("&", queryParams.Select(kvp =>
                $"{HttpUtility.UrlEncode(kvp.Key)}={HttpUtility.UrlEncode(kvp.Value)}"));

            return $"{baseUrl}/{endpoint}?{query}";
        }

        #endregion
    }
}
