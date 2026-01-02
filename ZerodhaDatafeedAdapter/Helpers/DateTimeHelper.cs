using System;
using ZerodhaDatafeedAdapter.Classes;

namespace ZerodhaDatafeedAdapter.Helpers
{
    /// <summary>
    /// Centralized DateTime utilities for NinjaTrader compatibility.
    /// Handles timezone conversions and ensures proper DateTimeKind for market data.
    /// </summary>
    public static class DateTimeHelper
    {
        // Cached TimeZone for performance (avoid repeated FindSystemTimeZoneById calls)
        private static readonly TimeZoneInfo IstTimeZone = TimeZoneInfo.FindSystemTimeZoneById(Constants.IndianTimeZoneId);

        /// <summary>
        /// Ensures DateTime has proper Kind for NinjaTrader compatibility.
        /// Optimized version that uses cached timezone and minimal allocations.
        /// </summary>
        /// <param name="dateTime">Input DateTime (any Kind)</param>
        /// <returns>DateTime with Local kind, converted to IST if needed</returns>
        public static DateTime EnsureNinjaTraderDateTime(DateTime dateTime)
        {
            try
            {
                // Fast path: Already local
                if (dateTime.Kind == DateTimeKind.Local)
                {
                    return dateTime;
                }

                // Handle UTC DateTime - convert to cached IST
                if (dateTime.Kind == DateTimeKind.Utc)
                {
                    var istTime = TimeZoneInfo.ConvertTimeFromUtc(dateTime, IstTimeZone);
                    return DateTime.SpecifyKind(istTime, DateTimeKind.Local);
                }

                // Handle Unspecified DateTime - assume it's local IST
                return DateTime.SpecifyKind(dateTime, DateTimeKind.Local);
            }
            catch
            {
                // Fallback: Return current time
                return DateTime.Now;
            }
        }

        /// <summary>
        /// Ensures DateTime has proper Kind for NinjaTrader compatibility.
        /// Full version with explicit DateTime reconstruction (legacy compatibility).
        /// </summary>
        /// <param name="dateTime">Input DateTime (any Kind)</param>
        /// <returns>DateTime with Local kind, explicitly constructed</returns>
        public static DateTime EnsureProperDateTime(DateTime dateTime)
        {
            try
            {
                DateTime resultTime;

                if (dateTime.Kind == DateTimeKind.Utc)
                {
                    try
                    {
                        // Convert UTC to local IST time
                        DateTime istTime = TimeZoneInfo.ConvertTimeFromUtc(dateTime, IstTimeZone);
                        resultTime = new DateTime(istTime.Year, istTime.Month, istTime.Day,
                                                istTime.Hour, istTime.Minute, istTime.Second,
                                                istTime.Millisecond, DateTimeKind.Local);
                    }
                    catch
                    {
                        // Fallback: Use system local time conversion
                        DateTime localTime = dateTime.ToLocalTime();
                        resultTime = new DateTime(localTime.Year, localTime.Month, localTime.Day,
                                                localTime.Hour, localTime.Minute, localTime.Second,
                                                localTime.Millisecond, DateTimeKind.Local);
                    }
                }
                else
                {
                    // Unspecified or Local - ensure Local kind
                    resultTime = new DateTime(dateTime.Year, dateTime.Month, dateTime.Day,
                                            dateTime.Hour, dateTime.Minute, dateTime.Second,
                                            dateTime.Millisecond, DateTimeKind.Local);
                }

                return resultTime;
            }
            catch
            {
                var now = DateTime.Now;
                return new DateTime(now.Year, now.Month, now.Day,
                                  now.Hour, now.Minute, now.Second,
                                  now.Millisecond, DateTimeKind.Local);
            }
        }

        /// <summary>
        /// Converts DateTime to IST timezone.
        /// </summary>
        public static DateTime ConvertToIst(DateTime dateTime)
        {
            if (dateTime.Kind == DateTimeKind.Utc)
            {
                return TimeZoneInfo.ConvertTimeFromUtc(dateTime, IstTimeZone);
            }
            return TimeZoneInfo.ConvertTime(dateTime, IstTimeZone);
        }

        /// <summary>
        /// Gets current time in IST.
        /// </summary>
        public static DateTime NowIst => TimeZoneInfo.ConvertTime(DateTime.Now, IstTimeZone);

        #region Market Hours

        // NSE/BSE Market Hours (IST)
        private static readonly TimeSpan MarketOpenTime = new TimeSpan(9, 15, 0);   // 09:15 IST
        private static readonly TimeSpan MarketCloseTime = new TimeSpan(15, 30, 0); // 15:30 IST

        // Pre-market hours for indices (GIFT NIFTY trades on SGX, spot indices get pre-market data)
        private static readonly TimeSpan PreMarketOpenTime = new TimeSpan(9, 0, 0); // 09:00 IST

        /// <summary>
        /// Checks if current time is within NSE/BSE market hours (9:15 AM - 3:30 PM IST).
        /// For indices/GIFT NIFTY, use IsMarketOpenForIndices() which includes pre-market from 9:00 AM.
        /// </summary>
        /// <returns>True if within market hours</returns>
        public static bool IsMarketOpen()
        {
            var now = NowIst;
            var timeOfDay = now.TimeOfDay;

            // Check if it's a weekday (Monday=1, Friday=5)
            if (now.DayOfWeek == DayOfWeek.Saturday || now.DayOfWeek == DayOfWeek.Sunday)
                return false;

            return timeOfDay >= MarketOpenTime && timeOfDay <= MarketCloseTime;
        }

        /// <summary>
        /// Checks if current time is within market hours for indices (includes pre-market from 9:00 AM).
        /// GIFT NIFTY trades on SGX and spot indices (NIFTY, SENSEX, BANKNIFTY) get pre-market data from 9:00 AM.
        /// </summary>
        /// <returns>True if within index market hours (9:00 AM - 3:30 PM IST)</returns>
        public static bool IsMarketOpenForIndices()
        {
            var now = NowIst;
            var timeOfDay = now.TimeOfDay;

            // Check if it's a weekday
            if (now.DayOfWeek == DayOfWeek.Saturday || now.DayOfWeek == DayOfWeek.Sunday)
                return false;

            // Indices get data from 9:00 AM (pre-market) to 3:30 PM
            return timeOfDay >= PreMarketOpenTime && timeOfDay <= MarketCloseTime;
        }

        /// <summary>
        /// Checks if a symbol is an index that gets pre-market data.
        /// Includes GIFT NIFTY (SGX), NIFTY, SENSEX, BANKNIFTY spot indices.
        /// </summary>
        public static bool IsIndexSymbol(string symbol)
        {
            if (string.IsNullOrEmpty(symbol)) return false;
            var upper = symbol.ToUpperInvariant();

            // Check for spot indices and GIFT NIFTY
            return upper == "NIFTY" || upper == "NIFTY 50" ||
                   upper == "SENSEX" ||
                   upper == "BANKNIFTY" || upper == "NIFTY BANK" ||
                   upper == "GIFT NIFTY" || upper == "GIFT_NIFTY" || upper.Contains("GIFTNIFTY");
        }

        /// <summary>
        /// Checks if market is open for a specific symbol.
        /// Uses extended hours (9:00 AM) for indices, regular hours (9:15 AM) for options/stocks.
        /// </summary>
        public static bool IsMarketOpenForSymbol(string symbol)
        {
            return IsIndexSymbol(symbol) ? IsMarketOpenForIndices() : IsMarketOpen();
        }

        /// <summary>
        /// Checks if current time is within NSE/BSE market hours.
        /// </summary>
        /// <param name="dateTime">DateTime to check (will be converted to IST)</param>
        /// <returns>True if within market hours</returns>
        public static bool IsMarketOpen(DateTime dateTime)
        {
            var istTime = ConvertToIst(dateTime);
            var timeOfDay = istTime.TimeOfDay;

            // Check if it's a weekday
            if (istTime.DayOfWeek == DayOfWeek.Saturday || istTime.DayOfWeek == DayOfWeek.Sunday)
                return false;

            return timeOfDay >= MarketOpenTime && timeOfDay <= MarketCloseTime;
        }

        /// <summary>
        /// Checks if current time is pre-market (before 9:15 AM IST on a trading day).
        /// </summary>
        public static bool IsPreMarket()
        {
            var now = NowIst;

            // Weekends are not pre-market
            if (now.DayOfWeek == DayOfWeek.Saturday || now.DayOfWeek == DayOfWeek.Sunday)
                return false;

            return now.TimeOfDay < MarketOpenTime;
        }

        /// <summary>
        /// Checks if current time is post-market (after 3:30 PM IST on a trading day).
        /// </summary>
        public static bool IsPostMarket()
        {
            var now = NowIst;

            // Weekends are not post-market
            if (now.DayOfWeek == DayOfWeek.Saturday || now.DayOfWeek == DayOfWeek.Sunday)
                return false;

            return now.TimeOfDay > MarketCloseTime;
        }

        /// <summary>
        /// Gets the market close time (15:30) for display in UI during off-hours.
        /// Returns the given date with time set to 15:30:00.
        /// </summary>
        /// <param name="date">The date to use (defaults to today if not specified)</param>
        /// <returns>DateTime with time set to market close (15:30:00)</returns>
        public static DateTime GetMarketCloseTime(DateTime? date = null)
        {
            var targetDate = date ?? NowIst.Date;
            return targetDate.Add(MarketCloseTime);
        }

        /// <summary>
        /// Adjusts timestamp for display: if outside market hours, clamps to market close time.
        /// This is useful for Option Chain UI to show 15:30 for post-market ticks.
        /// </summary>
        /// <param name="timestamp">Original timestamp</param>
        /// <returns>Adjusted timestamp (clamped to 15:30 if post-market)</returns>
        public static DateTime ClampToMarketHours(DateTime timestamp)
        {
            var istTime = ConvertToIst(timestamp);

            // If post-market, return market close time
            if (istTime.TimeOfDay > MarketCloseTime)
            {
                return istTime.Date.Add(MarketCloseTime);
            }

            // If pre-market, return as-is (or could clamp to market open)
            return timestamp;
        }

        #endregion

        #region Unix Timestamp Conversion

        // Unix epoch constant for performance
        private static readonly DateTime UnixEpoch = new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc);

        /// <summary>
        /// Converts a Unix timestamp (seconds since epoch) to local DateTime.
        /// Consolidates conversion logic from WebSocketManager and HistoricalDataService.
        /// </summary>
        /// <param name="unixSeconds">Unix timestamp in seconds</param>
        /// <returns>Local DateTime</returns>
        public static DateTime UnixSecondsToLocalTime(int unixSeconds)
        {
            return UnixEpoch.AddSeconds(unixSeconds).ToLocalTime();
        }

        /// <summary>
        /// Converts a Unix timestamp (seconds since epoch) to local DateTime.
        /// Overload for long values.
        /// </summary>
        /// <param name="unixSeconds">Unix timestamp in seconds</param>
        /// <returns>Local DateTime</returns>
        public static DateTime UnixSecondsToLocalTime(long unixSeconds)
        {
            return UnixEpoch.AddSeconds(unixSeconds).ToLocalTime();
        }

        /// <summary>
        /// Converts a Unix timestamp (milliseconds since epoch) to local DateTime.
        /// </summary>
        /// <param name="unixMilliseconds">Unix timestamp in milliseconds</param>
        /// <returns>Local DateTime</returns>
        public static DateTime UnixMillisecondsToLocalTime(long unixMilliseconds)
        {
            return UnixEpoch.AddMilliseconds(unixMilliseconds).ToLocalTime();
        }

        /// <summary>
        /// Converts a DateTime to Unix timestamp (seconds since epoch).
        /// </summary>
        /// <param name="dateTime">DateTime to convert</param>
        /// <returns>Unix timestamp in seconds</returns>
        public static long ToUnixSeconds(DateTime dateTime)
        {
            if (dateTime.Kind == DateTimeKind.Local)
            {
                dateTime = dateTime.ToUniversalTime();
            }
            return (long)(dateTime - UnixEpoch).TotalSeconds;
        }

        #endregion
    }
}
