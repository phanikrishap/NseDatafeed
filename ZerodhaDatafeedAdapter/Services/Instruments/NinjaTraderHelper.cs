using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using NinjaTrader.Cbi;

namespace ZerodhaDatafeedAdapter.Services.Instruments
{
    /// <summary>
    /// Helper for interacting with NinjaTrader's internal types via reflection
    /// when direct references are unstable or unavailable.
    /// </summary>
    public static class NinjaTraderHelper
    {
        private static Type _tradingHoursType;
        private static PropertyInfo _allTradingHoursProperty;

        /// <summary>
        /// Attempts to set the trading hours for a MasterInstrument by name.
        /// </summary>
        public static bool SetTradingHours(MasterInstrument masterInstrument, string templateName)
        {
            try
            {
                if (_tradingHoursType == null)
                {
                    _tradingHoursType = AppDomain.CurrentDomain.GetAssemblies()
                        .SelectMany(a => a.GetTypes())
                        .FirstOrDefault(t => t.Name == "TradingHours" && (t.Namespace?.Contains("NinjaTrader") ?? false));
                }

                if (_tradingHoursType == null) return false;

                if (_allTradingHoursProperty == null)
                {
                    _allTradingHoursProperty = _tradingHoursType.GetProperty("All", BindingFlags.Public | BindingFlags.Static);
                }

                var allTemplates = _allTradingHoursProperty?.GetValue(null) as IList;
                if (allTemplates == null) return false;

                object selectedTemplate = null;
                foreach (var template in allTemplates)
                {
                    var nameProp = template.GetType().GetProperty("Name");
                    if (nameProp?.GetValue(template)?.ToString() == templateName)
                    {
                        selectedTemplate = template;
                        break;
                    }
                }

                // Fallback to first if not found
                if (selectedTemplate == null && allTemplates.Count > 0)
                    selectedTemplate = allTemplates[0];

                if (selectedTemplate != null)
                {
                    var thProp = typeof(MasterInstrument).GetProperty("TradingHours");
                    thProp?.SetValue(masterInstrument, selectedTemplate);
                    return true;
                }
            }
            catch (Exception ex)
            {
                Logger.Error($"NinjaTraderHelper: Error setting trading hours to '{templateName}': {ex.Message}");
            }
            return false;
        }

        /// <summary>
        /// Gets the most appropriate trading hours template name for a segment and symbol.
        /// </summary>
        public static string GetTradingHoursTemplate(string segment, string symbol = null)
        {
            // Special handling for specific symbols
            if (!string.IsNullOrEmpty(symbol))
            {
                string upperSymbol = symbol.ToUpperInvariant();

                // GIFT NIFTY trades 24/7 (NSE IFSC)
                if (upperSymbol.Contains("GIFT") || upperSymbol.Contains("NSE_IFSC"))
                    return "Default 24 x 7";

                // Indian indices (NIFTY, SENSEX, BANKNIFTY, etc.) use NSE hours
                if (upperSymbol == "NIFTY" || upperSymbol == "NIFTY 50" ||
                    upperSymbol == "SENSEX" || upperSymbol == "BANKNIFTY" ||
                    upperSymbol == "FINNIFTY" || upperSymbol == "MIDCPNIFTY")
                    return "Nse";
            }

            // Segment-based logic
            if (string.IsNullOrEmpty(segment)) return "Nse"; // Default to Nse for Indian market

            segment = segment.ToUpperInvariant();
            if (segment.Contains("NSE") || segment.Contains("NFO") || segment.Contains("BSE") || segment.Contains("BFO"))
                return "Nse";
            if (segment.Contains("MCX"))
                return "MCX";
            if (segment.Contains("IFSC"))
                return "Default 24 x 7";

            return "Nse"; // Default to Nse for Indian market
        }
    }
}
