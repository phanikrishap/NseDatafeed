using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;
using System.Linq;
using ZerodhaDatafeedAdapter.AddOns.TBSManager.Converters;
using ZerodhaDatafeedAdapter.Models;

namespace ZerodhaDatafeedAdapter.AddOns.TBSManager.Controls
{
    public class TBSLegRowControl : UserControl
    {
        private TBSLegState _leg;

        public TBSLegRowControl(TBSLegState leg)
        {
            _leg = leg;
            DataContext = _leg;
            Content = BuildUI();
        }

        private Border BuildUI()
        {
            var legBorder = new Border
            {
                BorderBrush = TBSStyles.BorderColor,
                BorderThickness = new Thickness(0, 0, 0, 1),
                Padding = new Thickness(10, 8, 10, 8)
            };

            var legGrid = new Grid();

            // Order: Type, Symbol, Entry, LTP, SL, Exit, Time, P&L, Status, SL Sts, Stx Entry, Stx Exit, Stx Sts
            legGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(40) });   // Type (CE/PE)
            legGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(130) });  // Symbol
            legGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(60) });   // Entry
            legGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(60) });   // LTP
            legGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(60) });   // SL
            legGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(60) });   // Exit Price
            legGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(60) });   // Exit Time
            legGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(70) });   // P&L
            legGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(55) });   // Status
            legGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(55) });   // SL Status
            legGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(60) });   // Stoxxo Entry
            legGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(60) });   // Stoxxo Exit
            legGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(60) });   // Stoxxo Status

            int col = 0;

            // Type
            var typeText = CreateTextBlock(10, FontWeights.Bold);
            typeText.SetBinding(TextBlock.TextProperty, new Binding("OptionType"));
            typeText.Foreground = _leg.OptionType == "CE" ? new SolidColorBrush(Color.FromRgb(100, 200, 100)) : new SolidColorBrush(Color.FromRgb(220, 100, 100));
            Grid.SetColumn(typeText, col++);
            legGrid.Children.Add(typeText);

            // Symbol
            var symbolText = CreateTextBlock(10);
            symbolText.SetBinding(TextBlock.TextProperty, new Binding("SymbolDisplay"));
            Grid.SetColumn(symbolText, col++);
            legGrid.Children.Add(symbolText);

            // Entry Price
            var entryPriceText = CreateBoundTextBlock("EntryPriceDisplay", 10);
            Grid.SetColumn(entryPriceText, col++);
            legGrid.Children.Add(entryPriceText);

            // LTP
            var ltpText = CreateTextBlock(10, FontWeights.SemiBold);
            ltpText.SetBinding(TextBlock.TextProperty, new Binding("CurrentPriceDisplay"));
            Grid.SetColumn(ltpText, col++);
            legGrid.Children.Add(ltpText);

            // SL Price
            var slPriceText = CreateBoundTextBlock("SLPriceDisplay", 10);
            Grid.SetColumn(slPriceText, col++);
            legGrid.Children.Add(slPriceText);

            // Exit Price
            var exitPriceText = CreateBoundTextBlock("ExitPriceDisplay", 10);
            Grid.SetColumn(exitPriceText, col++);
            legGrid.Children.Add(exitPriceText);

            // Exit Time
            var exitTimeText = CreateBoundTextBlock("ExitTimeDisplay", 10);
            Grid.SetColumn(exitTimeText, col++);
            legGrid.Children.Add(exitTimeText);

            // P&L
            var pnlText = CreateTextBlock(11, FontWeights.Bold);
            pnlText.SetBinding(TextBlock.TextProperty, new Binding("PnLDisplay"));
            pnlText.SetBinding(TextBlock.ForegroundProperty, new Binding("PnL") { Converter = new PnLToColorConverter() });
            Grid.SetColumn(pnlText, col++);
            legGrid.Children.Add(pnlText);

            // Status
            var statusText = CreateTextBlock(9);
            statusText.SetBinding(TextBlock.TextProperty, new Binding("StatusText"));
            Grid.SetColumn(statusText, col++);
            legGrid.Children.Add(statusText);

            // SL Status
            var slStatusText = CreateTextBlock(9, FontWeights.Bold);
            slStatusText.SetBinding(TextBlock.TextProperty, new Binding("SLStatus"));
            slStatusText.SetBinding(TextBlock.ForegroundProperty, new Binding("SLStatus") { Converter = new SLStatusToColorConverter() });
            Grid.SetColumn(slStatusText, col++);
            legGrid.Children.Add(slStatusText);

            // Stoxxo Entry
            var stoxxoEntryText = CreateBoundTextBlock("StoxxoEntryPriceDisplay", 10);
            Grid.SetColumn(stoxxoEntryText, col++);
            legGrid.Children.Add(stoxxoEntryText);

            // Stoxxo Exit
            var stoxxoExitText = CreateBoundTextBlock("StoxxoExitPriceDisplay", 10);
            Grid.SetColumn(stoxxoExitText, col++);
            legGrid.Children.Add(stoxxoExitText);

            // Stoxxo Status
            var stoxxoStatusText = CreateTextBlock(9);
            stoxxoStatusText.SetBinding(TextBlock.TextProperty, new Binding("StoxxoStatusDisplay"));
            Grid.SetColumn(stoxxoStatusText, col++);
            legGrid.Children.Add(stoxxoStatusText);

            legBorder.Child = legGrid;
            return legBorder;
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

        private TextBlock CreateBoundTextBlock(string bindingPath, double fontSize)
        {
            var tb = CreateTextBlock(fontSize);
            tb.SetBinding(TextBlock.TextProperty, new Binding(bindingPath));
            return tb;
        }
    }
}
