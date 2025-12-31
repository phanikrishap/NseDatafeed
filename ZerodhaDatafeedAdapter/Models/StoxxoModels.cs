using System;
using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace ZerodhaDatafeedAdapter.Models
{
    /// <summary>
    /// Configuration for Stoxxo bridge integration
    /// </summary>
    public class StoxxoConfig
    {
        /// <summary>
        /// Base URL for Stoxxo HTTP API (e.g., http://localhost:21000)
        /// </summary>
        public string BaseUrl { get; set; } = "http://localhost:21000";

        /// <summary>
        /// Portfolio name for NIFTY options (e.g., NF_MULTILEG)
        /// </summary>
        public string NiftyPortfolioName { get; set; } = "NF_MULTILEG";

        /// <summary>
        /// Portfolio name for SENSEX options (e.g., SX_MULTILEG)
        /// </summary>
        public string SensexPortfolioName { get; set; } = "SX_MULTILEG";

        /// <summary>
        /// Strategy tag for Stoxxo (e.g., NINJA)
        /// </summary>
        public string StrategyTag { get; set; } = "NINJA";

        /// <summary>
        /// Product type for orders (MIS or NRML)
        /// </summary>
        public string Product { get; set; } = "NRML";

        /// <summary>
        /// Seconds to prevent duplicate orders
        /// </summary>
        public int NoDuplicateOrderForSeconds { get; set; } = 10;

        /// <summary>
        /// Get the portfolio name for a given underlying
        /// </summary>
        public string GetPortfolioName(string underlying)
        {
            if (string.IsNullOrEmpty(underlying))
                return NiftyPortfolioName;

            return underlying.ToUpperInvariant().Contains("SENSEX") || underlying.ToUpperInvariant().Contains("BSE")
                ? SensexPortfolioName
                : NiftyPortfolioName;
        }
    }

    /// <summary>
    /// Stoxxo portfolio status values
    /// </summary>
    public enum StoxxoPortfolioStatus
    {
        Unknown,
        Disabled,
        Stopped,
        Pending,
        Monitoring,
        Started,
        UnderExecution,
        Failed,
        Rejected,
        Completed,
        UnderExit
    }

    /// <summary>
    /// Represents a leg from IB_GetLegs response
    /// Headers: SNO | LegID | IsIdle | Symbol | Expiry | Strike | Instrument | Txn | Lot | Wait and Trade | Target Value | Sl value | IV | Delta | Theta | Vega
    /// </summary>
    public class StoxxoLeg
    {
        public int SNO { get; set; }
        public int LegID { get; set; }
        public bool IsIdle { get; set; }
        public string Symbol { get; set; }
        public DateTime Expiry { get; set; }
        public decimal Strike { get; set; }
        public string Instrument { get; set; }  // CE, PE, or FUT
        public string Txn { get; set; }         // Buy or Sell
        public int Lots { get; set; }
        public decimal WaitAndTrade { get; set; }
        public string TargetValue { get; set; }  // Can be points or percentage (e.g., "10" or "5P")
        public string SLValue { get; set; }      // Can be points or percentage (e.g., "20" or "5P")
        public decimal IV { get; set; }
        public decimal Delta { get; set; }
        public decimal Theta { get; set; }
        public decimal Vega { get; set; }

        /// <summary>
        /// Parse a pipe-separated leg string from IB_GetLegs
        /// </summary>
        public static StoxxoLeg Parse(string legString)
        {
            if (string.IsNullOrEmpty(legString))
                return null;

            var parts = legString.Split('|');
            if (parts.Length < 16)
                return null;

            try
            {
                var leg = new StoxxoLeg
                {
                    SNO = int.TryParse(parts[0], out int sno) ? sno : 0,
                    LegID = int.TryParse(parts[1], out int legId) ? legId : 0,
                    IsIdle = parts[2].Equals("True", StringComparison.OrdinalIgnoreCase),
                    Symbol = parts[3],
                    Strike = decimal.TryParse(parts[5], out decimal strike) ? strike : 0,
                    Instrument = parts[6],
                    Txn = parts[7],
                    Lots = int.TryParse(parts[8], out int lots) ? lots : 0,
                    WaitAndTrade = decimal.TryParse(parts[9], out decimal wt) ? wt : 0,
                    TargetValue = parts[10],
                    SLValue = parts[11],
                    IV = decimal.TryParse(parts[12], out decimal iv) ? iv : 0,
                    Delta = decimal.TryParse(parts[13], out decimal delta) ? delta : 0,
                    Theta = decimal.TryParse(parts[14], out decimal theta) ? theta : 0,
                    Vega = decimal.TryParse(parts[15], out decimal vega) ? vega : 0
                };

                // Parse expiry date (format: dd-MMM-yyyy)
                if (DateTime.TryParse(parts[4], out DateTime expiry))
                    leg.Expiry = expiry;

                return leg;
            }
            catch
            {
                return null;
            }
        }
    }

    /// <summary>
    /// Represents a user leg from IB_GetUserLegs response with execution details
    /// Headers: SNO | LegID | Symbol | Expiry | Strike | Instrument | Txn | Lot | Target Value | Sl value | IV | Delta | Theta | Vega | LTP | PNL | PNL Per Lot | Entry Filled Qty | Avg Entry Price | Exit Filled Qty | Avg Exit Price | Status | Target | SL | Locked Tgt | Trail SL
    /// </summary>
    public class StoxxoUserLeg : StoxxoLeg
    {
        public decimal LTP { get; set; }
        public decimal PnL { get; set; }
        public decimal PnLPerLot { get; set; }
        public int EntryFilledQty { get; set; }
        public decimal AvgEntryPrice { get; set; }
        public int ExitFilledQty { get; set; }
        public decimal AvgExitPrice { get; set; }
        public string Status { get; set; }
        public string Target { get; set; }
        public string SL { get; set; }
        public string LockedTgt { get; set; }
        public string TrailSL { get; set; }

        /// <summary>
        /// Parse a pipe-separated user leg string from IB_GetUserLegs
        /// </summary>
        public static new StoxxoUserLeg Parse(string legString)
        {
            if (string.IsNullOrEmpty(legString))
                return null;

            var parts = legString.Split('|');
            if (parts.Length < 25)
                return null;

            try
            {
                var leg = new StoxxoUserLeg
                {
                    SNO = int.TryParse(parts[0], out int sno) ? sno : 0,
                    LegID = int.TryParse(parts[1], out int legId) ? legId : 0,
                    Symbol = parts[2],
                    Strike = decimal.TryParse(parts[4], out decimal strike) ? strike : 0,
                    Instrument = parts[5],
                    Txn = parts[6],
                    Lots = int.TryParse(parts[7], out int lots) ? lots : 0,
                    TargetValue = parts[8],
                    SLValue = parts[9],
                    IV = decimal.TryParse(parts[10], out decimal iv) ? iv : 0,
                    Delta = decimal.TryParse(parts[11], out decimal delta) ? delta : 0,
                    Theta = decimal.TryParse(parts[12], out decimal theta) ? theta : 0,
                    Vega = decimal.TryParse(parts[13], out decimal vega) ? vega : 0,
                    LTP = decimal.TryParse(parts[14], out decimal ltp) ? ltp : 0,
                    PnL = decimal.TryParse(parts[15], out decimal pnl) ? pnl : 0,
                    PnLPerLot = decimal.TryParse(parts[16], out decimal pnlPerLot) ? pnlPerLot : 0,
                    EntryFilledQty = int.TryParse(parts[17], out int entryQty) ? entryQty : 0,
                    AvgEntryPrice = decimal.TryParse(parts[18], out decimal avgEntry) ? avgEntry : 0,
                    ExitFilledQty = int.TryParse(parts[19], out int exitQty) ? exitQty : 0,
                    AvgExitPrice = decimal.TryParse(parts[20], out decimal avgExit) ? avgExit : 0,
                    Status = parts[21],
                    Target = parts[22],
                    SL = parts[23],
                    LockedTgt = parts[24],
                    TrailSL = parts.Length > 25 ? parts[25] : ""
                };

                // Parse expiry date (format: dd-MMM-yyyy)
                if (DateTime.TryParse(parts[3], out DateTime expiry))
                    leg.Expiry = expiry;

                return leg;
            }
            catch
            {
                return null;
            }
        }
    }

    /// <summary>
    /// Generic response from Stoxxo API
    /// </summary>
    public class StoxxoResponse
    {
        public string status { get; set; }
        public string response { get; set; }
        public string error { get; set; }

        public bool IsSuccess => status?.Equals("success", StringComparison.OrdinalIgnoreCase) ?? false;
    }

    /// <summary>
    /// Helper methods for Stoxxo integration
    /// </summary>
    public static class StoxxoHelper
    {
        /// <summary>
        /// Convert TimeSpan to seconds for Stoxxo API
        /// Example: 09:15:00 => 33300
        /// </summary>
        public static int TimeSpanToSeconds(TimeSpan time)
        {
            return (int)time.TotalSeconds;
        }

        /// <summary>
        /// Convert seconds to TimeSpan
        /// </summary>
        public static TimeSpan SecondsToTimeSpan(int seconds)
        {
            return TimeSpan.FromSeconds(seconds);
        }

        /// <summary>
        /// Parse Stoxxo portfolio status string to enum
        /// </summary>
        public static StoxxoPortfolioStatus ParseStatus(string status)
        {
            if (string.IsNullOrEmpty(status))
                return StoxxoPortfolioStatus.Unknown;

            if (Enum.TryParse<StoxxoPortfolioStatus>(status, true, out var result))
                return result;

            return StoxxoPortfolioStatus.Unknown;
        }

        /// <summary>
        /// Format percentage value for Stoxxo (e.g., 0.5 => "50P", 50 => "50P")
        /// </summary>
        public static string FormatPercentage(decimal value)
        {
            // If value is between 0 and 1, treat as decimal percentage
            if (value > 0 && value < 1)
                value *= 100;

            return $"{value:F0}P";
        }

        /// <summary>
        /// Build PortLegs string for PlaceMultiLegOrderAdv
        /// Format: Strike:ATM|Txn:SELL|Ins:PE|Lots:2|SL:Premium:50P||Strike:ATM|Txn:SELL|Ins:CE|Lots:2|SL:Premium:50P
        /// First leg is PE, second leg is CE (per user requirement)
        /// Uses tranche-mapped quantity and stop loss percentages
        /// </summary>
        /// <param name="lots">Number of lots per leg (from tranche config)</param>
        /// <param name="slPercent">SL percentage as decimal (e.g., 0.5 for 50%) - from tranche config</param>
        /// <param name="strike">Strike value - use "ATM" for at-the-money, or specific strike number</param>
        /// <returns>PortLegs string ready for Stoxxo API</returns>
        public static string BuildPortLegs(int lots, decimal slPercent, string strike = "ATM")
        {
            // Format SL as percentage (e.g., "50P" for 50%)
            string slValue = slPercent > 0 ? $"Premium:{FormatPercentage(slPercent * 100)}" : "";

            // Build PE leg (first)
            var peLeg = BuildSingleLeg(strike, "SELL", "PE", lots, slValue);

            // Build CE leg (second)
            var ceLeg = BuildSingleLeg(strike, "SELL", "CE", lots, slValue);

            // Concatenate with || (double pipe)
            return $"{peLeg}||{ceLeg}";
        }

        /// <summary>
        /// Build a single leg string (without expiry - Stoxxo uses default)
        /// </summary>
        private static string BuildSingleLeg(string strike, string txn, string ins, int lots, string sl)
        {
            var parts = new System.Collections.Generic.List<string>
            {
                $"Strike:{strike}",
                $"Txn:{txn}",
                $"Ins:{ins}",
                $"Lots:{lots}"
            };

            // Add SL if specified
            if (!string.IsNullOrEmpty(sl))
            {
                parts.Add($"SL:{sl}");
            }

            return string.Join("|", parts);
        }
    }
}
