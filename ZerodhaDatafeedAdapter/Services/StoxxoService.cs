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

                // Log to both Logger and TBSLogger for diagnostic purposes
                Logger.Info($"[StoxxoService] GetUserLegs REQUEST: {url}");
                Logging.TBSLogger.Info($"[StoxxoService] GetUserLegs REQUEST: {url}");

                var response = await _httpClient.GetStringAsync(url);

                // Log raw response for debugging
                Logger.Info($"[StoxxoService] GetUserLegs RAW RESPONSE: {response}");
                Logging.TBSLogger.Info($"[StoxxoService] GetUserLegs RAW RESPONSE: {response}");

                var result = JsonConvert.DeserializeObject<StoxxoResponse>(response);

                if (result != null && result.IsSuccess && !string.IsNullOrEmpty(result.response))
                {
                    // Log the parsed response field specifically
                    Logger.Info($"[StoxxoService] GetUserLegs PARSED response field: {result.response}");
                    Logging.TBSLogger.Info($"[StoxxoService] GetUserLegs PARSED response field: {result.response}");

                    // Parse legs - try tilde-separated first, then fall back to field-count parsing
                    var legs = ParseUserLegsResponse(result.response);

                    Logger.Info($"[StoxxoService] GetUserLegs returned {legs.Count} legs total");
                    Logging.TBSLogger.Info($"[StoxxoService] GetUserLegs SUMMARY: {legs.Count} legs parsed successfully");
                    return legs;
                }

                string errorMsg = result?.error ?? "No data";
                Logger.Warn($"[StoxxoService] GetUserLegs failed: {errorMsg}");
                Logging.TBSLogger.Warn($"[StoxxoService] GetUserLegs FAILED: IsSuccess={result?.IsSuccess}, Error={errorMsg}");
                return new List<StoxxoUserLeg>();
            }
            catch (Exception ex)
            {
                Logger.Error($"[StoxxoService] GetUserLegs error: {ex.Message}", ex);
                Logging.TBSLogger.Error($"[StoxxoService] GetUserLegs EXCEPTION: {ex.Message}");
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

        /// <summary>
        /// Parse GetUserLegs response - handles both tilde-separated and concatenated formats.
        /// Per Stoxxo API docs, legs should be separated by tilde (~), but some versions omit it
        /// AND concatenate the last field of one leg with the SNO of the next leg (no separator).
        /// Example: "...0|0|01|1038|NIFTY|..." where "01" is actually "0" (TrailSL) + "1" (SNO of next leg)
        /// </summary>
        private List<StoxxoUserLeg> ParseUserLegsResponse(string response)
        {
            var legs = new List<StoxxoUserLeg>();

            // First try: Split by tilde (~) as per documentation
            var legStrings = response.Split(new[] { '~' }, StringSplitOptions.RemoveEmptyEntries);

            if (legStrings.Length > 1)
            {
                // Tilde-separated format - standard parsing
                Logging.TBSLogger.Info($"[StoxxoService] ParseUserLegsResponse: Using TILDE separator, found {legStrings.Length} leg strings");

                int legIndex = 0;
                foreach (var legStr in legStrings)
                {
                    if (string.IsNullOrWhiteSpace(legStr))
                        continue;

                    Logging.TBSLogger.Info($"[StoxxoService] ParseUserLegsResponse LEG[{legIndex}] RAW: {legStr}");

                    var leg = StoxxoUserLeg.Parse(legStr.Trim());
                    if (leg != null)
                    {
                        legs.Add(leg);
                        LogParsedLeg(legIndex, leg);
                    }
                    else
                    {
                        Logging.TBSLogger.Warn($"[StoxxoService] ParseUserLegsResponse: Failed to parse LEG[{legIndex}]");
                    }
                    legIndex++;
                }
            }
            else
            {
                // No tilde found - use regex-based parsing to find leg boundaries
                // Each leg starts with SNO (single digit 1-9) followed by |LegID| where LegID is a number
                // Pattern: Start of string or after a digit, find digit|4-5 digit number|NIFTY or BANKNIFTY
                legs = ParseConcatenatedUserLegs(response);
            }

            return legs;
        }

        /// <summary>
        /// Parse concatenated legs where there's no tilde separator.
        /// Uses the robust pattern: LegID|Symbol where LegID is 4+ digits and Symbol is NIFTY/SENSEX/BANKNIFTY/FINNIFTY.
        /// The SNO before LegID may be concatenated with the previous leg's last field (e.g., "35.752|1059|NIFTY").
        /// </summary>
        private List<StoxxoUserLeg> ParseConcatenatedUserLegs(string response)
        {
            var legs = new List<StoxxoUserLeg>();

            // Split into all parts first
            var allParts = response.Split('|');
            Logging.TBSLogger.Info($"[StoxxoService] ParseConcatenatedUserLegs: Total fields={allParts.Length}");

            // Find leg boundaries by looking for pattern: LegID|Symbol
            // LegID is a 4+ digit Stoxxo internal ID (e.g., 1059, 1060)
            // Symbol is NIFTY/BANKNIFTY/SENSEX/FINNIFTY
            // The index we find is the LegID position, so leg starts 1 position before (at SNO)
            var legIdIndices = new List<int>();

            for (int i = 0; i < allParts.Length - 1; i++)
            {
                string part = allParts[i];
                string nextPart = allParts[i + 1];

                // Check for LegID|Symbol pattern
                // LegID: 4+ digit number (Stoxxo IDs are typically 1000+)
                // Symbol: NIFTY, BANKNIFTY, SENSEX, FINNIFTY
                if (int.TryParse(part, out int legId) && legId >= 1000 && legId <= 99999 &&
                    IsValidSymbol(nextPart))
                {
                    legIdIndices.Add(i);
                    Logging.TBSLogger.Info($"[StoxxoService] ParseConcatenatedUserLegs: Found LegID|Symbol at index {i}: LegID={part}, Symbol={nextPart}");
                }
            }

            Logging.TBSLogger.Info($"[StoxxoService] ParseConcatenatedUserLegs: Found {legIdIndices.Count} legs by LegID|Symbol pattern");

            if (legIdIndices.Count == 0)
            {
                Logging.TBSLogger.Warn($"[StoxxoService] ParseConcatenatedUserLegs: No legs found!");
                return legs;
            }

            // Each leg in GetUserLegs has 26 fields:
            // SNO|LegID|Symbol|Expiry|Strike|Instrument|Txn|Lot|Target|SL|IV|Delta|Theta|Vega|LTP|PNL|PNLPerLot|EntryQty|AvgEntry|ExitQty|AvgExit|Status|Target|SL|LockedTgt|TrailSL
            // LegID is at position 1 (0-indexed), so SNO is at position 0
            // This means leg starts 1 position before the LegID index we found

            for (int legIdx = 0; legIdx < legIdIndices.Count; legIdx++)
            {
                int legIdPos = legIdIndices[legIdx];

                // The SNO field is 1 position before LegID
                // But it might be concatenated with the previous leg's TrailSL field
                int snoPos = legIdPos - 1;

                // Determine the end of this leg (start of next leg's SNO, or end of array)
                int nextLegSnoPos = legIdx < legIdIndices.Count - 1 ? legIdIndices[legIdx + 1] - 1 : allParts.Length;

                // Build leg parts
                var legParts = new List<string>();

                // Handle SNO - it might be concatenated with previous field
                string snoPart = allParts[snoPos];
                if (legIdx == 0)
                {
                    // First leg - SNO is clean (just the first field)
                    legParts.Add(snoPart);
                }
                else
                {
                    // Subsequent legs - SNO is the last digit of the previous leg's TrailSL
                    // The field looks like "35.752" where "2" is the SNO
                    // Or "0|04" where the SNO is at a separate position but may have leading content
                    // Extract just the last character as SNO if the field has multiple chars ending with digit
                    if (snoPart.Length >= 1)
                    {
                        char lastChar = snoPart[snoPart.Length - 1];
                        if (char.IsDigit(lastChar))
                        {
                            legParts.Add(lastChar.ToString());
                            Logging.TBSLogger.Info($"[StoxxoService] ParseConcatenatedUserLegs LEG[{legIdx}]: Extracted SNO='{lastChar}' from concatenated field '{snoPart}'");
                        }
                        else
                        {
                            legParts.Add(snoPart);
                        }
                    }
                }

                // Add LegID and remaining fields
                for (int j = legIdPos; j < nextLegSnoPos; j++)
                {
                    string partToAdd = allParts[j];

                    // For the last field of this leg (TrailSL), we need to strip the next leg's SNO if concatenated
                    if (j == nextLegSnoPos - 1 && legIdx < legIdIndices.Count - 1)
                    {
                        // Check if there's a trailing digit that's the next leg's SNO
                        // TrailSL is typically "0" or a decimal like "35.75", next SNO is a single digit 1-9
                        // Combined it might be "01" or "35.752"
                        if (partToAdd.Length >= 2)
                        {
                            char lastChar = partToAdd[partToAdd.Length - 1];
                            if (lastChar >= '1' && lastChar <= '9')
                            {
                                // Strip the last digit (next leg's SNO)
                                string stripped = partToAdd.Substring(0, partToAdd.Length - 1);
                                Logging.TBSLogger.Info($"[StoxxoService] ParseConcatenatedUserLegs LEG[{legIdx}]: Stripped SNO from TrailSL: '{partToAdd}' -> '{stripped}'");
                                partToAdd = stripped;
                            }
                        }
                    }

                    legParts.Add(partToAdd);
                }

                string legStr = string.Join("|", legParts);
                Logging.TBSLogger.Info($"[StoxxoService] ParseConcatenatedUserLegs LEG[{legIdx}] parts={legParts.Count}: {(legStr.Length > 150 ? legStr.Substring(0, 150) + "..." : legStr)}");

                var leg = StoxxoUserLeg.Parse(legStr);
                if (leg != null)
                {
                    legs.Add(leg);
                    LogParsedLeg(legIdx, leg);
                }
                else
                {
                    Logging.TBSLogger.Warn($"[StoxxoService] ParseConcatenatedUserLegs: Failed to parse LEG[{legIdx}], legStr={legStr}");
                }
            }

            return legs;
        }

        private bool IsValidSymbol(string symbol)
        {
            if (string.IsNullOrEmpty(symbol)) return false;
            var upper = symbol.ToUpperInvariant();
            return upper == "NIFTY" || upper == "BANKNIFTY" || upper == "SENSEX" || upper == "FINNIFTY";
        }

        private void LogParsedLeg(int index, StoxxoUserLeg leg)
        {
            string legDetails = $"LegID={leg.LegID}, Ins={leg.Instrument}, Txn={leg.Txn}, Strike={leg.Strike}, " +
                              $"EntryQty={leg.EntryFilledQty}, AvgEntry={leg.AvgEntryPrice}, " +
                              $"ExitQty={leg.ExitFilledQty}, AvgExit={leg.AvgExitPrice}, Status={leg.Status}";
            Logger.Info($"[StoxxoService] Parsed LEG[{index}]: {legDetails}");
            Logging.TBSLogger.Info($"[StoxxoService] Parsed LEG[{index}]: {legDetails}");
        }

        #endregion
    }
}
