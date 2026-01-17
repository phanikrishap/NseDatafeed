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
                Background = _bgColor,  // Black background like ticker section
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

        /// <summary>
        /// Creates the Composite Profile Metrics panel with 1D, 3D, 5D, 10D columns.
        /// Mirrors the FutBias "COMPOSITE PROFILE METRICS" table.
        /// </summary>
        public static Border CreateCompositeProfileMetricsPanel()
        {
            var border = new Border
            {
                Background = _bgColor,  // Black background like ticker section
                BorderBrush = _borderColor,
                BorderThickness = new Thickness(0, 1, 0, 0),
                Padding = new Thickness(8, 6, 8, 6)
            };

            var mainPanel = new StackPanel();

            // Header
            var headerPanel = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 4) };
            headerPanel.Children.Add(new TextBlock
            {
                Text = "Composite Profile Metrics",
                FontFamily = _ntFont,
                FontSize = 11,
                FontWeight = FontWeights.SemiBold,
                Foreground = new SolidColorBrush(Color.FromRgb(255, 180, 100)),
                VerticalAlignment = VerticalAlignment.Center
            });

            var lblBarCount = new TextBlock
            {
                FontFamily = _ntFont,
                FontSize = 10,
                Foreground = new SolidColorBrush(Color.FromRgb(150, 150, 150)),
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(10, 0, 0, 0)
            };
            lblBarCount.SetBinding(TextBlock.TextProperty, new Binding("VPMetrics.DailyBarCount") { StringFormat = "({0} daily bars)" });
            headerPanel.Children.Add(lblBarCount);
            mainPanel.Children.Add(headerPanel);

            // Grid with row headers + 4 columns (1D, 3D, 5D, 10D)
            var grid = new Grid();
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(60) }); // Row labels
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) }); // 1D
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) }); // 3D
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) }); // 5D
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) }); // 10D

            // 0=header, 1=POC, 2=VAH, 3=VAL, 4=CompRng, 5=CVsAvg, 6=RollRng, 7=RVsAvg,
            // 8=PriorEOD header, 9=D2Rng, 10=D2%, 11=D3Rng, 12=D3%, 13=D4Rng, 14=D4%, 15=yearly
            for (int i = 0; i < 16; i++) grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

            // Column headers with date ranges
            AddCompHeaderCellWithDate(grid, 0, 1, "1D", "VPMetrics.DateRange1D");
            AddCompHeaderCellWithDate(grid, 0, 2, "3D", "VPMetrics.DateRange3D");
            AddCompHeaderCellWithDate(grid, 0, 3, "5D", "VPMetrics.DateRange5D");
            AddCompHeaderCellWithDate(grid, 0, 4, "10D", "VPMetrics.DateRange10D");

            // POC row
            AddCompRowLabel(grid, 1, "POC");
            AddCompValueCell(grid, 1, 1, "VPMetrics.CompPOC1D");
            AddCompValueCell(grid, 1, 2, "VPMetrics.CompPOC3D");
            AddCompValueCell(grid, 1, 3, "VPMetrics.CompPOC5D");
            AddCompValueCell(grid, 1, 4, "VPMetrics.CompPOC10D");

            // VAH row
            AddCompRowLabel(grid, 2, "VAH");
            AddCompValueCell(grid, 2, 1, "VPMetrics.CompVAH1D");
            AddCompValueCell(grid, 2, 2, "VPMetrics.CompVAH3D");
            AddCompValueCell(grid, 2, 3, "VPMetrics.CompVAH5D");
            AddCompValueCell(grid, 2, 4, "VPMetrics.CompVAH10D");

            // VAL row
            AddCompRowLabel(grid, 3, "VAL");
            AddCompValueCell(grid, 3, 1, "VPMetrics.CompVAL1D");
            AddCompValueCell(grid, 3, 2, "VPMetrics.CompVAL3D");
            AddCompValueCell(grid, 3, 3, "VPMetrics.CompVAL5D");
            AddCompValueCell(grid, 3, 4, "VPMetrics.CompVAL10D");

            // Comp Rng row
            AddCompRowLabel(grid, 4, "Comp Rng");
            AddCompValueCell(grid, 4, 1, "VPMetrics.CompRng1D");
            AddCompValueCell(grid, 4, 2, "VPMetrics.CompRng3D");
            AddCompValueCell(grid, 4, 3, "VPMetrics.CompRng5D");
            AddCompValueCell(grid, 4, 4, "VPMetrics.CompRng10D");

            // C vs Avg row
            AddCompRowLabel(grid, 5, "C vs Avg");
            AddCompValueCell(grid, 5, 1, "VPMetrics.CVsAvg1D");
            AddCompValueCell(grid, 5, 2, "VPMetrics.CVsAvg3D");
            AddCompValueCell(grid, 5, 3, "VPMetrics.CVsAvg5D");
            AddCompValueCell(grid, 5, 4, "VPMetrics.CVsAvg10D");

            // Roll Rng row
            AddCompRowLabel(grid, 6, "Roll Rng");
            AddCompValueCell(grid, 6, 1, "VPMetrics.RollRng1D");
            AddCompValueCell(grid, 6, 2, "VPMetrics.RollRng3D");
            AddCompValueCell(grid, 6, 3, "VPMetrics.RollRng5D");
            AddCompValueCell(grid, 6, 4, "VPMetrics.RollRng10D");

            // R vs Avg row
            AddCompRowLabel(grid, 7, "R vs Avg");
            AddCompValueCell(grid, 7, 1, "VPMetrics.RVsAvg1D");
            AddCompValueCell(grid, 7, 2, "VPMetrics.RVsAvg3D");
            AddCompValueCell(grid, 7, 3, "VPMetrics.RVsAvg5D");
            AddCompValueCell(grid, 7, 4, "VPMetrics.RVsAvg10D");

            // ═══════════════════════════════════════════════════════════════════
            // PRIOR EOD SECTION
            // ═══════════════════════════════════════════════════════════════════

            // Prior EOD header
            var priorEodHeader = new TextBlock
            {
                Text = "PRIOR EOD",
                FontFamily = _ntFont,
                FontSize = 10,
                FontWeight = FontWeights.SemiBold,
                Foreground = new SolidColorBrush(Color.FromRgb(150, 150, 200)),
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 6, 0, 2)
            };
            Grid.SetRow(priorEodHeader, 8);
            Grid.SetColumnSpan(priorEodHeader, 5);
            grid.Children.Add(priorEodHeader);

            // D-2 Rng row
            AddCompRowLabel(grid, 9, "D-2 Rng");
            AddCompValueCell(grid, 9, 1, "VPMetrics.D2Rng1D");
            AddCompValueCell(grid, 9, 2, "VPMetrics.D2Rng3D");
            AddCompValueCell(grid, 9, 3, "VPMetrics.D2Rng5D");
            AddCompValueCell(grid, 9, 4, "VPMetrics.D2Rng10D");

            // D-2 % row
            AddCompRowLabel(grid, 10, "D-2 %");
            AddCompValueCell(grid, 10, 1, "VPMetrics.D2Pct1D");
            AddCompValueCell(grid, 10, 2, "VPMetrics.D2Pct3D");
            AddCompValueCell(grid, 10, 3, "VPMetrics.D2Pct5D");
            AddCompValueCell(grid, 10, 4, "VPMetrics.D2Pct10D");

            // D-3 Rng row
            AddCompRowLabel(grid, 11, "D-3 Rng");
            AddCompValueCell(grid, 11, 1, "VPMetrics.D3Rng1D");
            AddCompValueCell(grid, 11, 2, "VPMetrics.D3Rng3D");
            AddCompValueCell(grid, 11, 3, "VPMetrics.D3Rng5D");
            AddCompValueCell(grid, 11, 4, "VPMetrics.D3Rng10D");

            // D-3 % row
            AddCompRowLabel(grid, 12, "D-3 %");
            AddCompValueCell(grid, 12, 1, "VPMetrics.D3Pct1D");
            AddCompValueCell(grid, 12, 2, "VPMetrics.D3Pct3D");
            AddCompValueCell(grid, 12, 3, "VPMetrics.D3Pct5D");
            AddCompValueCell(grid, 12, 4, "VPMetrics.D3Pct10D");

            // D-4 Rng row
            AddCompRowLabel(grid, 13, "D-4 Rng");
            AddCompValueCell(grid, 13, 1, "VPMetrics.D4Rng1D");
            AddCompValueCell(grid, 13, 2, "VPMetrics.D4Rng3D");
            AddCompValueCell(grid, 13, 3, "VPMetrics.D4Rng5D");
            AddCompValueCell(grid, 13, 4, "VPMetrics.D4Rng10D");

            // D-4 % row
            AddCompRowLabel(grid, 14, "D-4 %");
            AddCompValueCell(grid, 14, 1, "VPMetrics.D4Pct1D");
            AddCompValueCell(grid, 14, 2, "VPMetrics.D4Pct3D");
            AddCompValueCell(grid, 14, 3, "VPMetrics.D4Pct5D");
            AddCompValueCell(grid, 14, 4, "VPMetrics.D4Pct10D");

            // Yearly High/Low row
            var yearlyPanel = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 6, 0, 0) };
            yearlyPanel.Children.Add(CreateYearlyLabel("52W High:"));
            yearlyPanel.Children.Add(CreateYearlyValue("VPMetrics.YearlyHigh", _greenColor));
            yearlyPanel.Children.Add(CreateYearlyLabel(" ("));
            yearlyPanel.Children.Add(CreateYearlyValue("VPMetrics.YearlyHighDate", null));
            yearlyPanel.Children.Add(CreateYearlyLabel(")  |  52W Low:"));
            yearlyPanel.Children.Add(CreateYearlyValue("VPMetrics.YearlyLow", _redColor));
            yearlyPanel.Children.Add(CreateYearlyLabel(" ("));
            yearlyPanel.Children.Add(CreateYearlyValue("VPMetrics.YearlyLowDate", null));
            yearlyPanel.Children.Add(CreateYearlyLabel(")  |  Control: "));
            yearlyPanel.Children.Add(CreateYearlyValue("VPMetrics.Control", null));
            yearlyPanel.Children.Add(CreateYearlyLabel(" | Migration: "));
            yearlyPanel.Children.Add(CreateYearlyValue("VPMetrics.Migration", null));

            Grid.SetRow(yearlyPanel, 15);
            Grid.SetColumnSpan(yearlyPanel, 5);
            grid.Children.Add(yearlyPanel);

            mainPanel.Children.Add(grid);
            border.Child = mainPanel;
            return border;
        }

        private static void AddCompHeaderCell(Grid grid, int row, int col, string text)
        {
            var tb = new TextBlock
            {
                Text = text,
                FontFamily = _ntFont,
                FontSize = 10,
                FontWeight = FontWeights.SemiBold,
                Foreground = new SolidColorBrush(Color.FromRgb(180, 180, 180)),
                HorizontalAlignment = HorizontalAlignment.Center,
                Margin = new Thickness(0, 2, 0, 2)
            };
            Grid.SetRow(tb, row);
            Grid.SetColumn(tb, col);
            grid.Children.Add(tb);
        }

        /// <summary>
        /// Adds a composite header cell with a label and a data-bound date range.
        /// Shows as "1D\n(13-Jan)" or "3D\n(10-Jan to 13-Jan)"
        /// </summary>
        private static void AddCompHeaderCellWithDate(Grid grid, int row, int col, string label, string dateBindingPath)
        {
            var panel = new StackPanel
            {
                Orientation = Orientation.Vertical,
                HorizontalAlignment = HorizontalAlignment.Center,
                Margin = new Thickness(0, 2, 0, 2)
            };

            // Label (1D, 3D, etc.)
            var labelTb = new TextBlock
            {
                Text = label,
                FontFamily = _ntFont,
                FontSize = 10,
                FontWeight = FontWeights.SemiBold,
                Foreground = new SolidColorBrush(Color.FromRgb(180, 180, 180)),
                HorizontalAlignment = HorizontalAlignment.Center
            };
            panel.Children.Add(labelTb);

            // Date range (bound)
            var dateTb = new TextBlock
            {
                FontFamily = _ntFont,
                FontSize = 8,
                Foreground = new SolidColorBrush(Color.FromRgb(130, 130, 130)),
                HorizontalAlignment = HorizontalAlignment.Center
            };
            dateTb.SetBinding(TextBlock.TextProperty, new Binding(dateBindingPath));
            panel.Children.Add(dateTb);

            Grid.SetRow(panel, row);
            Grid.SetColumn(panel, col);
            grid.Children.Add(panel);
        }

        private static void AddCompRowLabel(Grid grid, int row, string label)
        {
            var tb = new TextBlock
            {
                Text = label,
                FontFamily = _ntFont,
                FontSize = 10,
                Foreground = new SolidColorBrush(Color.FromRgb(150, 150, 150)),
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 2, 5, 2)
            };
            Grid.SetRow(tb, row);
            Grid.SetColumn(tb, 0);
            grid.Children.Add(tb);
        }

        private static void AddCompValueCell(Grid grid, int row, int col, string bindingPath)
        {
            var tb = new TextBlock
            {
                FontFamily = _ntFont,
                FontSize = 10,
                FontWeight = FontWeights.SemiBold,
                Foreground = _fgColor,
                HorizontalAlignment = HorizontalAlignment.Center,
                Margin = new Thickness(0, 2, 0, 2)
            };
            tb.SetBinding(TextBlock.TextProperty, new Binding(bindingPath));
            Grid.SetRow(tb, row);
            Grid.SetColumn(tb, col);
            grid.Children.Add(tb);
        }

        private static TextBlock CreateYearlyLabel(string text)
        {
            return new TextBlock
            {
                Text = text,
                FontFamily = _ntFont,
                FontSize = 10,
                Foreground = new SolidColorBrush(Color.FromRgb(150, 150, 150)),
                VerticalAlignment = VerticalAlignment.Center
            };
        }

        private static TextBlock CreateYearlyValue(string bindingPath, SolidColorBrush color)
        {
            var tb = new TextBlock
            {
                FontFamily = _ntFont,
                FontSize = 10,
                FontWeight = FontWeights.SemiBold,
                Foreground = color ?? _fgColor,
                VerticalAlignment = VerticalAlignment.Center
            };
            tb.SetBinding(TextBlock.TextProperty, new Binding(bindingPath));
            return tb;
        }
    }
}
