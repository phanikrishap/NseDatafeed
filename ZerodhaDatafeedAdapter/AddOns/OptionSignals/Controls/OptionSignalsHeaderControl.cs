using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace ZerodhaDatafeedAdapter.AddOns.OptionSignals.Controls
{
    public class OptionSignalsHeaderControl : UserControl
    {
        private TextBlock _lblUnderlying;
        private TextBlock _lblExpiry;
        private TextBlock _lblStatus;

        private static readonly SolidColorBrush _fgColor = new SolidColorBrush(Color.FromRgb(212, 212, 212));
        private static readonly SolidColorBrush _headerBg = new SolidColorBrush(Color.FromRgb(37, 37, 38));
        private static readonly SolidColorBrush _borderColor = new SolidColorBrush(Color.FromRgb(51, 51, 51));

        public string Underlying { set => _lblUnderlying.Text = value; }
        public string Expiry { set => _lblExpiry.Text = value; }
        public string StatusText { set => _lblStatus.Text = value; }

        public OptionSignalsHeaderControl()
        {
            Content = BuildUI();
        }

        private Border BuildUI()
        {
            var border = new Border
            {
                Background = _headerBg,
                BorderBrush = _borderColor,
                BorderThickness = new Thickness(0, 0, 0, 1),
                Padding = new Thickness(10, 8, 10, 8)
            };

            var grid = new Grid();
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

            // Underlying
            var pnlUnderlying = new StackPanel { Orientation = Orientation.Horizontal };
            pnlUnderlying.Children.Add(new TextBlock { Text = "Underlying: ", Foreground = _fgColor, VerticalAlignment = VerticalAlignment.Center });
            _lblUnderlying = new TextBlock { Text = "NIFTY", FontWeight = FontWeights.Bold, Foreground = new SolidColorBrush(Color.FromRgb(100, 200, 100)), VerticalAlignment = VerticalAlignment.Center };
            pnlUnderlying.Children.Add(_lblUnderlying);
            Grid.SetColumn(pnlUnderlying, 0);
            grid.Children.Add(pnlUnderlying);

            // Expiry
            var pnlExpiry = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Center };
            pnlExpiry.Children.Add(new TextBlock { Text = "Expiry: ", Foreground = _fgColor, VerticalAlignment = VerticalAlignment.Center });
            _lblExpiry = new TextBlock { Text = "---", FontWeight = FontWeights.Bold, Foreground = new SolidColorBrush(Color.FromRgb(200, 200, 100)), VerticalAlignment = VerticalAlignment.Center };
            pnlExpiry.Children.Add(_lblExpiry);
            Grid.SetColumn(pnlExpiry, 1);
            grid.Children.Add(pnlExpiry);

            // Sync Status
            _lblStatus = new TextBlock { Text = "Initializing...", Foreground = new SolidColorBrush(Color.FromRgb(150, 150, 150)), VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Right, FontStyle = FontStyles.Italic };
            Grid.SetColumn(_lblStatus, 2);
            grid.Children.Add(_lblStatus);

            border.Child = grid;
            return border;
        }
    }
}
