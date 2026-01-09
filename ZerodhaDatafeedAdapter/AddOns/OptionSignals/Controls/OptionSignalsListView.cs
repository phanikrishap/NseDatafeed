using System;
using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;
using ZerodhaDatafeedAdapter.AddOns.OptionSignals.Models;

namespace ZerodhaDatafeedAdapter.AddOns.OptionSignals.Controls
{
    public class OptionSignalsListView : UserControl
    {
        private DataGrid _dataGrid;

        private static readonly SolidColorBrush _bgColor = new SolidColorBrush(Color.FromRgb(27, 27, 28));
        private static readonly SolidColorBrush _fgColor = new SolidColorBrush(Color.FromRgb(212, 212, 212));
        private static readonly SolidColorBrush _headerBg = new SolidColorBrush(Color.FromRgb(37, 37, 38));
        private static readonly SolidColorBrush _strikeBg = new SolidColorBrush(Color.FromRgb(45, 45, 46));
        private static readonly SolidColorBrush _atmBg = new SolidColorBrush(Color.FromRgb(60, 80, 60));
        private static readonly SolidColorBrush _borderColor = new SolidColorBrush(Color.FromRgb(51, 51, 51));

        public ObservableCollection<OptionSignalsRow> ItemsSource
        {
            get => _dataGrid.ItemsSource as ObservableCollection<OptionSignalsRow>;
            set => _dataGrid.ItemsSource = value;
        }

        public OptionSignalsListView()
        {
            Content = BuildUI();
        }

        private DataGrid BuildUI()
        {
            _dataGrid = new DataGrid
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
                AlternatingRowBackground = new SolidColorBrush(Color.FromRgb(32, 32, 33))
            };

            var headerStyle = new Style(typeof(System.Windows.Controls.Primitives.DataGridColumnHeader));
            headerStyle.Setters.Add(new Setter(System.Windows.Controls.Primitives.DataGridColumnHeader.BackgroundProperty, _headerBg));
            headerStyle.Setters.Add(new Setter(System.Windows.Controls.Primitives.DataGridColumnHeader.ForegroundProperty, _fgColor));
            headerStyle.Setters.Add(new Setter(System.Windows.Controls.Primitives.DataGridColumnHeader.PaddingProperty, new Thickness(5)));
            headerStyle.Setters.Add(new Setter(System.Windows.Controls.Primitives.DataGridColumnHeader.BorderThicknessProperty, new Thickness(0, 0, 1, 1)));
            headerStyle.Setters.Add(new Setter(System.Windows.Controls.Primitives.DataGridColumnHeader.BorderBrushProperty, _borderColor));
            headerStyle.Setters.Add(new Setter(System.Windows.Controls.Primitives.DataGridColumnHeader.HorizontalContentAlignmentProperty, HorizontalAlignment.Center));

            // CE Column Group
            _dataGrid.Columns.Add(CreateTextColumn("CE Time", "CETickTime", 75, headerStyle));
            _dataGrid.Columns.Add(CreateTextColumn("CE LTP", "CELTP", 70, headerStyle, FontWeights.Bold, new SolidColorBrush(Color.FromRgb(38, 166, 91))));
            _dataGrid.Columns.Add(CreateTextColumn("CE Atr", "CEAtrLTP", 70, headerStyle, FontWeights.SemiBold));
            _dataGrid.Columns.Add(CreateTextColumn("Atr Time", "CEAtrTime", 75, headerStyle));

            // Strike
            _dataGrid.Columns.Add(CreateStrikeColumn(headerStyle));

            // PE Column Group
            _dataGrid.Columns.Add(CreateTextColumn("Atr Time", "PEAtrTime", 75, headerStyle));
            _dataGrid.Columns.Add(CreateTextColumn("PE Atr", "PEAtrLTP", 70, headerStyle, FontWeights.SemiBold));
            _dataGrid.Columns.Add(CreateTextColumn("PE LTP", "PELTP", 70, headerStyle, FontWeights.Bold, new SolidColorBrush(Color.FromRgb(207, 70, 71))));
            _dataGrid.Columns.Add(CreateTextColumn("PE Time", "PETickTime", 75, headerStyle));

            // Row Style for ATM
            var rowStyle = new Style(typeof(DataGridRow));
            var atmTrigger = new DataTrigger { Binding = new Binding("IsATM"), Value = true };
            atmTrigger.Setters.Add(new Setter(DataGridRow.BackgroundProperty, _atmBg));
            rowStyle.Triggers.Add(atmTrigger);
            _dataGrid.RowStyle = rowStyle;

            return _dataGrid;
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
            if (weight.HasValue) col.ElementStyle.Setters.Add(new Setter(TextBlock.FontWeightProperty, weight.Value));
            if (foreground != null) col.ElementStyle.Setters.Add(new Setter(TextBlock.ForegroundProperty, foreground));
            return col;
        }

        private DataGridTemplateColumn CreateStrikeColumn(Style headerStyle)
        {
            var col = new DataGridTemplateColumn
            {
                Header = "Strike",
                Width = 80,
                HeaderStyle = headerStyle
            };

            var factory = new FrameworkElementFactory(typeof(Border));
            factory.SetValue(Border.BackgroundProperty, _strikeBg);
            factory.SetValue(Border.PaddingProperty, new Thickness(5, 2, 5, 2));

            var text = new FrameworkElementFactory(typeof(TextBlock));
            text.SetBinding(TextBlock.TextProperty, new Binding("Strike"));
            text.SetValue(TextBlock.FontWeightProperty, FontWeights.Bold);
            text.SetValue(TextBlock.HorizontalAlignmentProperty, HorizontalAlignment.Center);
            text.SetValue(TextBlock.ForegroundProperty, Brushes.White);

            factory.AppendChild(text);
            col.CellTemplate = new DataTemplate { VisualTree = factory };
            return col;
        }
    }
}
