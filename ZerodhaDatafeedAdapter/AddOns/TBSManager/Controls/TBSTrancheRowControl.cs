using System;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;
using ZerodhaDatafeedAdapter.AddOns.TBSManager.Converters;
using ZerodhaDatafeedAdapter.Models;

namespace ZerodhaDatafeedAdapter.AddOns.TBSManager.Controls
{
    public class TBSTrancheRowControl : UserControl
    {
        private TBSExecutionState _state;
        private StackPanel _legsContent;
        private TextBlock _arrowText;

        public TBSTrancheRowControl(TBSExecutionState state, bool isAlt)
        {
            _state = state;
            DataContext = _state;
            Content = BuildUI(isAlt);
        }

        private Border BuildUI(bool isAlt)
        {
            var border = new Border
            {
                Background = isAlt ? TBSStyles.RowAltBg : TBSStyles.BgColor,
                BorderBrush = TBSStyles.BorderColor,
                BorderThickness = new Thickness(0, 0, 0, 1),
                Padding = new Thickness(10, 6, 10, 6),
                Tag = _state
            };

            var mainStack = new StackPanel();

            // Headline row: Tranche | Entry Time | Qty | Entry | Exit | Exit Time | Status | P&L | Stoxxo Name | Stoxxo Status | Stoxxo P&L | Message
            var headlineGrid = new Grid();
            headlineGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(40) });   // Tranche ID
            headlineGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(70) });   // Entry Time
            headlineGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(35) });   // Qty
            headlineGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(80) });   // Entry Price
            headlineGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(80) });   // Exit Price
            headlineGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(65) });   // Exit Time
            headlineGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(80) });   // Status
            headlineGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(80) });   // P&L
            headlineGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(110) });  // Stoxxo Name
            headlineGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(90) });   // Stoxxo Status
            headlineGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(80) });   // Stoxxo P&L
            headlineGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) }); // Message

            int col = 0;

            // Tranche ID
            var trancheText = CreateTextBlock(12, FontWeights.Bold);
            trancheText.SetBinding(TextBlock.TextProperty, new Binding("TrancheId") { StringFormat = "#{0}" });
            Grid.SetColumn(trancheText, col++);
            headlineGrid.Children.Add(trancheText);

            // Entry Time
            var entryTimeText = CreateTextBlock(12, FontWeights.SemiBold);
            entryTimeText.Text = _state.EntryTimeDisplay;
            Grid.SetColumn(entryTimeText, col++);
            headlineGrid.Children.Add(entryTimeText);

            // Qty
            var qtyText = CreateTextBlock(11);
            qtyText.SetBinding(TextBlock.TextProperty, new Binding("Quantity"));
            Grid.SetColumn(qtyText, col++);
            headlineGrid.Children.Add(qtyText);

            // Entry Price
            var entryPriceText = CreateTextBlock(11);
            entryPriceText.SetBinding(TextBlock.TextProperty, new Binding("EntryPriceDisplay"));
            Grid.SetColumn(entryPriceText, col++);
            headlineGrid.Children.Add(entryPriceText);

            // Exit Price
            var exitPriceText = CreateTextBlock(11);
            exitPriceText.SetBinding(TextBlock.TextProperty, new Binding("ExitPriceDisplay"));
            Grid.SetColumn(exitPriceText, col++);
            headlineGrid.Children.Add(exitPriceText);

            // Exit Time
            var exitTimeText = CreateTextBlock(11);
            exitTimeText.SetBinding(TextBlock.TextProperty, new Binding("ExitTimeDisplay"));
            Grid.SetColumn(exitTimeText, col++);
            headlineGrid.Children.Add(exitTimeText);

            // Status
            var statusText = CreateTextBlock(11, FontWeights.SemiBold);
            statusText.SetBinding(TextBlock.TextProperty, new Binding("StatusText"));
            statusText.SetBinding(TextBlock.ForegroundProperty, new Binding("Status") { Converter = new StatusToColorConverter() });
            Grid.SetColumn(statusText, col++);
            headlineGrid.Children.Add(statusText);

            // P&L
            var pnlText = CreateTextBlock(12, FontWeights.Bold);
            pnlText.SetBinding(TextBlock.TextProperty, new Binding("CombinedPnL") { StringFormat = "F2" });
            pnlText.SetBinding(TextBlock.ForegroundProperty, new Binding("CombinedPnL") { Converter = new PnLToColorConverter() });
            Grid.SetColumn(pnlText, col++);
            headlineGrid.Children.Add(pnlText);

            // Stoxxo Name
            var stoxxoNameText = CreateTextBlock(11);
            stoxxoNameText.SetBinding(TextBlock.TextProperty, new Binding("StoxxoPortfolioNameDisplay"));
            Grid.SetColumn(stoxxoNameText, col++);
            headlineGrid.Children.Add(stoxxoNameText);

            // Stoxxo Status
            var stoxxoStatusText = CreateTextBlock(11);
            stoxxoStatusText.Foreground = new SolidColorBrush(Color.FromRgb(180, 150, 220));
            stoxxoStatusText.SetBinding(TextBlock.TextProperty, new Binding("StoxxoStatusDisplay"));
            Grid.SetColumn(stoxxoStatusText, col++);
            headlineGrid.Children.Add(stoxxoStatusText);

            // Stoxxo P&L
            var stoxxoPnlText = CreateTextBlock(12, FontWeights.Bold);
            stoxxoPnlText.SetBinding(TextBlock.TextProperty, new Binding("StoxxoPnLDisplay"));
            stoxxoPnlText.SetBinding(TextBlock.ForegroundProperty, new Binding("StoxxoPnL") { Converter = new PnLToColorConverter() });
            Grid.SetColumn(stoxxoPnlText, col++);
            headlineGrid.Children.Add(stoxxoPnlText);

            // Message
            var messageText = CreateTextBlock(10);
            messageText.Foreground = new SolidColorBrush(Color.FromRgb(180, 180, 180));
            messageText.Margin = new Thickness(10, 0, 0, 0);
            messageText.TextTrimming = TextTrimming.CharacterEllipsis;
            messageText.SetBinding(TextBlock.TextProperty, new Binding("Message"));
            Grid.SetColumn(messageText, col++);
            headlineGrid.Children.Add(messageText);

            mainStack.Children.Add(headlineGrid);

            // Legs Header
            var headerBorder = new Border
            {
                Background = new SolidColorBrush(Color.FromRgb(50, 50, 55)),
                BorderBrush = TBSStyles.BorderColor,
                BorderThickness = new Thickness(1, 1, 1, 0),
                Padding = new Thickness(8, 5, 8, 5),
                Margin = new Thickness(0, 5, 0, 0),
                Cursor = System.Windows.Input.Cursors.Hand
            };

            var headerPanel = new StackPanel { Orientation = Orientation.Horizontal };
            _arrowText = new TextBlock
            {
                Text = _state.IsExpanded ? "▼" : "▶",
                Foreground = TBSStyles.FgColor,
                FontSize = 10,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 0, 8, 0)
            };
            headerPanel.Children.Add(_arrowText);

            headerPanel.Children.Add(new TextBlock
            {
                Text = "Legs",
                Foreground = TBSStyles.FgColor,
                FontWeight = FontWeights.SemiBold,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 0, 10, 0)
            });

            AddLegSummaries(headerPanel);
            headerBorder.Child = headerPanel;

            // Legs Content
            _legsContent = CreateLegsPanel();
            _legsContent.Visibility = _state.IsExpanded ? Visibility.Visible : Visibility.Collapsed;

            headerBorder.MouseLeftButtonUp += (s, e) =>
            {
                _state.IsExpanded = !_state.IsExpanded;
                _legsContent.Visibility = _state.IsExpanded ? Visibility.Visible : Visibility.Collapsed;
                _arrowText.Text = _state.IsExpanded ? "▼" : "▶";
            };

            mainStack.Children.Add(headerBorder);
            mainStack.Children.Add(_legsContent);

            border.Child = mainStack;
            return border;
        }

        private TextBlock CreateTextBlock(double fontSize, FontWeight? fontWeight = null)
        {
            return new TextBlock
            {
                FontSize = fontSize,
                FontWeight = fontWeight ?? FontWeights.Normal,
                VerticalAlignment = VerticalAlignment.Center,
                Foreground = TBSStyles.FgColor
            };
        }

        private void AddLegSummaries(StackPanel headerPanel)
        {
            var ceLeg = _state.Legs.FirstOrDefault(l => l.OptionType == "CE");
            var peLeg = _state.Legs.FirstOrDefault(l => l.OptionType == "PE");

            if (ceLeg != null)
            {
                var ceText = CreateTextBlock(11);
                ceText.Margin = new Thickness(5, 0, 10, 0);
                ceText.SetBinding(TextBlock.TextProperty, new Binding("SymbolDisplay") { Source = ceLeg, StringFormat = "CE: {0}" });
                ceText.Foreground = new SolidColorBrush(Color.FromRgb(100, 200, 100));
                headerPanel.Children.Add(ceText);
            }

            if (peLeg != null)
            {
                var peText = CreateTextBlock(11);
                peText.Margin = new Thickness(5, 0, 0, 0);
                peText.SetBinding(TextBlock.TextProperty, new Binding("SymbolDisplay") { Source = peLeg, StringFormat = "PE: {0}" });
                peText.Foreground = new SolidColorBrush(Color.FromRgb(220, 100, 100));
                headerPanel.Children.Add(peText);
            }
        }

        private StackPanel CreateLegsPanel()
        {
            var panel = new StackPanel
            {
                Background = TBSStyles.ExpanderBg,
                Margin = new Thickness(0, 5, 0, 0)
            };

            foreach (var leg in _state.Legs)
            {
                panel.Children.Add(new TBSLegRowControl(leg));
            }

            return panel;
        }

        public void Refresh()
        {
            // Simple bridge to force re-binding if needed
            var old = DataContext;
            DataContext = null;
            DataContext = old;
            
            _arrowText.Text = _state.IsExpanded ? "▼" : "▶";
            _legsContent.Visibility = _state.IsExpanded ? Visibility.Visible : Visibility.Collapsed;
        }
    }
}
