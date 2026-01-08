using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;

namespace ZerodhaDatafeedAdapter.AddOns.MarketAnalyzer
{
    public static class MarketAnalyzerUIHelpers
    {
        // NinjaTrader-style colors
        private static readonly SolidColorBrush _bgColor = new SolidColorBrush(Color.FromRgb(27, 27, 28));
        private static readonly SolidColorBrush _fgColor = new SolidColorBrush(Color.FromRgb(212, 212, 212));
        private static readonly SolidColorBrush _greenColor = new SolidColorBrush(Color.FromRgb(38, 166, 91));
        private static readonly SolidColorBrush _redColor = new SolidColorBrush(Color.FromRgb(207, 70, 71));
        private static readonly SolidColorBrush _headerBg = new SolidColorBrush(Color.FromRgb(37, 37, 38));
        private static readonly SolidColorBrush _borderColor = new SolidColorBrush(Color.FromRgb(51, 51, 51));
        private static readonly SolidColorBrush _selectionBg = new SolidColorBrush(Color.FromRgb(51, 51, 52));
        private static readonly FontFamily _ntFont = new FontFamily("Segoe UI");

        public static Border CreateToolbar(RoutedEventHandler onRefresh)
        {
            var border = new Border
            {
                Background = _headerBg,
                BorderBrush = _borderColor,
                BorderThickness = new Thickness(0, 0, 0, 1),
                Padding = new Thickness(5)
            };

            var panel = new StackPanel { Orientation = Orientation.Horizontal };

            var btnRefresh = new Button
            {
                Content = "Refresh",
                FontFamily = _ntFont,
                FontSize = 11,
                Padding = new Thickness(12, 4, 12, 4),
                Margin = new Thickness(0, 0, 10, 0),
                Background = new SolidColorBrush(Color.FromRgb(62, 62, 64)),
                Foreground = _fgColor,
                BorderBrush = _borderColor,
                BorderThickness = new Thickness(1)
            };
            if(onRefresh != null) btnRefresh.Click += onRefresh;
            panel.Children.Add(btnRefresh);

            panel.Children.Add(new TextBlock
            {
                Text = "Index Monitor",
                FontFamily = _ntFont,
                FontSize = 12,
                FontWeight = FontWeights.SemiBold,
                Foreground = _fgColor,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 0, 20, 0)
            });

            panel.Children.Add(new TextBlock
            {
                Text = "Prior Date:",
                FontFamily = _ntFont,
                FontSize = 11,
                Foreground = new SolidColorBrush(Color.FromRgb(150, 150, 150)),
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 0, 5, 0)
            });

            var lblPriorDate = new TextBlock
            {
                FontFamily = _ntFont,
                FontSize = 11,
                FontWeight = FontWeights.SemiBold,
                Foreground = new SolidColorBrush(Color.FromRgb(100, 180, 255)),
                VerticalAlignment = VerticalAlignment.Center
            };
            // Bind Label
            lblPriorDate.SetBinding(TextBlock.TextProperty, new Binding("PriorDateText"));
            panel.Children.Add(lblPriorDate);

            border.Child = panel;
            return border;
        }

        public static Border CreateStatusBar()
        {
            var border = new Border
            {
                Background = _headerBg,
                BorderBrush = _borderColor,
                BorderThickness = new Thickness(0, 1, 0, 0),
                Padding = new Thickness(8, 4, 8, 4)
            };

            var lblStatus = new TextBlock
            {
                FontFamily = _ntFont,
                FontSize = 11,
                Foreground = _fgColor
            };
            lblStatus.SetBinding(TextBlock.TextProperty, new Binding("StatusText"));

            border.Child = lblStatus;
            return border;
        }

        public static ListView CreateListView()
        {
            var listView = new ListView
            {
                Background = _bgColor,
                Foreground = _fgColor,
                BorderThickness = new Thickness(0),
                FontFamily = _ntFont,
                FontSize = 12,
                MaxHeight = 120
            };
            // Bind ItemsSource
            listView.SetBinding(ListView.ItemsSourceProperty, new Binding("Rows"));

            var gridView = new GridView();
            gridView.AllowsColumnReorder = true;

            gridView.Columns.Add(new GridViewColumn { Header = "Symbol", Width = 100, DisplayMemberBinding = new Binding("Symbol") });
            gridView.Columns.Add(new GridViewColumn { Header = "Last", Width = 85, DisplayMemberBinding = new Binding("Last") });
            gridView.Columns.Add(new GridViewColumn { Header = "Prior Close", Width = 85, DisplayMemberBinding = new Binding("PriorClose") });

            // Change Col
            var changeColumn = new GridViewColumn { Header = "Chg%", Width = 70 };
            var changeTemplate = new DataTemplate();
            var changeFactory = new FrameworkElementFactory(typeof(TextBlock));
            changeFactory.SetBinding(TextBlock.TextProperty, new Binding("Change"));
            changeFactory.SetValue(TextBlock.HorizontalAlignmentProperty, HorizontalAlignment.Right);
            changeFactory.SetValue(TextBlock.PaddingProperty, new Thickness(0, 0, 5, 0));
            // Color converter? Or DataTrigger? Or just property.
            // Original used explicit assignment. Now we bind.
            // ViewModel doesn't expose color.
            // We can add a simple DataTrigger or Converter.
            // Or just update Foreground property in ViewModel?
            // "IsPositive" boolean property exists!
            // Let's use a Style trigger in DataTemplate.
            var style = new Style(typeof(TextBlock));
            var triggerTrue = new DataTrigger { Binding = new Binding("IsPositive"), Value = true };
            triggerTrue.Setters.Add(new Setter(TextBlock.ForegroundProperty, _greenColor));
            var triggerFalse = new DataTrigger { Binding = new Binding("IsPositive"), Value = false };
            triggerFalse.Setters.Add(new Setter(TextBlock.ForegroundProperty, _redColor));
            style.Triggers.Add(triggerTrue);
            style.Triggers.Add(triggerFalse);
            changeFactory.SetValue(TextBlock.StyleProperty, style);
            
            changeTemplate.VisualTree = changeFactory;
            changeColumn.CellTemplate = changeTemplate;
            gridView.Columns.Add(changeColumn);

            gridView.Columns.Add(new GridViewColumn { Header = "Proj Open", Width = 85, DisplayMemberBinding = new Binding("ProjOpen") });
            gridView.Columns.Add(new GridViewColumn { Header = "Updated", Width = 70, DisplayMemberBinding = new Binding("LastUpdate") });
            gridView.Columns.Add(new GridViewColumn { Header = "Status", Width = 100, DisplayMemberBinding = new Binding("Status") });

            listView.View = gridView;

            var itemStyle = new Style(typeof(ListViewItem));
            itemStyle.Setters.Add(new Setter(ListViewItem.BackgroundProperty, _bgColor));
            itemStyle.Setters.Add(new Setter(ListViewItem.ForegroundProperty, _fgColor));
            itemStyle.Setters.Add(new Setter(ListViewItem.BorderThicknessProperty, new Thickness(0)));
            itemStyle.Setters.Add(new Setter(ListViewItem.PaddingProperty, new Thickness(2)));
            itemStyle.Setters.Add(new Setter(ListViewItem.FontFamilyProperty, _ntFont));
            
            var selTrigger = new Trigger { Property = ListViewItem.IsSelectedProperty, Value = true };
            selTrigger.Setters.Add(new Setter(ListViewItem.BackgroundProperty, _selectionBg));
            itemStyle.Triggers.Add(selTrigger);
            
            listView.ItemContainerStyle = itemStyle;

            return listView;
        }

        public static Border CreateNiftyFuturesMetricsPanel()
        {
            var border = new Border
            {
                Background = _headerBg,
                BorderBrush = _borderColor,
                BorderThickness = new Thickness(0, 1, 0, 0),
                Padding = new Thickness(8, 6, 8, 6)
            };

            var mainPanel = new StackPanel();

            var headerPanel = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 4) };
            headerPanel.Children.Add(new TextBlock
            {
                Text = "Nifty Futures Volume Profile",
                FontFamily = _ntFont,
                FontSize = 11,
                FontWeight = FontWeights.SemiBold,
                Foreground = new SolidColorBrush(Color.FromRgb(100, 180, 255)),
                VerticalAlignment = VerticalAlignment.Center
            });

            var lblStatus = new TextBlock
            {
                FontFamily = _ntFont,
                FontSize = 10,
                Foreground = new SolidColorBrush(Color.FromRgb(150, 150, 150)),
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(5, 0, 0, 0)
            };
            lblStatus.SetBinding(TextBlock.TextProperty, new Binding("VPMetrics.Status"));
            headerPanel.Children.Add(lblStatus);
            mainPanel.Children.Add(headerPanel);

            var grid = new Grid();
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

             for(int i=0; i<9; i++) grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

            // Session
            AddMetricToGrid(grid, 0, 0, "POC:", "VPMetrics.POC");
            AddMetricToGrid(grid, 0, 1, "VAH:", "VPMetrics.VAH");
            AddMetricToGrid(grid, 0, 2, "VAL:", "VPMetrics.VAL");
            
            AddMetricToGrid(grid, 1, 0, "VWAP:", "VPMetrics.VWAP");
            AddMetricToGrid(grid, 1, 1, "HVNs:", "VPMetrics.HVNCount");
            AddMetricToGrid(grid, 1, 2, "Bars:", "VPMetrics.BarCount");

            AddMetricToGrid(grid, 2, 0, "HVN↑:", "VPMetrics.HVNBuy", _greenColor);
            AddMetricToGrid(grid, 2, 1, "HVN↓:", "VPMetrics.HVNSell", _redColor);

            AddMetricToGrid(grid, 3, 0, "Rel↑:", "VPMetrics.RelHVNBuy", _greenColor);
            AddMetricToGrid(grid, 3, 1, "Rel↓:", "VPMetrics.RelHVNSell", _redColor);
            AddCumMetricToGrid(grid, 3, 2, "Cum:", "VPMetrics.CumHVNBuy", "VPMetrics.CumHVNSell");

            AddMetricToGrid(grid, 4, 0, "ValW:", "VPMetrics.ValueWidth");
            AddMetricToGrid(grid, 4, 1, "RelW:", "VPMetrics.RelValueWidth");
            AddMetricToGrid(grid, 4, 2, "CumW:", "VPMetrics.CumValueWidth");

            // Rolling
            var rollHeader = new TextBlock
            {
                Text = "Rolling VP (60m)",
                FontFamily = _ntFont,
                FontSize = 10,
                FontWeight = FontWeights.SemiBold,
                Foreground = new SolidColorBrush(Color.FromRgb(150, 150, 200)),
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 6, 0, 2)
            };
            var rollPanel = new StackPanel { Orientation = Orientation.Horizontal };
            rollPanel.Children.Add(rollHeader);
            Grid.SetRow(rollPanel, 5);
            Grid.SetColumnSpan(rollPanel, 3);
            grid.Children.Add(rollPanel);

            AddMetricToGrid(grid, 6, 0, "HVN↑:", "VPMetrics.RollingHVNBuy", _greenColor);
            AddMetricToGrid(grid, 6, 1, "HVN↓:", "VPMetrics.RollingHVNSell", _redColor);

            AddMetricToGrid(grid, 7, 0, "Rel↑:", "VPMetrics.RelRollingHVNBuy", _greenColor);
            AddMetricToGrid(grid, 7, 1, "Rel↓:", "VPMetrics.RelRollingHVNSell", _redColor);
            AddCumMetricToGrid(grid, 7, 2, "Cum:", "VPMetrics.CumRollingHVNBuy", "VPMetrics.CumRollingHVNSell");

            AddMetricToGrid(grid, 8, 0, "ValW:", "VPMetrics.RollingValueWidth");
            AddMetricToGrid(grid, 8, 1, "RelW:", "VPMetrics.RelRollingValueWidth");
            AddMetricToGrid(grid, 8, 2, "CumW:", "VPMetrics.CumRollingValueWidth");

            mainPanel.Children.Add(grid);
            border.Child = mainPanel;
            return border;
        }

        private static void AddMetricToGrid(Grid grid, int row, int col, string label, string bindingPath, SolidColorBrush valueColor = null)
        {
            var panel = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 2, 10, 2) };
            panel.Children.Add(new TextBlock
            {
                Text = label,
                FontFamily = _ntFont,
                FontSize = 11,
                Foreground = new SolidColorBrush(Color.FromRgb(150, 150, 150)),
                Width = 45
            });

            var val = new TextBlock
            {
                FontFamily = _ntFont,
                FontSize = 11,
                FontWeight = FontWeights.SemiBold,
                Foreground = valueColor ?? _fgColor // Default is _fgColor if null? Need to handle null
            };
            // Set binding
            val.SetBinding(TextBlock.TextProperty, new Binding(bindingPath));
            panel.Children.Add(val);
            
            Grid.SetRow(panel, row);
            Grid.SetColumn(panel, col);
            grid.Children.Add(panel);
        }

        private static void AddCumMetricToGrid(Grid grid, int row, int col, string label, string buyPath, string sellPath)
        {
             var panel = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 2, 10, 2) };
            panel.Children.Add(new TextBlock
            {
                Text = label,
                FontFamily = _ntFont,
                FontSize = 11,
                Foreground = new SolidColorBrush(Color.FromRgb(150, 150, 150)),
                Width = 35 // Smaller width for Cum label
            });
            
            var buy = new TextBlock
            {
                FontFamily = _ntFont,
                FontSize = 11,
                FontWeight = FontWeights.SemiBold,
                Foreground = _greenColor
            };
            buy.SetBinding(TextBlock.TextProperty, new Binding(buyPath));
            panel.Children.Add(buy);
            
            panel.Children.Add(new TextBlock
            {
                Text = "/",
                FontFamily = _ntFont,
                FontSize = 11,
                Foreground = new SolidColorBrush(Color.FromRgb(100, 100, 100)),
                Margin = new Thickness(3, 0, 3, 0)
            });

            var sell = new TextBlock
            {
                FontFamily = _ntFont,
                FontSize = 11,
                FontWeight = FontWeights.SemiBold,
                Foreground = _redColor
            };
            sell.SetBinding(TextBlock.TextProperty, new Binding(sellPath));
            panel.Children.Add(sell);

            Grid.SetRow(panel, row);
            Grid.SetColumn(panel, col);
            grid.Children.Add(panel);
        }

    }
}
