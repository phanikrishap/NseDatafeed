using System;
using System.Globalization;
using System.Windows.Data;
using System.Windows.Media;

namespace ZerodhaDatafeedAdapter.AddOns.MarketAnalyzer.Converters
{
    /// <summary>
    /// Converts VWAP comparison value to color: Green if price > VWAP, Red if price < VWAP, Yellow if equal or no data
    /// </summary>
    public class VWAPComparisonToColorConverter : IValueConverter
    {
        private static readonly SolidColorBrush GreenBrush = new SolidColorBrush(Color.FromRgb(100, 200, 100));  // Price above VWAP
        private static readonly SolidColorBrush RedBrush = new SolidColorBrush(Color.FromRgb(220, 80, 80));      // Price below VWAP
        private static readonly SolidColorBrush YellowBrush = new SolidColorBrush(Color.FromRgb(255, 220, 100)); // No data or equal

        static VWAPComparisonToColorConverter()
        {
            GreenBrush.Freeze();
            RedBrush.Freeze();
            YellowBrush.Freeze();
        }

        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            if (value is int comparison)
            {
                if (comparison > 0) return GreenBrush;  // Price above VWAP
                if (comparison < 0) return RedBrush;    // Price below VWAP
            }
            return YellowBrush; // No data or equal
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        {
            throw new NotImplementedException();
        }
    }
}
