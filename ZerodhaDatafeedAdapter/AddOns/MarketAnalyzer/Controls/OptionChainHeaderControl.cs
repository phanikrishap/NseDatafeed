using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace ZerodhaDatafeedAdapter.AddOns.MarketAnalyzer.Controls
{
    public class OptionChainHeaderControl : UserControl
    {
        private TextBlock _lblUnderlying;
        private TextBlock _lblExpiry;
        private TextBlock _lblATMStrike;
        private TextBlock _lblStrikePosition;
        private TextBlock _lblSelectedInstrument;

        private static readonly SolidColorBrush _fgColor = new SolidColorBrush(Color.FromRgb(212, 212, 212));
        private static readonly SolidColorBrush _headerBg = new SolidColorBrush(Color.FromRgb(37, 37, 38));
        private static readonly SolidColorBrush _borderColor = new SolidColorBrush(Color.FromRgb(51, 51, 51));
        private static readonly FontFamily _ntFont = new FontFamily("Segoe UI");

        public string Underlying { set => _lblUnderlying.Text = value; }
        public string Expiry { set => _lblExpiry.Text = value; }
        public string ATMStrike { set => _lblATMStrike.Text = value; }

        /// <summary>
        /// Sets the strike position indicator (e.g., "5 above | 5 below ATM")
        /// </summary>
        public void SetStrikePosition(int strikesAbove, int strikesBelow, int totalStrikes)
        {
            _lblStrikePosition.Text = $"{strikesAbove} above | {strikesBelow} below ATM ({totalStrikes} total)";
        }

        public string SelectedInstrument
        {
            set
            {
                _lblSelectedInstrument.Text = value;
                _lblSelectedInstrument.FontStyle = FontStyles.Normal;
                _lblSelectedInstrument.Foreground = new SolidColorBrush(Color.FromRgb(100, 200, 255));
            }
        }

        public OptionChainHeaderControl()
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

            var mainStack = new StackPanel();

            // Row 1: Underlying, Expiry, ATM
            var grid = new Grid();
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

            // Underlying label
            var underlyingPanel = new StackPanel { Orientation = Orientation.Horizontal };
            underlyingPanel.Children.Add(new TextBlock
            {
                Text = "Underlying: ",
                FontFamily = _ntFont,
                FontSize = 12,
                Foreground = _fgColor,
                VerticalAlignment = VerticalAlignment.Center
            });
            _lblUnderlying = new TextBlock
            {
                Text = "NIFTY",
                FontFamily = _ntFont,
                FontSize = 14,
                FontWeight = FontWeights.Bold,
                Foreground = new SolidColorBrush(Color.FromRgb(100, 200, 100)),
                VerticalAlignment = VerticalAlignment.Center
            };
            underlyingPanel.Children.Add(_lblUnderlying);
            Grid.SetColumn(underlyingPanel, 0);
            grid.Children.Add(underlyingPanel);

            // Expiry label
            var expiryPanel = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Center };
            expiryPanel.Children.Add(new TextBlock
            {
                Text = "Expiry: ",
                FontFamily = _ntFont,
                FontSize = 12,
                Foreground = _fgColor,
                VerticalAlignment = VerticalAlignment.Center
            });
            _lblExpiry = new TextBlock
            {
                Text = "---",
                FontFamily = _ntFont,
                FontSize = 14,
                FontWeight = FontWeights.Bold,
                Foreground = new SolidColorBrush(Color.FromRgb(200, 200, 100)),
                VerticalAlignment = VerticalAlignment.Center
            };
            expiryPanel.Children.Add(_lblExpiry);
            Grid.SetColumn(expiryPanel, 1);
            grid.Children.Add(expiryPanel);

            // ATM Strike label
            var atmPanel = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
            atmPanel.Children.Add(new TextBlock
            {
                Text = "ATM: ",
                FontFamily = _ntFont,
                FontSize = 12,
                Foreground = _fgColor,
                VerticalAlignment = VerticalAlignment.Center
            });
            _lblATMStrike = new TextBlock
            {
                Text = "---",
                FontFamily = _ntFont,
                FontSize = 14,
                FontWeight = FontWeights.Bold,
                Foreground = new SolidColorBrush(Color.FromRgb(100, 180, 255)),
                VerticalAlignment = VerticalAlignment.Center
            };
            atmPanel.Children.Add(_lblATMStrike);
            Grid.SetColumn(atmPanel, 2);
            grid.Children.Add(atmPanel);

            mainStack.Children.Add(grid);

            // Row 2: Strike position indicator
            var strikePositionPanel = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 6, 0, 0) };
            strikePositionPanel.Children.Add(new TextBlock
            {
                Text = "Strikes: ",
                FontFamily = _ntFont,
                FontSize = 11,
                Foreground = _fgColor,
                VerticalAlignment = VerticalAlignment.Center
            });
            _lblStrikePosition = new TextBlock
            {
                Text = "-- above | -- below ATM",
                FontFamily = _ntFont,
                FontSize = 11,
                Foreground = new SolidColorBrush(Color.FromRgb(180, 180, 180)),
                VerticalAlignment = VerticalAlignment.Center
            };
            strikePositionPanel.Children.Add(_lblStrikePosition);
            mainStack.Children.Add(strikePositionPanel);

            // Row 3: Selected instrument display
            var linkPanel = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 4, 0, 0) };
            linkPanel.Children.Add(new TextBlock
            {
                Text = "Selected: ",
                FontFamily = _ntFont,
                FontSize = 11,
                Foreground = _fgColor,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 0, 5, 0)
            });
            _lblSelectedInstrument = new TextBlock
            {
                Text = "(Click CE/PE row to select and link to chart)",
                FontFamily = _ntFont,
                FontSize = 10,
                FontStyle = FontStyles.Italic,
                Foreground = new SolidColorBrush(Color.FromRgb(150, 150, 150)),
                VerticalAlignment = VerticalAlignment.Center
            };
            linkPanel.Children.Add(_lblSelectedInstrument);
            mainStack.Children.Add(linkPanel);

            border.Child = mainStack;
            return border;
        }

        public void SetSelectedInstrumentError(string message)
        {
            _lblSelectedInstrument.Text = message;
            _lblSelectedInstrument.FontStyle = FontStyles.Italic;
            _lblSelectedInstrument.Foreground = new SolidColorBrush(Color.FromRgb(255, 200, 100));
        }
    }
}
