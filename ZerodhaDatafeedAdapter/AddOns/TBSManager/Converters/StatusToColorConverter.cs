using System;
using System.Globalization;
using System.Windows.Data;
using System.Windows.Media;
using ZerodhaDatafeedAdapter.Models;

namespace ZerodhaDatafeedAdapter.AddOns.TBSManager.Converters
{
    /// <summary>
    /// Converts TBSExecutionStatus to color for visual status indication
    /// </summary>
    public class StatusToColorConverter : IValueConverter
    {
        private static readonly SolidColorBrush SkippedBrush = new SolidColorBrush(Color.FromRgb(120, 120, 120));   // Gray
        private static readonly SolidColorBrush IdleBrush = new SolidColorBrush(Color.FromRgb(150, 150, 150));      // Light Gray
        private static readonly SolidColorBrush MonitoringBrush = new SolidColorBrush(Color.FromRgb(200, 180, 80)); // Yellow/Amber
        private static readonly SolidColorBrush LiveBrush = new SolidColorBrush(Color.FromRgb(100, 200, 100));      // Green
        private static readonly SolidColorBrush SquaredOffBrush = new SolidColorBrush(Color.FromRgb(100, 150, 220));// Blue

        static StatusToColorConverter()
        {
            // Freeze brushes for performance
            SkippedBrush.Freeze();
            IdleBrush.Freeze();
            MonitoringBrush.Freeze();
            LiveBrush.Freeze();
            SquaredOffBrush.Freeze();
        }

        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            if (value is TBSExecutionStatus status)
            {
                return status switch
                {
                    TBSExecutionStatus.Skipped => SkippedBrush,
                    TBSExecutionStatus.Idle => IdleBrush,
                    TBSExecutionStatus.Monitoring => MonitoringBrush,
                    TBSExecutionStatus.Live => LiveBrush,
                    TBSExecutionStatus.SquaredOff => SquaredOffBrush,
                    _ => IdleBrush
                };
            }
            return IdleBrush;
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        {
            throw new NotImplementedException();
        }
    }
}
