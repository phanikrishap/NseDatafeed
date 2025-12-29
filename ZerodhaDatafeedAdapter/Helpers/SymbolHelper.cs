using System;

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
    }
}
