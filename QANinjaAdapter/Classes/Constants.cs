using System;

namespace QANinjaAdapter.Classes
{
    public static class Constants
    {
        public const string IndianTimeZoneId = "India Standard Time";
        
        public static readonly TimeSpan MarketOpenTime = new TimeSpan(9, 15, 0);
        public static readonly TimeSpan MarketCloseTime = new TimeSpan(15, 30, 0);
        
        public const string ProviderName = "Zerodha";
        public const int ProviderId = 1019;
    }
}
