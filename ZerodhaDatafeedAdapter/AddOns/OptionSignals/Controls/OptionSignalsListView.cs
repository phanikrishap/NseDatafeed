using System;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;
using ZerodhaDatafeedAdapter.AddOns.OptionSignals.Models;

namespace ZerodhaDatafeedAdapter.AddOns.OptionSignals.Controls
{
    /// <summary>
    /// Converts HvnTrend enum to display string (Bull/Bear/-)
    /// </summary>
    public class HvnTrendToStringConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            if (value is HvnTrend trend)
            {
                return trend switch
                {
                    HvnTrend.Bullish => "Bull",
                    HvnTrend.Bearish => "Bear",
                    _ => "-"
                };
            }
            return "-";
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
            => throw new NotImplementedException();
    }

    /// <summary>
    /// Converts HvnTrend enum to color brush (Green for Bull, Red for Bear)
    /// </summary>
    public class HvnTrendToColorConverter : IValueConverter
    {
        private static readonly SolidColorBrush _bullColor = new SolidColorBrush(Color.FromRgb(38, 166, 91));
        private static readonly SolidColorBrush _bearColor = new SolidColorBrush(Color.FromRgb(207, 70, 71));
        private static readonly SolidColorBrush _neutralColor = new SolidColorBrush(Color.FromRgb(150, 150, 150));

        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            if (value is HvnTrend trend)
            {
                return trend switch
                {
                    HvnTrend.Bullish => _bullColor,
                    HvnTrend.Bearish => _bearColor,
                    _ => _neutralColor
                };
            }
            return _neutralColor;
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
            => throw new NotImplementedException();
    }

    public class OptionSignalsListView : UserControl
    {
        private DataGrid _dataGrid;

        private static readonly SolidColorBrush _bgColor = new SolidColorBrush(Color.FromRgb(27, 27, 28));
        private static readonly SolidColorBrush _fgColor = new SolidColorBrush(Color.FromRgb(212, 212, 212));
        private static readonly SolidColorBrush _headerBg = new SolidColorBrush(Color.FromRgb(37, 37, 38));
        private static readonly SolidColorBrush _groupHeaderBg = new SolidColorBrush(Color.FromRgb(45, 55, 65));
        private static readonly SolidColorBrush _strikeBg = new SolidColorBrush(Color.FromRgb(45, 45, 46));
        private static readonly SolidColorBrush _atmBg = new SolidColorBrush(Color.FromRgb(60, 80, 60));
        private static readonly SolidColorBrush _borderColor = new SolidColorBrush(Color.FromRgb(51, 51, 51));
        private static readonly SolidColorBrush _ceColor = new SolidColorBrush(Color.FromRgb(38, 166, 91));
        private static readonly SolidColorBrush _peColor = new SolidColorBrush(Color.FromRgb(207, 70, 71));

        // Column widths - narrower with wrapped headers
        private const double COL_LTP = 42;
        private const double COL_ATR = 36;
        private const double COL_ATR_TIME = 52;
        private const double COL_HVN = 22;
        private const double COL_TREND = 28;
        private const double COL_TREND_TIME = 52;
        private const double COL_STRIKE = 48;

        // Converters
        private static readonly HvnTrendToStringConverter _trendStringConverter = new HvnTrendToStringConverter();
        private static readonly HvnTrendToColorConverter _trendColorConverter = new HvnTrendToColorConverter();

        public ObservableCollection<OptionSignalsRow> ItemsSource
        {
            get => _dataGrid.ItemsSource as ObservableCollection<OptionSignalsRow>;
            set => _dataGrid.ItemsSource = value;
        }

        public OptionSignalsListView()
        {
            Content = BuildUI();
        }

        private UIElement BuildUI()
        {
            var mainGrid = new Grid { Background = _bgColor };
            mainGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto }); // Group headers
            mainGrid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) }); // DataGrid

            // Group header row
            var groupHeaderGrid = CreateGroupHeaderRow();
            Grid.SetRow(groupHeaderGrid, 0);
            mainGrid.Children.Add(groupHeaderGrid);

            // DataGrid with sub-headers
            _dataGrid = CreateDataGrid();
            Grid.SetRow(_dataGrid, 1);
            mainGrid.Children.Add(_dataGrid);

            return mainGrid;
        }

        private Grid CreateGroupHeaderRow()
        {
            var grid = new Grid { Background = _groupHeaderBg };

            // CE columns: LTP, Atr, AtrTm, Session(B,S,Tr,Tm), Rolling(B,S,Tr,Tm)
            double ceWidth = COL_LTP + COL_ATR + COL_ATR_TIME + (COL_HVN * 2 + COL_TREND + COL_TREND_TIME) * 2;
            double strikeWidth = COL_STRIKE;
            // PE columns mirror CE
            double peWidth = ceWidth;

            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(ceWidth) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(strikeWidth) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(peWidth) });

            var ceHeader = CreateGroupHeader("CALL", _ceColor);
            Grid.SetColumn(ceHeader, 0);
            grid.Children.Add(ceHeader);

            var strikeHeader = CreateGroupHeader("", null);
            Grid.SetColumn(strikeHeader, 1);
            grid.Children.Add(strikeHeader);

            var peHeader = CreateGroupHeader("PUT", _peColor);
            Grid.SetColumn(peHeader, 2);
            grid.Children.Add(peHeader);

            return grid;
        }

        private Border CreateGroupHeader(string text, Brush foreground)
        {
            var border = new Border
            {
                Background = _groupHeaderBg,
                BorderBrush = _borderColor,
                BorderThickness = new Thickness(0, 0, 1, 1),
                Padding = new Thickness(5, 3, 5, 3)
            };

            var textBlock = new TextBlock
            {
                Text = text,
                FontWeight = FontWeights.Bold,
                FontSize = 11,
                HorizontalAlignment = HorizontalAlignment.Center,
                Foreground = foreground ?? _fgColor
            };

            border.Child = textBlock;
            return border;
        }

        private DataGrid CreateDataGrid()
        {
            var dataGrid = new DataGrid
            {
                Background = _bgColor,
                Foreground = _fgColor,
                AutoGenerateColumns = false,
                CanUserAddRows = false,
                IsReadOnly = true,
                SelectionMode = DataGridSelectionMode.Single,
                GridLinesVisibility = DataGridGridLinesVisibility.Horizontal,
                HorizontalGridLinesBrush = _borderColor,
                HeadersVisibility = DataGridHeadersVisibility.Column,
                RowBackground = _bgColor,
                AlternatingRowBackground = new SolidColorBrush(Color.FromRgb(32, 32, 33)),
                ColumnHeaderHeight = 32, // Taller for wrapped headers
                RowHeight = 18
            };

            var headerStyle = CreateHeaderStyle();

            // CE Columns (left side): LTP, Atr, AtrTm, Session(B,S,Tr,Tm), Rolling(B,S,Tr,Tm)
            dataGrid.Columns.Add(CreateTextColumn("LTP", "CELTP", COL_LTP, headerStyle, FontWeights.Bold, _ceColor));
            dataGrid.Columns.Add(CreateTextColumn("Atr", "CEAtrLTP", COL_ATR, headerStyle));
            dataGrid.Columns.Add(CreateTextColumn("Atr Tm", "CEAtrTime", COL_ATR_TIME, headerStyle));
            // Session
            dataGrid.Columns.Add(CreateTextColumn("Ses B", "CEHvnBSess", COL_HVN, headerStyle));
            dataGrid.Columns.Add(CreateTextColumn("Ses S", "CEHvnSSess", COL_HVN, headerStyle));
            dataGrid.Columns.Add(CreateTrendColumn("Ses Tr", "CETrendSess", COL_TREND, headerStyle));
            dataGrid.Columns.Add(CreateTextColumn("Ses Tm", "CETrendSessTime", COL_TREND_TIME, headerStyle));
            // Rolling
            dataGrid.Columns.Add(CreateTextColumn("Rol B", "CEHvnBRoll", COL_HVN, headerStyle));
            dataGrid.Columns.Add(CreateTextColumn("Rol S", "CEHvnSRoll", COL_HVN, headerStyle));
            dataGrid.Columns.Add(CreateTrendColumn("Rol Tr", "CETrendRoll", COL_TREND, headerStyle));
            dataGrid.Columns.Add(CreateTextColumn("Rol Tm", "CETrendRollTime", COL_TREND_TIME, headerStyle));

            // Strike (center)
            dataGrid.Columns.Add(CreateStrikeColumn(headerStyle));

            // PE Columns (right side): Rolling(Tm,Tr,S,B), Session(Tm,Tr,S,B), AtrTm, Atr, LTP
            // Rolling
            dataGrid.Columns.Add(CreateTextColumn("Rol Tm", "PETrendRollTime", COL_TREND_TIME, headerStyle));
            dataGrid.Columns.Add(CreateTrendColumn("Rol Tr", "PETrendRoll", COL_TREND, headerStyle));
            dataGrid.Columns.Add(CreateTextColumn("Rol S", "PEHvnSRoll", COL_HVN, headerStyle));
            dataGrid.Columns.Add(CreateTextColumn("Rol B", "PEHvnBRoll", COL_HVN, headerStyle));
            // Session
            dataGrid.Columns.Add(CreateTextColumn("Ses Tm", "PETrendSessTime", COL_TREND_TIME, headerStyle));
            dataGrid.Columns.Add(CreateTrendColumn("Ses Tr", "PETrendSess", COL_TREND, headerStyle));
            dataGrid.Columns.Add(CreateTextColumn("Ses S", "PEHvnSSess", COL_HVN, headerStyle));
            dataGrid.Columns.Add(CreateTextColumn("Ses B", "PEHvnBSess", COL_HVN, headerStyle));
            dataGrid.Columns.Add(CreateTextColumn("Atr Tm", "PEAtrTime", COL_ATR_TIME, headerStyle));
            dataGrid.Columns.Add(CreateTextColumn("Atr", "PEAtrLTP", COL_ATR, headerStyle));
            dataGrid.Columns.Add(CreateTextColumn("LTP", "PELTP", COL_LTP, headerStyle, FontWeights.Bold, _peColor));

            // Row Style for ATM
            var rowStyle = new Style(typeof(DataGridRow));
            var atmTrigger = new DataTrigger { Binding = new Binding("IsATM"), Value = true };
            atmTrigger.Setters.Add(new Setter(DataGridRow.BackgroundProperty, _atmBg));
            rowStyle.Triggers.Add(atmTrigger);
            dataGrid.RowStyle = rowStyle;

            return dataGrid;
        }

        private Style CreateHeaderStyle()
        {
            var headerStyle = new Style(typeof(System.Windows.Controls.Primitives.DataGridColumnHeader));
            headerStyle.Setters.Add(new Setter(System.Windows.Controls.Primitives.DataGridColumnHeader.BackgroundProperty, _headerBg));
            headerStyle.Setters.Add(new Setter(System.Windows.Controls.Primitives.DataGridColumnHeader.ForegroundProperty, _fgColor));
            headerStyle.Setters.Add(new Setter(System.Windows.Controls.Primitives.DataGridColumnHeader.PaddingProperty, new Thickness(1, 2, 1, 2)));
            headerStyle.Setters.Add(new Setter(System.Windows.Controls.Primitives.DataGridColumnHeader.BorderThicknessProperty, new Thickness(0, 0, 1, 1)));
            headerStyle.Setters.Add(new Setter(System.Windows.Controls.Primitives.DataGridColumnHeader.BorderBrushProperty, _borderColor));
            headerStyle.Setters.Add(new Setter(System.Windows.Controls.Primitives.DataGridColumnHeader.HorizontalContentAlignmentProperty, HorizontalAlignment.Center));
            headerStyle.Setters.Add(new Setter(System.Windows.Controls.Primitives.DataGridColumnHeader.VerticalContentAlignmentProperty, VerticalAlignment.Center));
            headerStyle.Setters.Add(new Setter(System.Windows.Controls.Primitives.DataGridColumnHeader.FontSizeProperty, 9.0));

            // Create a template for wrapped text headers
            var template = new ControlTemplate(typeof(System.Windows.Controls.Primitives.DataGridColumnHeader));
            var border = new FrameworkElementFactory(typeof(Border));
            border.SetValue(Border.BackgroundProperty, new TemplateBindingExtension(System.Windows.Controls.Primitives.DataGridColumnHeader.BackgroundProperty));
            border.SetValue(Border.BorderBrushProperty, new TemplateBindingExtension(System.Windows.Controls.Primitives.DataGridColumnHeader.BorderBrushProperty));
            border.SetValue(Border.BorderThicknessProperty, new TemplateBindingExtension(System.Windows.Controls.Primitives.DataGridColumnHeader.BorderThicknessProperty));
            border.SetValue(Border.PaddingProperty, new TemplateBindingExtension(System.Windows.Controls.Primitives.DataGridColumnHeader.PaddingProperty));

            var textBlock = new FrameworkElementFactory(typeof(TextBlock));
            textBlock.SetBinding(TextBlock.TextProperty, new Binding("Content") { RelativeSource = new RelativeSource(RelativeSourceMode.TemplatedParent) });
            textBlock.SetValue(TextBlock.TextWrappingProperty, TextWrapping.Wrap);
            textBlock.SetValue(TextBlock.TextAlignmentProperty, TextAlignment.Center);
            textBlock.SetValue(TextBlock.VerticalAlignmentProperty, VerticalAlignment.Center);
            textBlock.SetValue(TextBlock.HorizontalAlignmentProperty, HorizontalAlignment.Center);

            border.AppendChild(textBlock);
            template.VisualTree = border;
            headerStyle.Setters.Add(new Setter(System.Windows.Controls.Primitives.DataGridColumnHeader.TemplateProperty, template));

            return headerStyle;
        }

        private DataGridTextColumn CreateTextColumn(string header, string binding, double width, Style headerStyle, FontWeight? weight = null, Brush foreground = null)
        {
            var col = new DataGridTextColumn
            {
                Header = header,
                Binding = new Binding(binding),
                Width = width,
                HeaderStyle = headerStyle,
                ElementStyle = new Style(typeof(TextBlock))
            };
            col.ElementStyle.Setters.Add(new Setter(TextBlock.TextAlignmentProperty, TextAlignment.Center));
            col.ElementStyle.Setters.Add(new Setter(TextBlock.VerticalAlignmentProperty, VerticalAlignment.Center));
            col.ElementStyle.Setters.Add(new Setter(TextBlock.FontSizeProperty, 10.0));
            if (weight.HasValue) col.ElementStyle.Setters.Add(new Setter(TextBlock.FontWeightProperty, weight.Value));
            if (foreground != null) col.ElementStyle.Setters.Add(new Setter(TextBlock.ForegroundProperty, foreground));
            return col;
        }

        private DataGridTemplateColumn CreateTrendColumn(string header, string binding, double width, Style headerStyle)
        {
            var col = new DataGridTemplateColumn
            {
                Header = header,
                Width = width,
                HeaderStyle = headerStyle
            };

            // Create the cell template with TextBlock that uses converters for text and color
            var factory = new FrameworkElementFactory(typeof(TextBlock));
            factory.SetBinding(TextBlock.TextProperty, new Binding(binding) { Converter = _trendStringConverter });
            factory.SetBinding(TextBlock.ForegroundProperty, new Binding(binding) { Converter = _trendColorConverter });
            factory.SetValue(TextBlock.TextAlignmentProperty, TextAlignment.Center);
            factory.SetValue(TextBlock.VerticalAlignmentProperty, VerticalAlignment.Center);
            factory.SetValue(TextBlock.FontSizeProperty, 10.0);
            factory.SetValue(TextBlock.FontWeightProperty, FontWeights.SemiBold);

            col.CellTemplate = new DataTemplate { VisualTree = factory };
            return col;
        }

        private DataGridTemplateColumn CreateStrikeColumn(Style headerStyle)
        {
            var col = new DataGridTemplateColumn
            {
                Header = "Strike",
                Width = COL_STRIKE,
                HeaderStyle = headerStyle
            };

            var factory = new FrameworkElementFactory(typeof(Border));
            factory.SetValue(Border.BackgroundProperty, _strikeBg);
            factory.SetValue(Border.PaddingProperty, new Thickness(2, 0, 2, 0));

            var text = new FrameworkElementFactory(typeof(TextBlock));
            text.SetBinding(TextBlock.TextProperty, new Binding("Strike"));
            text.SetValue(TextBlock.FontWeightProperty, FontWeights.Bold);
            text.SetValue(TextBlock.FontSizeProperty, 10.0);
            text.SetValue(TextBlock.HorizontalAlignmentProperty, HorizontalAlignment.Center);
            text.SetValue(TextBlock.ForegroundProperty, Brushes.White);

            factory.AppendChild(text);
            col.CellTemplate = new DataTemplate { VisualTree = factory };
            return col;
        }
    }
}
