using System;
using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace ZerodhaDatafeedAdapter.AddOns.MarketAnalyzer.Converters
{
    /// <summary>
    /// Converts a percentage (0-100) to a pixel width based on max width parameter
    /// </summary>
    public class PercentageToWidthConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            if (value == null || value == DependencyProperty.UnsetValue)
                return 0.0;

            double percentage = 0;
            if (value is double d)
                percentage = d;

            double maxWidth = 96; // Default histogram bar max width
            if (parameter is double mw)
                maxWidth = mw;
            else if (parameter is string s && double.TryParse(s, out double parsed))
                maxWidth = parsed;

            // Clamp percentage to 0-100
            percentage = Math.Max(0, Math.Min(100, percentage));

            return (percentage / 100.0) * maxWidth;
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        {
            throw new NotImplementedException();
        }
    }
}
