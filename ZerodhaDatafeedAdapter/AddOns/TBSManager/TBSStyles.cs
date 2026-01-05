using System;
using System.Windows;
using System.Windows.Media;

namespace ZerodhaDatafeedAdapter.AddOns.TBSManager
{
    public static class TBSStyles
    {
        // NinjaTrader-style colors
        public static readonly SolidColorBrush BgColor = new SolidColorBrush(Color.FromRgb(27, 27, 28));
        public static readonly SolidColorBrush FgColor = new SolidColorBrush(Color.FromRgb(212, 212, 212));
        public static readonly SolidColorBrush HeaderBg = new SolidColorBrush(Color.FromRgb(37, 37, 38));
        public static readonly SolidColorBrush BorderColor = new SolidColorBrush(Color.FromRgb(51, 51, 51));
        public static readonly SolidColorBrush RowAltBg = new SolidColorBrush(Color.FromRgb(32, 32, 33));
        public static readonly SolidColorBrush ExpanderBg = new SolidColorBrush(Color.FromRgb(40, 40, 42));
        public static readonly FontFamily NtFont = new FontFamily("Segoe UI");

        // Status colors
        public static readonly SolidColorBrush SkippedColor = new SolidColorBrush(Color.FromRgb(120, 120, 120));
        public static readonly SolidColorBrush IdleColor = new SolidColorBrush(Color.FromRgb(150, 150, 150));
        public static readonly SolidColorBrush MonitoringColor = new SolidColorBrush(Color.FromRgb(200, 180, 80));
        public static readonly SolidColorBrush LiveColor = new SolidColorBrush(Color.FromRgb(100, 200, 100));
        public static readonly SolidColorBrush SquaredOffColor = new SolidColorBrush(Color.FromRgb(100, 150, 220));
        public static readonly SolidColorBrush NegativeColor = new SolidColorBrush(Color.FromRgb(220, 80, 80));
    }
}
