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
    }
}
