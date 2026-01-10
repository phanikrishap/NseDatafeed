using System;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Media;
using ZerodhaDatafeedAdapter.AddOns.OptionSignals.Models;

// Alias FilterDataGrid column types
using FilterTextColumn = FilterDataGrid.DataGridTextColumn;
using FilterTemplateColumn = FilterDataGrid.DataGridTemplateColumn;

namespace ZerodhaDatafeedAdapter.AddOns.OptionSignals.Controls
{
    /// <summary>
    /// Converts P&L value to color (green for positive, red for negative).
    /// </summary>
    public class PnLToColorConverter : IValueConverter
    {
        private static readonly SolidColorBrush _positiveColor = new SolidColorBrush(Color.FromRgb(38, 166, 91));  // Green
        private static readonly SolidColorBrush _negativeColor = new SolidColorBrush(Color.FromRgb(207, 70, 71)); // Red
        private static readonly SolidColorBrush _neutralColor = new SolidColorBrush(Color.FromRgb(150, 150, 150)); // Gray

        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            if (value is double pnl)
            {
                if (pnl > 0) return _positiveColor;
                if (pnl < 0) return _negativeColor;
            }
            return _neutralColor;
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
            => throw new NotImplementedException();
    }

    /// <summary>
    /// Converts SignalStatus to color.
    /// </summary>
    public class StatusToColorConverter : IValueConverter
    {
        private static readonly SolidColorBrush _pendingColor = new SolidColorBrush(Color.FromRgb(255, 193, 7));  // Amber
        private static readonly SolidColorBrush _activeColor = new SolidColorBrush(Color.FromRgb(38, 166, 91));   // Green
        private static readonly SolidColorBrush _closedColor = new SolidColorBrush(Color.FromRgb(150, 150, 150)); // Gray
        private static readonly SolidColorBrush _cancelledColor = new SolidColorBrush(Color.FromRgb(207, 70, 71)); // Red

        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            if (value is SignalStatus status)
            {
                switch (status)
                {
                    case SignalStatus.Pending: return _pendingColor;
                    case SignalStatus.Active: return _activeColor;
                    case SignalStatus.Closed: return _closedColor;
                    case SignalStatus.Cancelled: return _cancelledColor;
                }
            }
            return _closedColor;
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
            => throw new NotImplementedException();
    }

    /// <summary>
    /// Converts SignalDirection to color.
    /// </summary>
    public class DirectionToColorConverter : IValueConverter
    {
        private static readonly SolidColorBrush _longColor = new SolidColorBrush(Color.FromRgb(38, 166, 91));  // Green
        private static readonly SolidColorBrush _shortColor = new SolidColorBrush(Color.FromRgb(207, 70, 71)); // Red

        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            if (value is SignalDirection direction)
            {
                return direction == SignalDirection.Long ? _longColor : _shortColor;
            }
            return _longColor;
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
            => throw new NotImplementedException();
    }

    /// <summary>
    /// FilterDataGrid view for displaying trading signals with Excel-style column filtering.
    /// Uses FilterDataGrid library for advanced filtering capabilities.
    /// </summary>
    public class SignalsListView : UserControl
    {
        private FilterDataGrid.FilterDataGrid _dataGrid;

        private static readonly SolidColorBrush _bgColor = new SolidColorBrush(Color.FromRgb(27, 27, 28));
        private static readonly SolidColorBrush _fgColor = new SolidColorBrush(Color.FromRgb(212, 212, 212));
        private static readonly SolidColorBrush _headerBg = new SolidColorBrush(Color.FromRgb(37, 37, 38));
        private static readonly SolidColorBrush _rowBg = new SolidColorBrush(Color.FromRgb(30, 30, 31));
        private static readonly SolidColorBrush _altRowBg = new SolidColorBrush(Color.FromRgb(35, 35, 36));
        private static readonly SolidColorBrush _borderColor = new SolidColorBrush(Color.FromRgb(60, 60, 60));
        private static readonly FontFamily _ntFont = new FontFamily("Segoe UI");

        // Converters
        private static readonly PnLToColorConverter _pnlColorConverter = new PnLToColorConverter();
        private static readonly StatusToColorConverter _statusColorConverter = new StatusToColorConverter();
        private static readonly DirectionToColorConverter _directionColorConverter = new DirectionToColorConverter();

        // Column widths
        private const double COL_TIME = 105;
        private const double COL_ORDERID = 60;
        private const double COL_SYMBOL = 120;
        private const double COL_STRIKE = 55;
        private const double COL_TYPE = 35;
        private const double COL_MONEYNESS = 55;
        private const double COL_DIR = 45;
        private const double COL_STATUS = 60;
        private const double COL_QTY = 35;
        private const double COL_PRICE = 60;
        private const double COL_PNL = 75;
        private const double COL_STRATEGY = 85;
        private const double COL_REASON = 180;

        public ObservableCollection<SignalRow> ItemsSource
        {
            get => _dataGrid.ItemsSource as ObservableCollection<SignalRow>;
            set => _dataGrid.ItemsSource = value;
        }

        public SignalRow SelectedItem => _dataGrid.SelectedItem as SignalRow;

        public SignalsListView()
        {
            Content = BuildUI();
        }

        private UIElement BuildUI()
        {
            var mainGrid = new Grid { Background = _bgColor };
            mainGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto }); // Header
            mainGrid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) }); // DataGrid

            // Summary header
            var summaryPanel = CreateSummaryHeader();
            Grid.SetRow(summaryPanel, 0);
            mainGrid.Children.Add(summaryPanel);

            // FilterDataGrid
            _dataGrid = CreateFilterDataGrid();
            Grid.SetRow(_dataGrid, 1);
            mainGrid.Children.Add(_dataGrid);

            return mainGrid;
        }

        private StackPanel CreateSummaryHeader()
        {
            var panel = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Background = _headerBg,
                Height = 28
            };

            panel.Children.Add(new TextBlock
            {
                Text = "Signals",
                Foreground = _fgColor,
                FontWeight = FontWeights.Bold,
                FontSize = 12,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(10, 0, 20, 0)
            });

            return panel;
        }

        private FilterDataGrid.FilterDataGrid CreateFilterDataGrid()
        {
            var dataGrid = new FilterDataGrid.FilterDataGrid
            {
                AutoGenerateColumns = false,
                IsReadOnly = true,
                Background = _bgColor,
                Foreground = _fgColor,
                BorderBrush = _borderColor,
                BorderThickness = new Thickness(0),
                GridLinesVisibility = DataGridGridLinesVisibility.None,
                HeadersVisibility = DataGridHeadersVisibility.Column,
                RowBackground = _rowBg,
                AlternatingRowBackground = _altRowBg,
                CanUserAddRows = false,
                CanUserDeleteRows = false,
                CanUserReorderColumns = true,
                CanUserResizeRows = false,
                SelectionMode = DataGridSelectionMode.Single,
                SelectionUnit = DataGridSelectionUnit.FullRow,
                RowHeight = 24,
                FontFamily = _ntFont,
                FontSize = 11,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
                // FilterDataGrid specific properties for Excel-style filtering
                ShowStatusBar = false,
                ShowElapsedTime = false,
                FilterLanguage = FilterDataGrid.Local.English,
                DateFormatString = "dd-MMM HH:mm:ss"
            };

            // Style the resources for selection colors (dark theme)
            dataGrid.Resources.Add(SystemColors.HighlightBrushKey, new SolidColorBrush(Color.FromRgb(60, 60, 65)));
            dataGrid.Resources.Add(SystemColors.HighlightTextBrushKey, new SolidColorBrush(Color.FromRgb(255, 255, 255)));
            dataGrid.Resources.Add(SystemColors.InactiveSelectionHighlightBrushKey, new SolidColorBrush(Color.FromRgb(50, 50, 55)));
            dataGrid.Resources.Add(SystemColors.InactiveSelectionHighlightTextBrushKey, new SolidColorBrush(Color.FromRgb(220, 220, 220)));

            // Create header style
            var headerStyle = CreateHeaderStyle();
            var cellStyle = CreateCellStyle();

            // Add columns using FilterDataGrid column types with IsColumnFiltered = true
            dataGrid.Columns.Add(CreateFilterTextColumn("Time", "SignalTimeStr", COL_TIME, headerStyle, cellStyle));
            dataGrid.Columns.Add(CreateFilterTextColumn("OrderId", "BridgeOrderIdStr", COL_ORDERID, headerStyle, cellStyle));
            dataGrid.Columns.Add(CreateColoredTemplateColumn("Status", "StatusStr", "Status", _statusColorConverter, COL_STATUS, headerStyle));
            dataGrid.Columns.Add(CreateColoredTemplateColumn("Dir", "DirectionStr", "Direction", _directionColorConverter, COL_DIR, headerStyle));
            dataGrid.Columns.Add(CreateFilterTextColumn("Symbol", "Symbol", COL_SYMBOL, headerStyle, cellStyle));
            dataGrid.Columns.Add(CreateFilterTextColumn("Strike", "Strike", COL_STRIKE, headerStyle, cellStyle));
            dataGrid.Columns.Add(CreateFilterTextColumn("Type", "OptionType", COL_TYPE, headerStyle, cellStyle));
            dataGrid.Columns.Add(CreateFilterTextColumn("Money", "MoneynessStr", COL_MONEYNESS, headerStyle, cellStyle));
            dataGrid.Columns.Add(CreateFilterTextColumn("Qty", "Quantity", COL_QTY, headerStyle, cellStyle));
            dataGrid.Columns.Add(CreateFilterTextColumn("Entry", "EntryPriceStr", COL_PRICE, headerStyle, cellStyle));
            dataGrid.Columns.Add(CreateFilterTextColumn("Current", "CurrentPriceStr", COL_PRICE, headerStyle, cellStyle));
            dataGrid.Columns.Add(CreateFilterTextColumn("Exit", "ExitPriceStr", COL_PRICE, headerStyle, cellStyle));
            dataGrid.Columns.Add(CreatePnLTemplateColumn("Unreal P&L", "UnrealizedPnL", COL_PNL, headerStyle));
            dataGrid.Columns.Add(CreatePnLTemplateColumn("Real P&L", "RealizedPnL", COL_PNL, headerStyle));
            dataGrid.Columns.Add(CreateFilterTextColumn("Strategy", "StrategyName", COL_STRATEGY, headerStyle, cellStyle));
            dataGrid.Columns.Add(CreateFilterTextColumn("Reason", "SignalReason", COL_REASON, headerStyle, cellStyle));

            // Row style
            dataGrid.RowStyle = CreateRowStyle();

            return dataGrid;
        }

        private Style CreateHeaderStyle()
        {
            var headerStyle = new Style(typeof(DataGridColumnHeader));
            headerStyle.Setters.Add(new Setter(DataGridColumnHeader.BackgroundProperty, _headerBg));
            headerStyle.Setters.Add(new Setter(DataGridColumnHeader.ForegroundProperty, _fgColor));
            headerStyle.Setters.Add(new Setter(DataGridColumnHeader.FontSizeProperty, 10.0));
            headerStyle.Setters.Add(new Setter(DataGridColumnHeader.FontWeightProperty, FontWeights.SemiBold));
            headerStyle.Setters.Add(new Setter(DataGridColumnHeader.FontFamilyProperty, _ntFont));
            headerStyle.Setters.Add(new Setter(DataGridColumnHeader.PaddingProperty, new Thickness(4, 3, 4, 3)));
            headerStyle.Setters.Add(new Setter(DataGridColumnHeader.HorizontalContentAlignmentProperty, HorizontalAlignment.Center));
            headerStyle.Setters.Add(new Setter(DataGridColumnHeader.BorderBrushProperty, new SolidColorBrush(Color.FromRgb(50, 50, 52))));
            headerStyle.Setters.Add(new Setter(DataGridColumnHeader.BorderThicknessProperty, new Thickness(0, 0, 1, 1)));
            return headerStyle;
        }

        private Style CreateCellStyle()
        {
            var cellStyle = new Style(typeof(TextBlock));
            cellStyle.Setters.Add(new Setter(TextBlock.TextAlignmentProperty, TextAlignment.Center));
            cellStyle.Setters.Add(new Setter(TextBlock.VerticalAlignmentProperty, VerticalAlignment.Center));
            cellStyle.Setters.Add(new Setter(TextBlock.PaddingProperty, new Thickness(3, 2, 3, 2)));
            return cellStyle;
        }

        private Style CreateRowStyle()
        {
            var style = new Style(typeof(DataGridRow));
            style.Setters.Add(new Setter(DataGridRow.ForegroundProperty, _fgColor));
            style.Setters.Add(new Setter(DataGridRow.FontFamilyProperty, _ntFont));
            style.Setters.Add(new Setter(DataGridRow.BackgroundProperty, _rowBg));
            style.Setters.Add(new Setter(DataGridRow.BorderThicknessProperty, new Thickness(0)));

            // Realtime signal highlight
            var realtimeTrigger = new DataTrigger { Binding = new Binding("IsRealtime"), Value = true };
            realtimeTrigger.Setters.Add(new Setter(DataGridRow.BackgroundProperty, new SolidColorBrush(Color.FromRgb(40, 55, 40))));
            style.Triggers.Add(realtimeTrigger);

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

        /// <summary>
        /// Creates a FilterDataGrid text column with filtering enabled.
        /// </summary>
        private FilterTextColumn CreateFilterTextColumn(string header, string binding, double width, Style headerStyle, Style cellStyle)
        {
            var column = new FilterTextColumn
            {
                Header = header,
                Binding = new Binding(binding),
                Width = new DataGridLength(width),
                HeaderStyle = headerStyle,
                ElementStyle = cellStyle,
                IsReadOnly = true,
                IsColumnFiltered = true // Enable Excel-style filtering
            };

            return column;
        }

        /// <summary>
        /// Creates a FilterDataGrid template column for colored text (Status, Direction) with filtering.
        /// </summary>
        private FilterTemplateColumn CreateColoredTemplateColumn(string header, string textBinding, string colorBinding,
            IValueConverter colorConverter, double width, Style headerStyle)
        {
            var column = new FilterTemplateColumn
            {
                Header = header,
                Width = new DataGridLength(width),
                HeaderStyle = headerStyle,
                IsReadOnly = true,
                IsColumnFiltered = true, // Enable Excel-style filtering
                FieldName = textBinding, // Required for filtering on template columns
                SortMemberPath = textBinding
            };

            var template = new DataTemplate();
            var factory = new FrameworkElementFactory(typeof(TextBlock));
            factory.SetBinding(TextBlock.TextProperty, new Binding(textBinding));
            factory.SetBinding(TextBlock.ForegroundProperty, new Binding(colorBinding) { Converter = colorConverter });
            factory.SetValue(TextBlock.TextAlignmentProperty, TextAlignment.Center);
            factory.SetValue(TextBlock.HorizontalAlignmentProperty, HorizontalAlignment.Center);
            factory.SetValue(TextBlock.VerticalAlignmentProperty, VerticalAlignment.Center);
            factory.SetValue(TextBlock.FontWeightProperty, FontWeights.SemiBold);
            factory.SetValue(TextBlock.FontSizeProperty, 10.0);
            template.VisualTree = factory;
            column.CellTemplate = template;

            return column;
        }

        /// <summary>
        /// Creates a FilterDataGrid template column for P&L values with color formatting.
        /// </summary>
        private FilterTemplateColumn CreatePnLTemplateColumn(string header, string binding, double width, Style headerStyle)
        {
            var column = new FilterTemplateColumn
            {
                Header = header,
                Width = new DataGridLength(width),
                HeaderStyle = headerStyle,
                IsReadOnly = true,
                IsColumnFiltered = true, // Enable Excel-style filtering
                FieldName = binding, // Required for filtering on template columns
                SortMemberPath = binding
            };

            var template = new DataTemplate();
            var factory = new FrameworkElementFactory(typeof(TextBlock));
            factory.SetBinding(TextBlock.TextProperty, new Binding(binding) { StringFormat = "F2" });
            factory.SetBinding(TextBlock.ForegroundProperty, new Binding(binding) { Converter = _pnlColorConverter });
            factory.SetValue(TextBlock.TextAlignmentProperty, TextAlignment.Right);
            factory.SetValue(TextBlock.HorizontalAlignmentProperty, HorizontalAlignment.Right);
            factory.SetValue(TextBlock.VerticalAlignmentProperty, VerticalAlignment.Center);
            factory.SetValue(TextBlock.FontWeightProperty, FontWeights.SemiBold);
            factory.SetValue(TextBlock.FontSizeProperty, 10.0);
            factory.SetValue(TextBlock.PaddingProperty, new Thickness(0, 0, 6, 0));
            template.VisualTree = factory;
            column.CellTemplate = template;

            return column;
        }

        /// <summary>
        /// Refreshes the grid view.
        /// </summary>
        public void Refresh()
        {
            _dataGrid.Items.Refresh();
        }
    }
}
