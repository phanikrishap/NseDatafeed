using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Newtonsoft.Json;
using ZerodhaDatafeedAdapter.Helpers;

namespace ZerodhaDatafeedAdapter.Services
{
    /// <summary>
    /// Service for computing prior working days based on holiday calendar.
    /// Reads holidays from holidayCalendar.json in the ZerodhaAdapter folder.
    /// </summary>
    public class HolidayCalendarService
    {
        private static readonly Lazy<HolidayCalendarService> _instance = new Lazy<HolidayCalendarService>(() => new HolidayCalendarService());
        public static HolidayCalendarService Instance => _instance.Value;

        private HashSet<DateTime> _holidays = new HashSet<DateTime>();

        private HolidayCalendarService()
        {
            LoadHolidayCalendar();
        }

        private void LoadHolidayCalendar()
        {
            try
            {
                string calendarPath = Classes.Constants.GetFolderPath("holidayCalendar.json");

                if (!File.Exists(calendarPath))
                {
                    Logger.Warn($"[HolidayCalendarService] Holiday calendar not found at {calendarPath}");
                    return;
                }

                string json = File.ReadAllText(calendarPath);
                var calendar = JsonConvert.DeserializeObject<HolidayCalendar>(json);

                if (calendar?.holidays != null)
                {
                    foreach (var holiday in calendar.holidays)
                    {
                        if (DateTime.TryParse(holiday.date, out DateTime holidayDate))
                        {
                            _holidays.Add(holidayDate.Date);
                        }
                    }
                    Logger.Info($"[HolidayCalendarService] Loaded {_holidays.Count} holidays for year {calendar.year}");
                }
            }
            catch (Exception ex)
            {
                Logger.Error($"[HolidayCalendarService] Failed to load holiday calendar: {ex.Message}", ex);
            }
        }

        /// <summary>
        /// Checks if a given date is a holiday
        /// </summary>
        public bool IsHoliday(DateTime date)
        {
            return _holidays.Contains(date.Date);
        }

        /// <summary>
        /// Checks if a given date is a weekend (Saturday or Sunday)
        /// </summary>
        public bool IsWeekend(DateTime date)
        {
            return date.DayOfWeek == DayOfWeek.Saturday || date.DayOfWeek == DayOfWeek.Sunday;
        }

        /// <summary>
        /// Checks if a given date is a trading day (not weekend, not holiday)
        /// </summary>
        public bool IsTradingDay(DateTime date)
        {
            return !IsWeekend(date) && !IsHoliday(date);
        }

        /// <summary>
        /// Gets the prior working day (trading day) from a given date.
        /// Skips weekends and holidays.
        /// </summary>
        public DateTime GetPriorWorkingDay(DateTime fromDate)
        {
            DateTime priorDate = fromDate.Date.AddDays(-1);

            // Keep going back until we find a trading day
            int maxIterations = 10; // Safety limit
            int iterations = 0;

            while (iterations < maxIterations)
            {
                if (IsTradingDay(priorDate))
                {
                    Logger.Debug($"[HolidayCalendarService] Prior working day from {fromDate:yyyy-MM-dd} is {priorDate:yyyy-MM-dd}");
                    return priorDate;
                }

                priorDate = priorDate.AddDays(-1);
                iterations++;
            }

            // Fallback - return previous calendar day if we can't find a trading day
            Logger.Warn($"[HolidayCalendarService] Could not find prior trading day within {maxIterations} days, returning {fromDate.AddDays(-1):yyyy-MM-dd}");
            return fromDate.Date.AddDays(-1);
        }

        /// <summary>
        /// Gets the prior working day from today
        /// </summary>
        public DateTime GetPriorWorkingDay()
        {
            return GetPriorWorkingDay(DateTime.Today);
        }

        /// <summary>
        /// Gets the prior working day formatted as a string (e.g., "03-Jan-2026")
        /// </summary>
        public string GetPriorWorkingDayFormatted(string format = "dd-MMM-yyyy")
        {
            return GetPriorWorkingDay().ToString(format);
        }
    }

    // JSON model classes
    internal class HolidayCalendar
    {
        public int year { get; set; }
        public List<HolidayEntry> holidays { get; set; }
    }

    internal class HolidayEntry
    {
        public string date { get; set; }
        public string day { get; set; }
        public string description { get; set; }
    }
}
