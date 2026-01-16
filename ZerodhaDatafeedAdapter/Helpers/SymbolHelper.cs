using System;
using System.Linq;

namespace ZerodhaDatafeedAdapter.Helpers
{
    /// <summary>
    /// Centralized helper for symbol classification and validation.
    /// Consolidates IsIndexSymbol logic from ZerodhaAdapter and MarketDataService.
    /// </summary>
    public static class SymbolHelper
    {
        /// <summary>
        /// Known index symbols that have no volume (they're calculated indices).
        /// These symbols require special handling as they don't have trade volume,
        /// only price updates.
        /// </summary>
        private static readonly string[] IndexSymbols = new[]
        {
            "GIFT_NIFTY",
            "GIFT NIFTY",
            "NIFTY 50",
            "NIFTY",
            "SENSEX",
            "NIFTY BANK",
            "BANKNIFTY",
            "FINNIFTY",
            "MIDCPNIFTY"
        };

        /// <summary>
        /// Determines if a symbol represents an index (no volume, price-only updates).
        /// Index symbols include NIFTY, SENSEX, BANKNIFTY, GIFT NIFTY, etc.
        /// </summary>
        /// <param name="symbolName">The symbol name to check</param>
        /// <returns>True if the symbol is a known index, false otherwise</returns>
        public static bool IsIndexSymbol(string symbolName)
        {
            if (string.IsNullOrEmpty(symbolName))
                return false;

            string upperSymbol = symbolName.ToUpperInvariant();

            // Check against known index symbols
            foreach (var index in IndexSymbols)
            {
                if (upperSymbol == index)
                    return true;
            }

            // Also check for _SPOT suffix (e.g., NIFTY_SPOT, SENSEX_SPOT)
            if (upperSymbol.EndsWith("_SPOT"))
                return true;

            return false;
        }

        /// <summary>
        /// Determines if a symbol belongs to an Indian market instrument.
        /// Checks for NSE/BSE exchange indicators in the symbol name.
        /// </summary>
        /// <param name="symbolName">The symbol name to check</param>
        /// <returns>True if the symbol appears to be from Indian markets</returns>
        public static bool IsIndianMarketSymbol(string symbolName)
        {
            if (string.IsNullOrEmpty(symbolName))
                return false;

            return symbolName.EndsWith("-NSE") || symbolName.EndsWith("-BSE");
        }

        /// <summary>
        /// Determines if a symbol is an MCX (Multi Commodity Exchange) symbol.
        /// MCX symbols include crude oil, gold, silver, natural gas, copper, etc.
        /// </summary>
        /// <param name="symbolName">The symbol name to check</param>
        /// <returns>True if the symbol is from MCX, false otherwise</returns>
        public static bool IsMcxSymbol(string symbolName)
        {
            if (string.IsNullOrEmpty(symbolName))
                return false;

            return symbolName.StartsWith("CRUDEOIL", StringComparison.OrdinalIgnoreCase) ||
                   symbolName.StartsWith("GOLD", StringComparison.OrdinalIgnoreCase) ||
                   symbolName.StartsWith("SILVER", StringComparison.OrdinalIgnoreCase) ||
                   symbolName.StartsWith("NATURALGAS", StringComparison.OrdinalIgnoreCase) ||
                   symbolName.StartsWith("COPPER", StringComparison.OrdinalIgnoreCase);
        }

        #region Option Symbol Generation

        /// <summary>
        /// Build an option trading symbol in the correct Zerodha format.
        /// This is the SINGLE SOURCE OF TRUTH for option symbol generation.
        ///
        /// Format depends on expiry type:
        /// - Monthly Expiry: UNDERLYING + YY + MMM + STRIKE + TYPE (e.g., SENSEX26JAN85000CE)
        /// - Weekly Expiry (Jan-Sep): UNDERLYING + YY + M + DD + STRIKE + TYPE (e.g., SENSEX2610185000CE where M=1-9)
        /// - Weekly Expiry (Oct-Dec): UNDERLYING + YY + X + DD + STRIKE + TYPE (e.g., NIFTY25O0324700CE where X=O/N/D)
        /// </summary>
        /// <param name="underlying">Underlying symbol (e.g., NIFTY, SENSEX, BANKNIFTY)</param>
        /// <param name="expiry">Expiry date</param>
        /// <param name="strike">Strike price</param>
        /// <param name="optionType">Option type: CE or PE</param>
        /// <param name="isMonthlyExpiry">Whether this is a monthly expiry (last expiry of the month)</param>
        /// <returns>The formatted option symbol</returns>
        public static string BuildOptionSymbol(string underlying, DateTime expiry, decimal strike, string optionType, bool isMonthlyExpiry)
        {
            if (isMonthlyExpiry)
            {
                // Monthly format: UNDERLYING + YY + MMM + STRIKE + TYPE
                // Example: NIFTY25DEC24700CE, SENSEX26JAN85000CE
                string monthAbbr = expiry.ToString("MMM").ToUpper();
                return $"{underlying}{expiry:yy}{monthAbbr}{strike:F0}{optionType}";
            }
            else
            {
                // Weekly format depends on month
                int month = expiry.Month;
                int day = expiry.Day;
                int year = expiry.Year % 100; // 2-digit year

                string monthIndicator;
                if (month >= 1 && month <= 9)
                {
                    // Jan-Sep: Use single digit 1-9
                    // Example: SENSEX2610185000CE (Jan 01)
                    monthIndicator = month.ToString();
                }
                else
                {
                    // Oct-Dec: Use O, N, D respectively
                    // Month 10=O, 11=N, 12=D
                    monthIndicator = month switch
                    {
                        10 => "O",
                        11 => "N",
                        12 => "D",
                        _ => month.ToString() // Fallback
                    };
                }

                // Weekly format: UNDERLYING + YY + M + DD + STRIKE + TYPE
                // Day is always 2 digits (padded with 0 if needed)
                return $"{underlying}{year}{monthIndicator}{day:D2}{strike:F0}{optionType}";
            }
        }

        /// <summary>
        /// Determines if an expiry date is a monthly expiry.
        /// A monthly expiry is the last expiry (typically Thursday) of a given month.
        /// </summary>
        /// <param name="expiry">The expiry date to check</param>
        /// <param name="allExpiries">List of all available expiries for the underlying</param>
        /// <returns>True if this is a monthly expiry, false for weekly</returns>
        public static bool IsMonthlyExpiry(DateTime expiry, System.Collections.Generic.List<DateTime> allExpiries)
        {
            if (allExpiries == null || allExpiries.Count == 0)
            {
                // If no expiry list provided, assume weekly (safer default)
                return false;
            }

            // Get all expiries in the same month as the target expiry
            var sameMonthExpiries = allExpiries
                .Where(e => e.Year == expiry.Year && e.Month == expiry.Month)
                .OrderBy(e => e)
                .ToList();

            // If this is the last (or only) expiry in the month, it's monthly
            if (sameMonthExpiries.Count == 0)
                return false;

            return expiry.Date == sameMonthExpiries.Last().Date;
        }

        /// <summary>
        /// Determines if a given expiry is a monthly expiry (last Thursday of month).
        /// Overload that accepts string-formatted expiries.
        /// </summary>
        public static bool IsMonthlyExpiry(DateTime expiry, System.Collections.Generic.List<string> allExpiryStrings)
        {
            if (allExpiryStrings == null || allExpiryStrings.Count == 0)
                return false;

            var allExpiries = allExpiryStrings
                .Select(s => DateTime.TryParse(s, out var dt) ? dt : (DateTime?)null)
                .Where(d => d.HasValue)
                .Select(d => d.Value)
                .ToList();

            return IsMonthlyExpiry(expiry, allExpiries);
        }

        /// <summary>
        /// Get lot size for an underlying index.
        /// First tries to get from cached values (loaded from instrument masters DB),
        /// then falls back to hardcoded defaults.
        /// </summary>
        public static int GetLotSize(string underlying)
        {
            if (string.IsNullOrEmpty(underlying)) return 25;

            // First try to get from InstrumentManager (source of truth)
            try
            {
                int dbLotSize = Services.Instruments.InstrumentManager.Instance.GetLotSizeForUnderlying(underlying);
                if (dbLotSize > 0)
                {
                    return dbLotSize;
                }
            }
            catch
            {
                // Ignore - fall through to defaults
            }

            // Fallback to hardcoded defaults (may be outdated - DB values are preferred)
            return underlying.ToUpperInvariant() switch
            {
                "NIFTY" => 25,
                "BANKNIFTY" => 15,
                "SENSEX" => 20,  // Updated from 10 to 20 as of Jan 2026
                "FINNIFTY" => 25,
                "MIDCPNIFTY" => 50,
                _ => 25  // Default to NIFTY lot size
            };
        }

        #endregion

        #region Strike Extraction

        /// <summary>
        /// Known option underlyings for strike extraction.
        /// Ordered by length (longest first) to match correctly.
        /// </summary>
        private static readonly string[] KnownOptionUnderlyings = new[]
        {
            "MIDCPNIFTY", // 10 chars
            "BANKNIFTY",  // 9 chars
            "FINNIFTY",   // 8 chars
            "SENSEX",     // 6 chars
            "NIFTY",      // 5 chars
            "BANKEX"      // 6 chars
        };

        /// <summary>
        /// Extract strike price from an option symbol.
        /// Works with Zerodha format options:
        /// - Monthly: NIFTY25JAN24700CE (underlying + YY + MMM + strike + CE/PE)
        /// - Weekly: SENSEX2610884800CE (underlying + YY + M + DD + strike + CE/PE)
        /// </summary>
        /// <param name="symbol">The option symbol</param>
        /// <param name="strike">Output strike price if found</param>
        /// <returns>True if strike was successfully extracted</returns>
        public static bool TryExtractStrike(string symbol, out decimal strike)
        {
            strike = 0;
            if (string.IsNullOrEmpty(symbol)) return false;

            try
            {
                string upperSymbol = symbol.ToUpperInvariant();

                // Find option type suffix (CE or PE)
                int optTypeIdx = upperSymbol.LastIndexOf("CE");
                if (optTypeIdx < 0) optTypeIdx = upperSymbol.LastIndexOf("PE");
                if (optTypeIdx < 0) return false;

                // Find which underlying this symbol belongs to
                string underlying = null;
                foreach (var u in KnownOptionUnderlyings)
                {
                    if (upperSymbol.StartsWith(u))
                    {
                        underlying = u;
                        break;
                    }
                }

                if (underlying == null)
                {
                    // Unknown underlying - fall back to extracting all digits before CE/PE
                    // This may include date digits but is better than nothing
                    string strikeStr = "";
                    for (int i = optTypeIdx - 1; i >= 0 && char.IsDigit(symbol[i]); i--)
                    {
                        strikeStr = symbol[i] + strikeStr;
                    }
                    return decimal.TryParse(strikeStr, out strike);
                }

                // Extract the part between underlying and CE/PE
                string middle = upperSymbol.Substring(underlying.Length, optTypeIdx - underlying.Length);

                // Check if this is monthly format (contains month abbreviation like JAN, FEB, etc.)
                bool isMonthly = ContainsMonthAbbreviation(middle);

                if (isMonthly)
                {
                    // Monthly format: YY + MMM + STRIKE
                    // Find where the month abbreviation ends (after 3 letters)
                    int monthEnd = FindMonthAbbreviationEnd(middle);
                    if (monthEnd > 0 && monthEnd < middle.Length)
                    {
                        string strikeStr = middle.Substring(monthEnd);
                        return decimal.TryParse(strikeStr, out strike);
                    }
                }
                else
                {
                    // Weekly format: YY + M + DD + STRIKE (where M is 1-9 or O/N/D)
                    // Format is: 2 digits (year) + 1 char (month) + 2 digits (day) + strike
                    // Total prefix before strike is 5 characters (e.g., "26108" for Jan 8, 2026)
                    if (middle.Length > 5)
                    {
                        string strikeStr = middle.Substring(5); // Skip YYMDD
                        return decimal.TryParse(strikeStr, out strike);
                    }
                }

                return false;
            }
            catch
            {
                return false;
            }
        }

        /// <summary>
        /// Check if a string contains a month abbreviation (JAN, FEB, etc.).
        /// </summary>
        private static bool ContainsMonthAbbreviation(string text)
        {
            string[] months = { "JAN", "FEB", "MAR", "APR", "MAY", "JUN", "JUL", "AUG", "SEP", "OCT", "NOV", "DEC" };
            foreach (var month in months)
            {
                if (text.Contains(month)) return true;
            }
            return false;
        }

        /// <summary>
        /// Find the end index of the month abbreviation in a string.
        /// Returns the index after the month (where strike begins).
        /// </summary>
        private static int FindMonthAbbreviationEnd(string text)
        {
            string[] months = { "JAN", "FEB", "MAR", "APR", "MAY", "JUN", "JUL", "AUG", "SEP", "OCT", "NOV", "DEC" };
            foreach (var month in months)
            {
                int idx = text.IndexOf(month, StringComparison.Ordinal);
                if (idx >= 0) return idx + 3; // Return position after month abbreviation
            }
            return -1;
        }

        /// <summary>
        /// Extract the option type (CE or PE) from an option symbol.
        /// </summary>
        /// <param name="symbol">The option symbol</param>
        /// <returns>"CE", "PE", or null if not found</returns>
        public static string GetOptionType(string symbol)
        {
            if (string.IsNullOrEmpty(symbol)) return null;

            if (symbol.EndsWith("CE")) return "CE";
            if (symbol.EndsWith("PE")) return "PE";
            return null;
        }

        #endregion
    }
}
