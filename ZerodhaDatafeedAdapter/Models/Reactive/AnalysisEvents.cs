namespace ZerodhaDatafeedAdapter.Models.Reactive
{
    /// <summary>
    /// Data class for System.Reactive price update events
    /// </summary>
    public class TickerPriceUpdate
    {
        public string TickerSymbol { get; set; }
        public double Price { get; set; }
        public double Close { get; set; }
        public double NetChangePercent { get; set; }
    }

    /// <summary>
    /// Data class for System.Reactive projected open calculation events
    /// </summary>
    public class ProjectedOpenUpdate
    {
        public double NiftyProjectedOpen { get; set; }
        public double SensexProjectedOpen { get; set; }
        public double GiftChangePercent { get; set; }
    }
}
