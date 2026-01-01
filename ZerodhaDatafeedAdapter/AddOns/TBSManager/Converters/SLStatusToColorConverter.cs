using System;
using System.Globalization;
using System.Windows.Data;
using System.Windows.Media;

namespace ZerodhaDatafeedAdapter.AddOns.TBSManager.Converters
{
    /// <summary>
    /// Converts SL status string to color (SAFE=green, WARNING=yellow, HIT=red)
    /// </summary>
    public class SLStatusToColorConverter : IValueConverter
    {
        private static readonly SolidColorBrush SafeBrush = new SolidColorBrush(Color.FromRgb(100, 200, 100));
        private static readonly SolidColorBrush WarningBrush = new SolidColorBrush(Color.FromRgb(220, 180, 50));
        private static readonly SolidColorBrush HitBrush = new SolidColorBrush(Color.FromRgb(220, 80, 80));
        private static readonly SolidColorBrush NeutralBrush = new SolidColorBrush(Color.FromRgb(150, 150, 150));

        static SLStatusToColorConverter()
        {
            // Freeze brushes for performance
            SafeBrush.Freeze();
            WarningBrush.Freeze();
            HitBrush.Freeze();
            NeutralBrush.Freeze();
        }

        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            if (value is string status)
            {
                return status.ToUpper() switch
                {
                    "SAFE" => SafeBrush,
                    "WARNING" => WarningBrush,
                    "HIT" => HitBrush,
                    _ => NeutralBrush
                };
            }
            return NeutralBrush;
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        {
            throw new NotImplementedException();
        }
    }
}
