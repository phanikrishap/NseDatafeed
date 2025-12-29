using System;

namespace ZerodhaDatafeedAdapter.Classes
{
    public static class Constants
    {
        public const string IndianTimeZoneId = "India Standard Time";

        // Cached TimeZone to avoid repeated FindSystemTimeZoneById calls in hot paths
        public static readonly TimeZoneInfo IndianTimeZone = TimeZoneInfo.FindSystemTimeZoneById(IndianTimeZoneId);

        // Cached Unix epoch for timestamp conversions
        public static readonly DateTime UnixEpoch = new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc);

        public static readonly TimeSpan MarketOpenTime = new TimeSpan(9, 15, 0);
        public static readonly TimeSpan MarketCloseTime = new TimeSpan(15, 30, 0);
        
        public const string ProviderName = "Zerodha";
        public const int ProviderId = 1019;  // Custom19 provider

        // File and Directory Paths
        public const string BaseDataFolder = "NinjaTrader 8\\ZerodhaAdapter\\";
        public const string ConfigFileName = "config.json";
        public const string IndexMappingsFileName = "index_mappings.json";      // Static indices - never changes
        public const string FOMappingsFileName = "fo_mappings.json";             // F&O - recreated on startup
        public const string MappedInstrumentsFileName = "mapped_instruments.json"; // Legacy - to be removed
        public const string InstrumentDbFileName = "InstrumentMasters.db";
    }
}
