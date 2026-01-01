using System;
using System.Globalization;
using System.Windows.Data;
using System.Windows.Media;

namespace ZerodhaDatafeedAdapter.AddOns.TBSManager.Converters
{
    /// <summary>
    /// Converts P&L value to color (green for profit, red for loss, neutral for zero)
    /// </summary>
    public class PnLToColorConverter : IValueConverter
    {
        private static readonly SolidColorBrush GreenBrush = new SolidColorBrush(Color.FromRgb(100, 200, 100));
        private static readonly SolidColorBrush RedBrush = new SolidColorBrush(Color.FromRgb(220, 80, 80));
        private static readonly SolidColorBrush NeutralBrush = new SolidColorBrush(Color.FromRgb(212, 212, 212));

        static PnLToColorConverter()
        {
            // Freeze brushes for performance
            GreenBrush.Freeze();
            RedBrush.Freeze();
            NeutralBrush.Freeze();
        }

        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            if (value is decimal pnl)
            {
                if (pnl > 0) return GreenBrush;
                if (pnl < 0) return RedBrush;
            }
            return NeutralBrush;
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        {
            throw new NotImplementedException();
        }
    }
}
