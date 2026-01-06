using System;
using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Media;
using ZerodhaDatafeedAdapter.AddOns.MarketAnalyzer.Converters;
using ZerodhaDatafeedAdapter.AddOns.MarketAnalyzer.Models;

// Alias to avoid ambiguity with System.Windows.Controls.DataGridTemplateColumn
using WpfDataGridTemplateColumn = System.Windows.Controls.DataGridTemplateColumn;

namespace ZerodhaDatafeedAdapter.AddOns.MarketAnalyzer.Controls
{
    public class OptionChainListView : UserControl
    {
        private FilterDataGrid.FilterDataGrid _dataGrid;

        private static readonly SolidColorBrush _bgColor = new SolidColorBrush(Color.FromRgb(27, 27, 28));
        private static readonly SolidColorBrush _fgColor = new SolidColorBrush(Color.FromRgb(212, 212, 212));
        private static readonly SolidColorBrush _headerBg = new SolidColorBrush(Color.FromRgb(37, 37, 38));
        private static readonly SolidColorBrush _borderColor = new SolidColorBrush(Color.FromRgb(51, 51, 51));
        private static readonly SolidColorBrush _atmBg = new SolidColorBrush(Color.FromRgb(60, 80, 60));
        private static readonly SolidColorBrush _strikeBg = new SolidColorBrush(Color.FromRgb(45, 45, 46));
        private static readonly FontFamily _ntFont = new FontFamily("Segoe UI");

        private static readonly SolidColorBrush _ceHistogramBrush = new SolidColorBrush(Color.FromArgb(180, 38, 166, 91));
        private static readonly SolidColorBrush _peHistogramBrush = new SolidColorBrush(Color.FromArgb(180, 207, 70, 71));

        public ObservableCollection<OptionChainRow> ItemsSource
        {
            get => _dataGrid.ItemsSource as ObservableCollection<OptionChainRow>;
            set => _dataGrid.ItemsSource = value;
        }

        public OptionChainRow SelectedItem => _dataGrid.SelectedItem as OptionChainRow;

        public new event System.Windows.Input.MouseButtonEventHandler MouseLeftButtonUpEvent;

        public OptionChainListView()
        {
            Content = BuildUI();

            _dataGrid.MouseLeftButtonUp += (s, e) => MouseLeftButtonUpEvent?.Invoke(s, e);
        }

        private FilterDataGrid.FilterDataGrid BuildUI()
        {
            _dataGrid = new FilterDataGrid.FilterDataGrid
            {
                Background = _bgColor,
                Foreground = _fgColor,
                BorderThickness = new Thickness(0),
                BorderBrush = null,
                FontFamily = _ntFont,
                FontSize = 12,
                AutoGenerateColumns = false,
                CanUserAddRows = false,
                CanUserDeleteRows = false,
                IsReadOnly = true,
                SelectionMode = DataGridSelectionMode.Single,
                SelectionUnit = DataGridSelectionUnit.FullRow,
                HeadersVisibility = DataGridHeadersVisibility.Column,
                GridLinesVisibility = DataGridGridLinesVisibility.None,
                RowBackground = _bgColor,
                AlternatingRowBackground = new SolidColorBrush(Color.FromRgb(32, 32, 33)),
                HorizontalGridLinesBrush = _borderColor,
                VerticalGridLinesBrush = _borderColor,
                // FilterDataGrid specific - enable filtering
                ShowStatusBar = false,
                ShowElapsedTime = false,
                FilterLanguage = FilterDataGrid.Local.English,
                DateFormatString = "dd-MMM-yyyy"
            };

            // Style the resources for selection colors
            _dataGrid.Resources.Add(SystemColors.HighlightBrushKey, new SolidColorBrush(Color.FromRgb(60, 60, 65)));
            _dataGrid.Resources.Add(SystemColors.HighlightTextBrushKey, new SolidColorBrush(Color.FromRgb(255, 255, 255)));
            _dataGrid.Resources.Add(SystemColors.InactiveSelectionHighlightBrushKey, new SolidColorBrush(Color.FromRgb(50, 50, 55)));
            _dataGrid.Resources.Add(SystemColors.InactiveSelectionHighlightTextBrushKey, new SolidColorBrush(Color.FromRgb(220, 220, 220)));

            // Create header style
            var headerStyle = CreateHeaderStyle();

            // Create columns - FilterDataGrid enables filtering automatically on columns
            // CE Side columns (Left)
            _dataGrid.Columns.Add(CreateTextColumn("Time", "CEUpdateTime", 60, headerStyle));
            _dataGrid.Columns.Add(CreateTextColumn("CE Status", "CEStatus", 90, headerStyle));
            _dataGrid.Columns.Add(CreateVWAPColumn("CE VWAP", "CEVWAPDisplay", "CEVWAPComparison", 55, true, headerStyle));
            _dataGrid.Columns.Add(CreateHistogramColumn("CE LTP", "CELast", "CEHistogramWidth", 90, true, headerStyle));

            // Strike column (center)
            _dataGrid.Columns.Add(CreateStrikeColumn(headerStyle));

            // PE Side columns (Right)
            _dataGrid.Columns.Add(CreateHistogramColumn("PE LTP", "PELast", "PEHistogramWidth", 90, false, headerStyle));
            _dataGrid.Columns.Add(CreateVWAPColumn("PE VWAP", "PEVWAPDisplay", "PEVWAPComparison", 55, false, headerStyle));
            _dataGrid.Columns.Add(CreateTextColumn("PE Status", "PEStatus", 90, headerStyle));
            _dataGrid.Columns.Add(CreateTextColumn("Time", "PEUpdateTime", 60, headerStyle));

            // Straddle columns
            _dataGrid.Columns.Add(CreateStraddleColumn(headerStyle));
            _dataGrid.Columns.Add(CreateVWAPColumn("Str VWAP", "StraddleVWAPDisplay", "StraddleVWAPComparison", 60, false, headerStyle));

            // Row style with ATM highlighting
            _dataGrid.RowStyle = CreateRowStyle();

            return _dataGrid;
        }

        private Style CreateHeaderStyle()
        {
            var headerStyle = new Style(typeof(DataGridColumnHeader));
            headerStyle.Setters.Add(new Setter(DataGridColumnHeader.BackgroundProperty, _headerBg));
            headerStyle.Setters.Add(new Setter(DataGridColumnHeader.ForegroundProperty, _fgColor));
            headerStyle.Setters.Add(new Setter(DataGridColumnHeader.FontWeightProperty, FontWeights.Bold));
            headerStyle.Setters.Add(new Setter(DataGridColumnHeader.FontFamilyProperty, _ntFont));
            headerStyle.Setters.Add(new Setter(DataGridColumnHeader.FontSizeProperty, 12.0));
            headerStyle.Setters.Add(new Setter(DataGridColumnHeader.PaddingProperty, new Thickness(5, 4, 5, 4)));
            headerStyle.Setters.Add(new Setter(DataGridColumnHeader.BorderThicknessProperty, new Thickness(0, 0, 1, 1)));
            headerStyle.Setters.Add(new Setter(DataGridColumnHeader.BorderBrushProperty, new SolidColorBrush(Color.FromRgb(50, 50, 52))));
            headerStyle.Setters.Add(new Setter(DataGridColumnHeader.HorizontalContentAlignmentProperty, HorizontalAlignment.Center));
            return headerStyle;
        }

        private Style CreateRowStyle()
        {
            var style = new Style(typeof(DataGridRow));
            style.Setters.Add(new Setter(DataGridRow.ForegroundProperty, _fgColor));
            style.Setters.Add(new Setter(DataGridRow.FontFamilyProperty, _ntFont));
            style.Setters.Add(new Setter(DataGridRow.BackgroundProperty, _bgColor));
            style.Setters.Add(new Setter(DataGridRow.BorderThicknessProperty, new Thickness(0)));

            // ATM trigger - highlight the ATM strike row
            var atmTrigger = new DataTrigger { Binding = new Binding("IsATM"), Value = true };
            atmTrigger.Setters.Add(new Setter(DataGridRow.BackgroundProperty, _atmBg));
            atmTrigger.Setters.Add(new Setter(DataGridRow.FontWeightProperty, FontWeights.Bold));
            style.Triggers.Add(atmTrigger);

            // Selected trigger
            var selectedTrigger = new Trigger { Property = DataGridRow.IsSelectedProperty, Value = true };
            selectedTrigger.Setters.Add(new Setter(DataGridRow.BackgroundProperty, new SolidColorBrush(Color.FromRgb(60, 60, 65))));
            style.Triggers.Add(selectedTrigger);

            // Mouse over trigger
            var mouseOverTrigger = new Trigger { Property = DataGridRow.IsMouseOverProperty, Value = true };
            mouseOverTrigger.Setters.Add(new Setter(DataGridRow.BackgroundProperty, new SolidColorBrush(Color.FromRgb(50, 50, 55))));
            style.Triggers.Add(mouseOverTrigger);

            return style;
        }

        private WpfDataGridTemplateColumn CreateTextColumn(string header, string binding, double width, Style headerStyle)
        {
            var column = new WpfDataGridTemplateColumn
            {
                Header = header,
                Width = new DataGridLength(width),
                HeaderStyle = headerStyle,
                IsReadOnly = true
            };

            var template = new DataTemplate();
            var factory = new FrameworkElementFactory(typeof(TextBlock));
            factory.SetBinding(TextBlock.TextProperty, new Binding(binding));
            factory.SetValue(TextBlock.HorizontalAlignmentProperty, HorizontalAlignment.Center);
            factory.SetValue(TextBlock.VerticalAlignmentProperty, VerticalAlignment.Center);
            factory.SetValue(TextBlock.PaddingProperty, new Thickness(3, 2, 3, 2));
            template.VisualTree = factory;
            column.CellTemplate = template;

            return column;
        }

        private WpfDataGridTemplateColumn CreateStrikeColumn(Style headerStyle)
        {
            var column = new WpfDataGridTemplateColumn
            {
                Header = "Strike",
                Width = new DataGridLength(80),
                HeaderStyle = headerStyle,
                IsReadOnly = true
            };

            var template = new DataTemplate();
            var borderFactory = new FrameworkElementFactory(typeof(Border));
            borderFactory.SetValue(Border.BackgroundProperty, _strikeBg);
            borderFactory.SetValue(Border.PaddingProperty, new Thickness(5, 2, 5, 2));

            var textFactory = new FrameworkElementFactory(typeof(TextBlock));
            textFactory.SetBinding(TextBlock.TextProperty, new Binding("StrikeDisplay"));
            textFactory.SetValue(TextBlock.FontWeightProperty, FontWeights.Bold);
            textFactory.SetValue(TextBlock.HorizontalAlignmentProperty, HorizontalAlignment.Center);
            textFactory.SetValue(TextBlock.ForegroundProperty, new SolidColorBrush(Color.FromRgb(255, 255, 255)));

            borderFactory.AppendChild(textFactory);
            template.VisualTree = borderFactory;
            column.CellTemplate = template;

            return column;
        }

        private WpfDataGridTemplateColumn CreateStraddleColumn(Style headerStyle)
        {
            var column = new WpfDataGridTemplateColumn
            {
                Header = "Straddle",
                Width = new DataGridLength(75),
                HeaderStyle = headerStyle,
                IsReadOnly = true
            };

            var template = new DataTemplate();
            var factory = new FrameworkElementFactory(typeof(TextBlock));
            factory.SetBinding(TextBlock.TextProperty, new Binding("StraddlePrice"));
            factory.SetValue(TextBlock.HorizontalAlignmentProperty, HorizontalAlignment.Center);
            factory.SetValue(TextBlock.VerticalAlignmentProperty, VerticalAlignment.Center);
            factory.SetValue(TextBlock.PaddingProperty, new Thickness(2));
            factory.SetValue(TextBlock.FontWeightProperty, FontWeights.Bold);
            factory.SetValue(TextBlock.ForegroundProperty, new SolidColorBrush(Color.FromRgb(255, 255, 255)));

            template.VisualTree = factory;
            column.CellTemplate = template;

            return column;
        }

        private WpfDataGridTemplateColumn CreateHistogramColumn(string header, string priceBinding, string widthBinding, double columnWidth, bool isCall, Style headerStyle)
        {
            var column = new WpfDataGridTemplateColumn
            {
                Header = header,
                Width = new DataGridLength(columnWidth),
                HeaderStyle = headerStyle,
                IsReadOnly = true
            };

            var template = new DataTemplate();
            var gridFactory = new FrameworkElementFactory(typeof(Grid));
            gridFactory.SetValue(Grid.HeightProperty, 22.0);
            gridFactory.SetValue(Grid.VerticalAlignmentProperty, VerticalAlignment.Center);

            // Histogram bar
            var barFactory = new FrameworkElementFactory(typeof(Border));
            barFactory.SetValue(Border.BackgroundProperty, isCall ? _ceHistogramBrush : _peHistogramBrush);
            barFactory.SetValue(Border.HorizontalAlignmentProperty, isCall ? HorizontalAlignment.Right : HorizontalAlignment.Left);
            barFactory.SetValue(Border.VerticalAlignmentProperty, VerticalAlignment.Stretch);
            barFactory.SetValue(Border.CornerRadiusProperty, new CornerRadius(2));
            barFactory.SetValue(Border.MarginProperty, new Thickness(1));

            barFactory.SetBinding(Border.WidthProperty, new Binding(widthBinding)
            {
                Converter = new PercentageToWidthConverter(),
                ConverterParameter = (columnWidth - 4).ToString()
            });

            gridFactory.AppendChild(barFactory);

            // Price text overlay
            var textFactory = new FrameworkElementFactory(typeof(TextBlock));
            textFactory.SetBinding(TextBlock.TextProperty, new Binding(priceBinding));
            textFactory.SetValue(TextBlock.HorizontalAlignmentProperty, HorizontalAlignment.Center);
            textFactory.SetValue(TextBlock.VerticalAlignmentProperty, VerticalAlignment.Center);
            textFactory.SetValue(TextBlock.PaddingProperty, new Thickness(5, 0, 5, 0));
            textFactory.SetValue(TextBlock.ForegroundProperty, new SolidColorBrush(Colors.White));
            textFactory.SetValue(TextBlock.FontWeightProperty, FontWeights.SemiBold);

            gridFactory.AppendChild(textFactory);
            template.VisualTree = gridFactory;
            column.CellTemplate = template;

            return column;
        }

        private WpfDataGridTemplateColumn CreateVWAPColumn(string header, string vwapBinding, string comparisonBinding, double columnWidth, bool isCall, Style headerStyle)
        {
            var column = new WpfDataGridTemplateColumn
            {
                Header = header,
                Width = new DataGridLength(columnWidth),
                HeaderStyle = headerStyle,
                IsReadOnly = true
            };

            var template = new DataTemplate();
            var gridFactory = new FrameworkElementFactory(typeof(Grid));
            gridFactory.SetValue(Grid.HorizontalAlignmentProperty, HorizontalAlignment.Stretch);

            var vwapText = new FrameworkElementFactory(typeof(TextBlock));
            vwapText.SetBinding(TextBlock.TextProperty, new Binding(vwapBinding));
            vwapText.SetValue(TextBlock.HorizontalAlignmentProperty, HorizontalAlignment.Center);
            vwapText.SetValue(TextBlock.VerticalAlignmentProperty, VerticalAlignment.Center);
            vwapText.SetValue(TextBlock.FontSizeProperty, 11.0);
            vwapText.SetValue(TextBlock.TextAlignmentProperty, TextAlignment.Center);

            if (!string.IsNullOrEmpty(comparisonBinding))
            {
                vwapText.SetBinding(TextBlock.ForegroundProperty, new Binding(comparisonBinding) { Converter = new VWAPComparisonToColorConverter() });
            }
            else
            {
                vwapText.SetValue(TextBlock.ForegroundProperty, new SolidColorBrush(Color.FromRgb(255, 220, 100)));
            }

            gridFactory.AppendChild(vwapText);
            template.VisualTree = gridFactory;
            column.CellTemplate = template;

            return column;
        }

        public void Refresh()
        {
            _dataGrid.Items.Refresh();
        }
    }
}
