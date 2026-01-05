using System;
using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;
using ZerodhaDatafeedAdapter.AddOns.MarketAnalyzer.Converters;
using ZerodhaDatafeedAdapter.AddOns.MarketAnalyzer.Models;

namespace ZerodhaDatafeedAdapter.AddOns.MarketAnalyzer.Controls
{
    public class OptionChainListView : UserControl
    {
        private ListView _listView;
        private GridView _gridView;

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
            get => _listView.ItemsSource as ObservableCollection<OptionChainRow>;
            set => _listView.ItemsSource = value;
        }

        public OptionChainRow SelectedItem => _listView.SelectedItem as OptionChainRow;

        public event System.Windows.Input.MouseButtonEventHandler MouseLeftButtonUpEvent;

        public OptionChainListView()
        {
            Content = BuildUI();
            
            _listView.MouseLeftButtonUp += (s, e) => MouseLeftButtonUpEvent?.Invoke(s, e);
        }

        private ListView BuildUI()
        {
            _listView = new ListView
            {
                Background = _bgColor,
                Foreground = _fgColor,
                BorderThickness = new Thickness(0),
                BorderBrush = null,
                FontFamily = _ntFont,
                FontSize = 12
            };

            // Style and columns logic same as in original CreateOptionChainListView
            _listView.Resources.Add(SystemColors.HighlightBrushKey, new SolidColorBrush(Color.FromRgb(60, 60, 65)));
            _listView.Resources.Add(SystemColors.HighlightTextBrushKey, new SolidColorBrush(Color.FromRgb(255, 255, 255)));
            _listView.Resources.Add(SystemColors.InactiveSelectionHighlightBrushKey, new SolidColorBrush(Color.FromRgb(50, 50, 55)));
            _listView.Resources.Add(SystemColors.InactiveSelectionHighlightTextBrushKey, new SolidColorBrush(Color.FromRgb(220, 220, 220)));

            var headerStyle = CreateHeaderStyle();
            _listView.Resources.Add(typeof(GridViewColumnHeader), CreateFillerHeaderStyle());

            _gridView = new GridView { AllowsColumnReorder = false };

            // CE Side columns (Left)
            _gridView.Columns.Add(CreateColumn("Time", "CEUpdateTime", 60, HorizontalAlignment.Center, headerStyle));
            _gridView.Columns.Add(CreateColumn("Status", "CEStatus", 90, HorizontalAlignment.Center, headerStyle));
            _gridView.Columns.Add(CreateVWAPColumn("VWAP", "CEVWAPDisplay", "CEVWAPComparison", 55, true, headerStyle));
            _gridView.Columns.Add(CreateHistogramColumn("LTP", "CELast", "CEHistogramWidth", 90, true, headerStyle));

            // Strike column
            _gridView.Columns.Add(CreateStrikeColumn(headerStyle));

            // PE Side columns (Right)
            _gridView.Columns.Add(CreateHistogramColumn("LTP", "PELast", "PEHistogramWidth", 90, false, headerStyle));
            _gridView.Columns.Add(CreateVWAPColumn("VWAP", "PEVWAPDisplay", "PEVWAPComparison", 55, false, headerStyle));
            _gridView.Columns.Add(CreateColumn("Status", "PEStatus", 90, HorizontalAlignment.Center, headerStyle));
            _gridView.Columns.Add(CreateColumn("Time", "PEUpdateTime", 60, HorizontalAlignment.Center, headerStyle));

            // Straddle columns
            _gridView.Columns.Add(CreateStraddleColumn(headerStyle));
            _gridView.Columns.Add(CreateVWAPColumn("VWAP", "StraddleVWAPDisplay", "StraddleVWAPComparison", 60, false, headerStyle));

            _listView.View = _gridView;
            _listView.ItemContainerStyle = CreateItemContainerStyle();

            return _listView;
        }

        private Style CreateHeaderStyle()
        {
            var headerStyle = new Style(typeof(GridViewColumnHeader));
            headerStyle.Setters.Add(new Setter(GridViewColumnHeader.BackgroundProperty, _headerBg));
            headerStyle.Setters.Add(new Setter(GridViewColumnHeader.ForegroundProperty, _fgColor));
            headerStyle.Setters.Add(new Setter(GridViewColumnHeader.FontWeightProperty, FontWeights.Bold));
            headerStyle.Setters.Add(new Setter(GridViewColumnHeader.FontFamilyProperty, _ntFont));
            headerStyle.Setters.Add(new Setter(GridViewColumnHeader.FontSizeProperty, 12.0));
            headerStyle.Setters.Add(new Setter(GridViewColumnHeader.PaddingProperty, new Thickness(5, 4, 5, 4)));
            headerStyle.Setters.Add(new Setter(GridViewColumnHeader.BorderThicknessProperty, new Thickness(0)));
            headerStyle.Setters.Add(new Setter(GridViewColumnHeader.BorderBrushProperty, null));
            headerStyle.Setters.Add(new Setter(GridViewColumnHeader.HorizontalContentAlignmentProperty, HorizontalAlignment.Center));

            var headerTemplate = new ControlTemplate(typeof(GridViewColumnHeader));
            var headerBorder = new FrameworkElementFactory(typeof(Border));
            headerBorder.SetValue(Border.BackgroundProperty, _headerBg);
            headerBorder.SetValue(Border.BorderThicknessProperty, new Thickness(0, 0, 1, 1));
            headerBorder.SetValue(Border.BorderBrushProperty, new SolidColorBrush(Color.FromRgb(50, 50, 52)));
            headerBorder.SetValue(Border.PaddingProperty, new Thickness(5, 4, 5, 4));
            var headerContent = new FrameworkElementFactory(typeof(ContentPresenter));
            headerContent.SetValue(ContentPresenter.HorizontalAlignmentProperty, HorizontalAlignment.Center);
            headerContent.SetValue(ContentPresenter.VerticalAlignmentProperty, VerticalAlignment.Center);
            headerBorder.AppendChild(headerContent);
            headerTemplate.VisualTree = headerBorder;
            headerStyle.Setters.Add(new Setter(GridViewColumnHeader.TemplateProperty, headerTemplate));

            return headerStyle;
        }

        private Style CreateFillerHeaderStyle()
        {
            var fillerHeaderStyle = new Style(typeof(GridViewColumnHeader));
            fillerHeaderStyle.Setters.Add(new Setter(GridViewColumnHeader.BackgroundProperty, _headerBg));
            fillerHeaderStyle.Setters.Add(new Setter(GridViewColumnHeader.BorderBrushProperty, null));
            fillerHeaderStyle.Setters.Add(new Setter(GridViewColumnHeader.BorderThicknessProperty, new Thickness(0)));
            var fillerTemplate = new ControlTemplate(typeof(GridViewColumnHeader));
            var fillerBorder = new FrameworkElementFactory(typeof(Border));
            fillerBorder.SetValue(Border.BackgroundProperty, _headerBg);
            fillerBorder.SetValue(Border.BorderThicknessProperty, new Thickness(0));
            fillerTemplate.VisualTree = fillerBorder;
            fillerHeaderStyle.Setters.Add(new Setter(GridViewColumnHeader.TemplateProperty, fillerTemplate));
            return fillerHeaderStyle;
        }

        private Style CreateItemContainerStyle()
        {
            var style = new Style(typeof(ListViewItem));
            style.Setters.Add(new Setter(ListViewItem.ForegroundProperty, _fgColor));
            style.Setters.Add(new Setter(ListViewItem.FontFamilyProperty, _ntFont));
            style.Setters.Add(new Setter(ListViewItem.HorizontalContentAlignmentProperty, HorizontalAlignment.Stretch));
            style.Setters.Add(new Setter(ListViewItem.FocusVisualStyleProperty, null));
            style.Setters.Add(new Setter(ListViewItem.BackgroundProperty, _bgColor));
            style.Setters.Add(new Setter(ListViewItem.BorderThicknessProperty, new Thickness(0)));
            style.Setters.Add(new Setter(ListViewItem.BorderBrushProperty, null));
            style.Setters.Add(new Setter(ListViewItem.PaddingProperty, new Thickness(2)));

            var itemTemplate = new ControlTemplate(typeof(ListViewItem));
            var itemBorder = new FrameworkElementFactory(typeof(Border));
            itemBorder.SetValue(Border.BackgroundProperty, new TemplateBindingExtension(ListViewItem.BackgroundProperty));
            itemBorder.SetValue(Border.BorderThicknessProperty, new Thickness(0));
            itemBorder.SetValue(Border.PaddingProperty, new Thickness(2));
            itemBorder.SetValue(Border.SnapsToDevicePixelsProperty, true);
            var contentPresenter = new FrameworkElementFactory(typeof(GridViewRowPresenter));
            contentPresenter.SetValue(GridViewRowPresenter.HorizontalAlignmentProperty, HorizontalAlignment.Stretch);
            contentPresenter.SetValue(GridViewRowPresenter.VerticalAlignmentProperty, VerticalAlignment.Center);
            contentPresenter.SetValue(GridViewRowPresenter.SnapsToDevicePixelsProperty, true);
            itemBorder.AppendChild(contentPresenter);
            itemTemplate.VisualTree = itemBorder;
            style.Setters.Add(new Setter(ListViewItem.TemplateProperty, itemTemplate));

            var atmTrigger = new DataTrigger { Binding = new Binding("IsATM"), Value = true };
            atmTrigger.Setters.Add(new Setter(ListViewItem.BackgroundProperty, _atmBg));
            atmTrigger.Setters.Add(new Setter(ListViewItem.FontWeightProperty, FontWeights.Bold));
            style.Triggers.Add(atmTrigger);

            var selectedTrigger = new Trigger { Property = ListViewItem.IsSelectedProperty, Value = true };
            selectedTrigger.Setters.Add(new Setter(ListViewItem.BackgroundProperty, new SolidColorBrush(Color.FromRgb(60, 60, 65))));
            style.Triggers.Add(selectedTrigger);

            var mouseOverTrigger = new Trigger { Property = ListViewItem.IsMouseOverProperty, Value = true };
            mouseOverTrigger.Setters.Add(new Setter(ListViewItem.BackgroundProperty, new SolidColorBrush(Color.FromRgb(50, 50, 55))));
            style.Triggers.Add(mouseOverTrigger);

            return style;
        }

        private GridViewColumn CreateColumn(string header, string binding, double width, HorizontalAlignment align, Style headerStyle)
        {
            var column = new GridViewColumn { Header = header, Width = width, HeaderContainerStyle = headerStyle };
            var template = new DataTemplate();
            var gridFactory = new FrameworkElementFactory(typeof(Grid));
            gridFactory.SetValue(Grid.HorizontalAlignmentProperty, HorizontalAlignment.Stretch);

            var factory = new FrameworkElementFactory(typeof(TextBlock));
            factory.SetBinding(TextBlock.TextProperty, new Binding(binding));
            factory.SetValue(TextBlock.HorizontalAlignmentProperty, align);
            factory.SetValue(TextBlock.VerticalAlignmentProperty, VerticalAlignment.Center);
            factory.SetValue(TextBlock.TextAlignmentProperty, align == HorizontalAlignment.Center ? TextAlignment.Center : TextAlignment.Left);
            factory.SetValue(TextBlock.PaddingProperty, new Thickness(3, 2, 3, 2));

            gridFactory.AppendChild(factory);
            template.VisualTree = gridFactory;
            column.CellTemplate = template;
            return column;
        }

        private GridViewColumn CreateStrikeColumn(Style headerStyle)
        {
            var strikeColumn = new GridViewColumn { Header = "Strike", Width = 80, HeaderContainerStyle = headerStyle };
            var strikeTemplate = new DataTemplate();
            var strikeFactory = new FrameworkElementFactory(typeof(Border));
            strikeFactory.SetValue(Border.BackgroundProperty, _strikeBg);
            strikeFactory.SetValue(Border.PaddingProperty, new Thickness(5, 2, 5, 2));
            var strikeText = new FrameworkElementFactory(typeof(TextBlock));
            strikeText.SetBinding(TextBlock.TextProperty, new Binding("StrikeDisplay"));
            strikeText.SetValue(TextBlock.FontWeightProperty, FontWeights.Bold);
            strikeText.SetValue(TextBlock.HorizontalAlignmentProperty, HorizontalAlignment.Center);
            strikeText.SetValue(TextBlock.ForegroundProperty, new SolidColorBrush(Color.FromRgb(255, 255, 255)));
            strikeFactory.AppendChild(strikeText);
            strikeTemplate.VisualTree = strikeFactory;
            strikeColumn.CellTemplate = strikeTemplate;
            return strikeColumn;
        }

        private GridViewColumn CreateStraddleColumn(Style headerStyle)
        {
            var straddleColumn = new GridViewColumn { Header = "Straddle", Width = 75, HeaderContainerStyle = headerStyle };
            var straddleTemplate = new DataTemplate();
            var straddleFactory = new FrameworkElementFactory(typeof(TextBlock));
            straddleFactory.SetBinding(TextBlock.TextProperty, new Binding("StraddlePrice"));
            straddleFactory.SetValue(TextBlock.HorizontalAlignmentProperty, HorizontalAlignment.Center);
            straddleFactory.SetValue(TextBlock.VerticalAlignmentProperty, VerticalAlignment.Center);
            straddleFactory.SetValue(TextBlock.PaddingProperty, new Thickness(2, 2, 2, 2));
            straddleFactory.SetValue(TextBlock.FontWeightProperty, FontWeights.Bold);
            straddleFactory.SetValue(TextBlock.ForegroundProperty, new SolidColorBrush(Color.FromRgb(255, 255, 255)));
            straddleTemplate.VisualTree = straddleFactory;
            straddleColumn.CellTemplate = straddleTemplate;
            return straddleColumn;
        }

        private GridViewColumn CreateHistogramColumn(string header, string priceBinding, string widthBinding, double columnWidth, bool isCall, Style headerStyle)
        {
            var column = new GridViewColumn { Header = header, Width = columnWidth, HeaderContainerStyle = headerStyle };
            var template = new DataTemplate();
            var gridFactory = new FrameworkElementFactory(typeof(Grid));
            gridFactory.SetValue(Grid.HeightProperty, 22.0);
            gridFactory.SetValue(Grid.VerticalAlignmentProperty, VerticalAlignment.Center);

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

        private GridViewColumn CreateVWAPColumn(string header, string vwapBinding, string comparisonBinding, double columnWidth, bool isCall, Style headerStyle)
        {
            var column = new GridViewColumn { Header = header, Width = columnWidth, HeaderContainerStyle = headerStyle };
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
            _listView.Items.Refresh();
        }
    }
}
