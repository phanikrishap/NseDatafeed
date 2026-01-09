using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text.RegularExpressions;
using ZerodhaDatafeedAdapter.Logging;

namespace ZerodhaDatafeedAdapter.Services.Historical
{
    /// <summary>
    /// Maps Zerodha option symbols to Accelpix format.
    ///
    /// Both Zerodha and Accelpix use the SAME format:
    /// - Weekly: {UNDERLYING}{YY}{M}{DD}{STRIKE}{CE/PE}
    ///   M = 1-9 for Jan-Sep, O=Oct, N=Nov, D=Dec
    ///   Example: NIFTY2611326000CE (NIFTY, 2026-01-13, 26000, CE)
    /// - Monthly: {UNDERLYING}{YY}{MMM}{STRIKE}{CE/PE}
    ///   MMM = 3-letter month abbreviation
    ///   Example: NIFTY26JAN26000CE (NIFTY, January 2026, 26000, CE)
    ///
    /// The ONLY difference is BSE options (SENSEX) need -BSE suffix in Accelpix:
    /// - Weekly: SENSEX2611585000CE-BSE
    /// - Monthly: SENSEX26JAN85000CE-BSE
    /// </summary>
    public static class AccelpixSymbolMapper
    {
        /// <summary>
        /// Build an Accelpix symbol directly from context (preferred method).
        /// This avoids the need to parse the symbol string.
        /// </summary>
        /// <param name="underlying">Underlying index (NIFTY, BANKNIFTY, FINNIFTY, MIDCPNIFTY, SENSEX)</param>
        /// <param name="expiry">Expiry date</param>
        /// <param name="strike">Strike price as integer (e.g., 26000)</param>
        /// <param name="optionType">CE or PE</param>
        /// <param name="isMonthlyExpiry">Whether this is a monthly expiry (last Thursday of month)</param>
        /// <returns>Accelpix symbol (e.g., NIFTY2611326000CE or NIFTY26JAN26000CE or SENSEX2611585000CE-BSE)</returns>
        public static string BuildAccelpixSymbol(string underlying, DateTime expiry, int strike, string optionType, bool isMonthlyExpiry)
        {
            if (string.IsNullOrEmpty(underlying) || string.IsNullOrEmpty(optionType))
                return null;

            try
            {
                string accelpixSymbol;

                if (isMonthlyExpiry)
                {
                    // Monthly format: {UNDERLYING}{YY}{MMM}{STRIKE}{CE/PE}
                    string monthAbbrev = MonthAbbreviations[expiry.Month];
                    accelpixSymbol = $"{underlying}{expiry:yy}{monthAbbrev}{strike}{optionType}";
                }
                else
                {
                    // Weekly format: {UNDERLYING}{YY}{M}{DD}{STRIKE}{CE/PE}
                    char monthCode = GetMonthCode(expiry.Month);
                    accelpixSymbol = $"{underlying}{expiry:yy}{monthCode}{expiry:dd}{strike}{optionType}";
                }

                // Add -BSE suffix for SENSEX
                if (underlying == "SENSEX")
                {
                    accelpixSymbol += "-BSE";
                }

                HistoricalTickLogger.LogSymbolMapping($"(built from context)", accelpixSymbol, true);
                return accelpixSymbol;
            }
            catch (Exception ex)
            {
                HistoricalTickLogger.Error($"[SYMBOL-BUILD] Error building symbol for {underlying} {expiry:dd-MMM-yy} {strike} {optionType}: {ex.Message}");
                return null;
            }
        }

        /// <summary>
        /// Get month code for weekly expiry format.
        /// </summary>
        private static char GetMonthCode(int month)
        {
            if (month <= 9)
                return month.ToString()[0];
            else if (month == 10)
                return 'O';
            else if (month == 11)
                return 'N';
            else
                return 'D';
        }

        // Month abbreviations for monthly expiry format
        private static readonly Dictionary<int, string> MonthAbbreviations = new Dictionary<int, string>
        {
            { 1, "JAN" }, { 2, "FEB" }, { 3, "MAR" }, { 4, "APR" },
            { 5, "MAY" }, { 6, "JUN" }, { 7, "JUL" }, { 8, "AUG" },
            { 9, "SEP" }, { 10, "OCT" }, { 11, "NOV" }, { 12, "DEC" }
        };

        // Known underlying symbols
        private static readonly HashSet<string> KnownUnderlyings = new HashSet<string>
        {
            "NIFTY", "BANKNIFTY", "FINNIFTY", "MIDCPNIFTY", "SENSEX"
        };

        // Zerodha month codes: 1-9 for Jan-Sep, O=Oct, N=Nov, D=Dec
        private static readonly Dictionary<char, int> ZerodhaMonthCodes = new Dictionary<char, int>
        {
            { '1', 1 }, { '2', 2 }, { '3', 3 }, { '4', 4 }, { '5', 5 },
            { '6', 6 }, { '7', 7 }, { '8', 8 }, { '9', 9 },
            { 'O', 10 }, { 'N', 11 }, { 'D', 12 }
        };

        /// <summary>
        /// Convert Zerodha symbol to Accelpix format.
        /// </summary>
        /// <param name="zerodhaSymbol">Zerodha trading symbol (e.g., NIFTY2511326000CE)</param>
        /// <returns>Accelpix symbol, or null if mapping fails</returns>
        public static string MapZerodhaToAccelpix(string zerodhaSymbol)
        {
            if (string.IsNullOrEmpty(zerodhaSymbol))
                return null;

            try
            {
                // Parse the Zerodha symbol
                var parsed = ParseZerodhaSymbol(zerodhaSymbol);
                if (parsed == null)
                {
                    HistoricalTickLogger.LogSymbolMapping(zerodhaSymbol, null, false);
                    return null;
                }

                // Check if monthly expiry
                bool isMonthly = IsMonthlyExpiry(parsed.Expiry);

                // Build Accelpix symbol
                string accelpixSymbol;

                if (isMonthly)
                {
                    // Monthly format: {UNDERLYING}{YY}{MMM}{STRIKE}{CE/PE}
                    string monthAbbrev = MonthAbbreviations[parsed.Expiry.Month];
                    accelpixSymbol = $"{parsed.Underlying}{parsed.Expiry:yy}{monthAbbrev}{parsed.Strike}{parsed.OptionType}";
                }
                else
                {
                    // Weekly format: {UNDERLYING}{YYMMDD}{STRIKE}{CE/PE}
                    accelpixSymbol = $"{parsed.Underlying}{parsed.Expiry:yyMMdd}{parsed.Strike}{parsed.OptionType}";
                }

                // Add -BSE suffix for SENSEX
                if (parsed.Underlying == "SENSEX")
                {
                    accelpixSymbol += "-BSE";
                }

                HistoricalTickLogger.LogSymbolMapping(zerodhaSymbol, accelpixSymbol, true);
                return accelpixSymbol;
            }
            catch (Exception ex)
            {
                HistoricalTickLogger.Error($"[SYMBOL-MAP] Error mapping {zerodhaSymbol}: {ex.Message}");
                return null;
            }
        }

        /// <summary>
        /// Parse a Zerodha option symbol to extract components.
        /// </summary>
        public static ParsedOptionSymbol ParseZerodhaSymbol(string zerodhaSymbol)
        {
            if (string.IsNullOrEmpty(zerodhaSymbol))
                return null;

            try
            {
                // Determine option type
                string optionType = null;
                if (zerodhaSymbol.EndsWith("CE"))
                    optionType = "CE";
                else if (zerodhaSymbol.EndsWith("PE"))
                    optionType = "PE";
                else
                    return null;

                // Remove option type suffix
                string remaining = zerodhaSymbol.Substring(0, zerodhaSymbol.Length - 2);

                // Find the underlying
                string underlying = null;
                foreach (var known in KnownUnderlyings)
                {
                    if (remaining.StartsWith(known))
                    {
                        underlying = known;
                        remaining = remaining.Substring(known.Length);
                        break;
                    }
                }

                if (underlying == null)
                    return null;

                // Remaining should be YYMDD + STRIKE
                // Zerodha format: YY = year, M = month code (1-9,O,N,D), DD = day
                if (remaining.Length < 6)
                    return null;

                // Parse date: first 5 chars are YYMDD
                string dateStr = remaining.Substring(0, 5);
                string strikeStr = remaining.Substring(5);

                int year = 2000 + int.Parse(dateStr.Substring(0, 2));

                // Month code: 1-9 for Jan-Sep, O=Oct, N=Nov, D=Dec
                char monthChar = dateStr[2];
                int month;
                if (!ZerodhaMonthCodes.TryGetValue(monthChar, out month))
                {
                    HistoricalTickLogger.Warn($"[SYMBOL-PARSE] Unknown month code: {monthChar} in {zerodhaSymbol}");
                    return null;
                }

                int day = int.Parse(dateStr.Substring(3, 2));

                DateTime expiry = new DateTime(year, month, day);
                int strike = int.Parse(strikeStr);

                return new ParsedOptionSymbol
                {
                    Underlying = underlying,
                    Expiry = expiry,
                    Strike = strike,
                    OptionType = optionType
                };
            }
            catch (Exception ex)
            {
                HistoricalTickLogger.Debug($"[SYMBOL-PARSE] Error parsing {zerodhaSymbol}: {ex.Message}");
                return null;
            }
        }

        /// <summary>
        /// Check if the given expiry date is a monthly expiry.
        /// Monthly expiries are typically the last Thursday of the month.
        /// </summary>
        public static bool IsMonthlyExpiry(DateTime expiryDate)
        {
            // Monthly expiry is the last Thursday of the month
            // Find the last Thursday of the expiry's month
            DateTime lastThursday = GetLastThursdayOfMonth(expiryDate.Year, expiryDate.Month);

            // If expiry falls on the last Thursday, it's a monthly expiry
            // Allow 1-day tolerance for holidays
            return Math.Abs((expiryDate.Date - lastThursday.Date).TotalDays) <= 1;
        }

        /// <summary>
        /// Get the last Thursday of a given month.
        /// </summary>
        private static DateTime GetLastThursdayOfMonth(int year, int month)
        {
            // Start from the last day of the month
            DateTime lastDay = new DateTime(year, month, DateTime.DaysInMonth(year, month));

            // Go backwards to find Thursday
            while (lastDay.DayOfWeek != DayOfWeek.Thursday)
            {
                lastDay = lastDay.AddDays(-1);
            }

            return lastDay;
        }

        /// <summary>
        /// Build a Zerodha symbol from components.
        /// </summary>
        public static string BuildZerodhaSymbol(string underlying, DateTime expiry, int strike, string optionType)
        {
            // Get month code
            char monthCode;
            if (expiry.Month <= 9)
                monthCode = expiry.Month.ToString()[0];
            else if (expiry.Month == 10)
                monthCode = 'O';
            else if (expiry.Month == 11)
                monthCode = 'N';
            else
                monthCode = 'D';

            // Build symbol: {UNDERLYING}{YY}{M}{DD}{STRIKE}{CE/PE}
            return $"{underlying}{expiry:yy}{monthCode}{expiry:dd}{strike}{optionType}";
        }
    }

    /// <summary>
    /// Parsed option symbol components.
    /// </summary>
    public class ParsedOptionSymbol
    {
        public string Underlying { get; set; }
        public DateTime Expiry { get; set; }
        public int Strike { get; set; }
        public string OptionType { get; set; }

        public override string ToString()
        {
            return $"{Underlying} {Expiry:dd-MMM-yy} {Strike} {OptionType}";
        }
    }
}
