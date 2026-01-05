using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using NinjaTrader.Cbi;
using ZerodhaDatafeedAdapter.Classes;
using ZerodhaDatafeedAdapter.Models;

namespace ZerodhaDatafeedAdapter.Services.Instruments
{
    /// <summary>
    /// Helper for interacting with NinjaTrader's internal types via reflection
    /// when direct references are unstable or unavailable.
    /// Also provides methods for creating and managing NinjaTrader instruments.
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

        /// <summary>
        /// Creates or retrieves a NinjaTrader instrument for the given definition.
        /// This is the core method that actually creates MasterInstrument and Instrument in NinjaTrader's database.
        /// MUST be called on the UI dispatcher thread.
        /// </summary>
        /// <param name="instrument">The instrument definition containing symbol and properties</param>
        /// <param name="ntSymbolName">Output: the NinjaTrader symbol name that was created/found</param>
        /// <returns>True if instrument was created or already exists, false on failure</returns>
        public static bool CreateNTInstrument(InstrumentDefinition instrument, out string ntSymbolName)
        {
            ntSymbolName = "";

            try
            {
                InstrumentType instrumentType = InstrumentType.Stock;
                string validName = instrument.Symbol;

                // Determine trading hours template based on segment and symbol
                string templateName = GetTradingHoursTemplate(instrument.Segment, validName);
                Logger.Info($"[NTHelper] CreateNTInstrument: Setting up '{validName}' with trading hours '{templateName}'");

                // Step 1: Check if MasterInstrument already exists
                MasterInstrument existingMaster = MasterInstrument.DbGet(validName, instrumentType);
                if (existingMaster != null)
                {
                    ntSymbolName = validName;
                    Logger.Debug($"[NTHelper] CreateNTInstrument: MasterInstrument '{validName}' already exists");

                    // Ensure SymbolNames mapping exists
                    if (!DataContext.Instance.SymbolNames.ContainsKey(validName))
                    {
                        DataContext.Instance.SymbolNames.Add(validName, instrument.Symbol);
                    }

                    // Ensure provider name is set at our provider index
                    List<string> providerNames = new List<string>(existingMaster.ProviderNames);
                    while (providerNames.Count <= Constants.ProviderId)
                    {
                        providerNames.Add("");
                    }

                    if (string.IsNullOrEmpty(providerNames[Constants.ProviderId]))
                    {
                        providerNames[Constants.ProviderId] = instrument.Symbol;
                        existingMaster.ProviderNames = providerNames.ToArray();

                        // Ensure trading hours are set
                        if (existingMaster.TradingHours == null)
                        {
                            SetTradingHours(existingMaster, templateName);
                        }

                        existingMaster.DbUpdate();
                        MasterInstrument.DbUpdateCache();
                        Logger.Info($"[NTHelper] CreateNTInstrument: Updated existing MasterInstrument '{validName}' with provider mapping");
                    }

                    return true;
                }

                // Step 2: Create new MasterInstrument
                Logger.Info($"[NTHelper] CreateNTInstrument: Creating new MasterInstrument '{validName}'");

                double tickSize = instrument.TickSize > 0 ? instrument.TickSize : 0.05;

                MasterInstrument newMaster = new MasterInstrument()
                {
                    Description = instrument.Symbol,
                    InstrumentType = instrumentType,
                    Name = validName,
                    PointValue = 1.0,
                    TickSize = tickSize,
                    Url = new Uri("https://kite.zerodha.com"),
                    Exchanges = { Exchange.Default },
                    Currency = Currency.IndianRupee
                };

                // Set provider names array with our provider ID
                var providers = new string[Constants.ProviderId + 1];
                for (int i = 0; i <= Constants.ProviderId; i++)
                {
                    providers[i] = "";
                }
                providers[Constants.ProviderId] = instrument.Symbol;
                newMaster.ProviderNames = providers;

                // Set trading hours
                SetTradingHours(newMaster, templateName);

                // Add to database
                newMaster.DbAdd(false);
                Logger.Debug($"[NTHelper] CreateNTInstrument: MasterInstrument '{validName}' added to DB");

                // Step 3: Create Instrument and add to database
                new Instrument()
                {
                    Exchange = Exchange.Default,
                    MasterInstrument = newMaster
                }.DbAdd();
                Logger.Debug($"[NTHelper] CreateNTInstrument: Instrument '{validName}' added to DB");

                // Step 4: Update SymbolNames mapping
                if (!DataContext.Instance.SymbolNames.ContainsKey(validName))
                {
                    DataContext.Instance.SymbolNames.Add(validName, instrument.Symbol);
                }

                ntSymbolName = validName;
                Logger.Info($"[NTHelper] CreateNTInstrument: Successfully created NT instrument '{validName}'");
                return true;
            }
            catch (Exception ex)
            {
                Logger.Error($"[NTHelper] CreateNTInstrument: Failed to create '{instrument.Symbol}' - {ex.Message}", ex);
                return false;
            }
        }

        /// <summary>
        /// Creates a NinjaTrader instrument from a MappedInstrument (typically from index_mappings.json or F&O lookups).
        /// MUST be called on the UI dispatcher thread.
        /// </summary>
        /// <param name="mapped">The mapped instrument containing symbol and token info</param>
        /// <param name="ntSymbolName">Output: the NinjaTrader symbol name that was created/found</param>
        /// <returns>True if instrument was created or already exists, false on failure</returns>
        public static bool CreateNTInstrumentFromMapping(MappedInstrument mapped, out string ntSymbolName)
        {
            // Convert MappedInstrument to InstrumentDefinition
            var instrumentDef = new InstrumentDefinition
            {
                Symbol = mapped.zerodhaSymbol ?? mapped.symbol,
                BrokerSymbol = mapped.zerodhaSymbol ?? mapped.symbol,
                Segment = mapped.segment ?? mapped.exchange ?? "NSE",
                InstrumentToken = mapped.instrument_token,
                TickSize = mapped.tick_size > 0 ? mapped.tick_size : 0.05,
                LotSize = mapped.lot_size,
                Expiry = mapped.expiry,
                Strike = mapped.strike,
                Underlying = mapped.underlying
            };

            return CreateNTInstrument(instrumentDef, out ntSymbolName);
        }

        /// <summary>
        /// Removes a NinjaTrader instrument from the database.
        /// MUST be called on the UI dispatcher thread.
        /// </summary>
        /// <param name="symbol">The symbol name to remove</param>
        /// <returns>True if removed, false if not found or on error</returns>
        public static bool RemoveNTInstrument(string symbol)
        {
            try
            {
                InstrumentType instrumentType = InstrumentType.Stock;
                MasterInstrument masterInstrument = MasterInstrument.DbGet(symbol, instrumentType);

                if (masterInstrument == null)
                {
                    Logger.Debug($"[NTHelper] RemoveNTInstrument: '{symbol}' not found");
                    return false;
                }

                // Check if this is our instrument (by URL) - if so, remove entirely
                if (masterInstrument.Url?.AbsoluteUri == "https://kite.zerodha.com/")
                {
                    masterInstrument.DbRemove();
                    Logger.Info($"[NTHelper] RemoveNTInstrument: Removed '{symbol}' from DB");
                }
                else
                {
                    // Just clear our provider mapping
                    if (masterInstrument.ProviderNames?.ElementAtOrDefault(Constants.ProviderId) != null)
                    {
                        masterInstrument.UserData = null;
                        masterInstrument.ProviderNames[Constants.ProviderId] = "";
                        masterInstrument.DbUpdate();
                        Logger.Info($"[NTHelper] RemoveNTInstrument: Cleared provider mapping for '{symbol}'");
                    }
                }

                // Remove from SymbolNames
                if (DataContext.Instance.SymbolNames.ContainsKey(symbol))
                {
                    DataContext.Instance.SymbolNames.Remove(symbol);
                }

                return true;
            }
            catch (Exception ex)
            {
                Logger.Error($"[NTHelper] RemoveNTInstrument: Error removing '{symbol}' - {ex.Message}", ex);
                return false;
            }
        }

        /// <summary>
        /// Gets all NinjaTrader instruments that have our provider mapping set.
        /// </summary>
        /// <returns>List of instrument symbols registered with our adapter</returns>
        public static List<string> GetRegisteredInstruments()
        {
            var result = new List<string>();
            try
            {
                var instruments = MasterInstrument.All
                    .Where(x => !string.IsNullOrEmpty(x.ProviderNames?.ElementAtOrDefault(Constants.ProviderId)))
                    .OrderBy(x => x.Name);

                foreach (var mi in instruments)
                {
                    result.Add(mi.Name);
                }
            }
            catch (Exception ex)
            {
                Logger.Error($"[NTHelper] GetRegisteredInstruments: Error - {ex.Message}", ex);
            }
            return result;
        }
    }
}
