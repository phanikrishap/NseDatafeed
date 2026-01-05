using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace ZerodhaDatafeedAdapter.AddOns.MarketAnalyzer.Controls
{
    public class OptionChainStatusBar : UserControl
    {
        private TextBlock _lblStatus;
        private static readonly SolidColorBrush _headerBg = new SolidColorBrush(Color.FromRgb(37, 37, 38));
        private static readonly SolidColorBrush _fgColor = new SolidColorBrush(Color.FromRgb(212, 212, 212));
        private static readonly SolidColorBrush _borderColor = new SolidColorBrush(Color.FromRgb(51, 51, 51));
        private static readonly FontFamily _ntFont = new FontFamily("Segoe UI");

        public string StatusText
        {
            get => _lblStatus.Text;
            set => _lblStatus.Text = value;
        }

        public OptionChainStatusBar()
        {
            Content = BuildUI();
        }

        private Border BuildUI()
        {
            var border = new Border
            {
                Background = _headerBg,
                BorderBrush = _borderColor,
                BorderThickness = new Thickness(0, 1, 0, 0),
                Padding = new Thickness(8, 4, 8, 4)
            };

            _lblStatus = new TextBlock
            {
                Text = "Waiting for option data...",
                FontFamily = _ntFont,
                FontSize = 11,
                Foreground = _fgColor
            };

            border.Child = _lblStatus;
            return border;
        }
    }
}
