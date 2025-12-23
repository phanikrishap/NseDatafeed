using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using System.Xml.Linq;
using NinjaTrader.Cbi;
using NinjaTrader.Gui.Tools;
using QANinjaAdapter;
using QANinjaAdapter.Models;
using QANinjaAdapter.Services.Analysis;
using QANinjaAdapter.SyntheticInstruments;

namespace QANinjaAdapter.AddOns.MarketAnalyzer
{
    /// <summary>
    /// Converts a percentage (0-100) to a pixel width based on max width parameter
    /// </summary>
    public class PercentageToWidthConverter : IMultiValueConverter
    {
        public object Convert(object[] values, Type targetType, object parameter, CultureInfo culture)
        {
            if (values == null || values.Length < 1 || values[0] == DependencyProperty.UnsetValue)
                return 0.0;

            double percentage = 0;
            if (values[0] is double d)
                percentage = d;

            double maxWidth = 96; // Default
            if (parameter is double mw)
                maxWidth = mw;
            else if (parameter is string s && double.TryParse(s, out double parsed))
                maxWidth = parsed;

            // Clamp percentage to 0-100
            percentage = Math.Max(0, Math.Min(100, percentage));

            return (percentage / 100.0) * maxWidth;
        }

        public object[] ConvertBack(object value, Type[] targetTypes, object parameter, CultureInfo culture)
        {
            throw new NotImplementedException();
        }
    }

    /// <summary>
    /// Row item for the Option Chain - represents a single strike with CE and PE data
    /// </summary>
    public class OptionChainRow
    {
        public double Strike { get; set; }
        public string StrikeDisplay => Strike.ToString("F0");

        // CE (Call) data
        public string CELast { get; set; } = "---";
        public string CEStatus { get; set; } = "---";
        public double CEPrice { get; set; }
        public string CESymbol { get; set; }

        // PE (Put) data
        public string PELast { get; set; } = "---";
        public string PEStatus { get; set; } = "---";
        public double PEPrice { get; set; }
        public string PESymbol { get; set; }

        // Synthetic Straddle price from SyntheticStraddleService (live calculated)
        public double SyntheticStraddlePrice { get; set; }

        // Straddle = Synthetic straddle price (if available) or CE + PE fallback
        public string StraddlePrice => SyntheticStraddlePrice > 0
            ? SyntheticStraddlePrice.ToString("F2")
            : (CEPrice > 0 && PEPrice > 0)
                ? (CEPrice + PEPrice).ToString("F2")
                : "---";

        public double StraddleValue => SyntheticStraddlePrice > 0
            ? SyntheticStraddlePrice
            : (CEPrice > 0 && PEPrice > 0) ? CEPrice + PEPrice : double.MaxValue;

        // Straddle symbol for the synthetic instrument (e.g., NIFTY25DEC24000_STRDL)
        public string StraddleSymbol { get; set; }

        public bool IsATM { get; set; }

        // Histogram width percentage (0-100) for visual representation
        // CE histogram grows from right-to-left (aligned right in cell)
        // PE histogram grows from left-to-right (aligned left in cell)
        public double CEHistogramWidth { get; set; }
        public double PEHistogramWidth { get; set; }
    }

    /// <summary>
    /// Option Chain Window - hosts the OptionChainTabPage
    /// This is the main NTWindow that contains a TabControl with our NTTabPage
    /// </summary>
    public class OptionChainWindow : NTWindow, IWorkspacePersistence
    {
        public OptionChainWindow()
        {
            Logger.Info("[OptionChainWindow] Constructor: Creating window");

            Caption = "Option Chain";
            Width = 700;
            Height = 600;

            // Create the tab control (required by NTWindow for proper instrument linking)
            TabControl tabControl = new TabControl();
            tabControl.Style = Application.Current.TryFindResource("TabControlStyle") as Style;

            // Create and add our tab page
            OptionChainTabPage tabPage = new OptionChainTabPage();
            tabControl.Items.Add(tabPage);

            // Set the content
            Content = tabControl;

            Logger.Info("[OptionChainWindow] Constructor: Window created with TabControl and OptionChainTabPage");
        }

        // IWorkspacePersistence - delegate to tab page
        public void Restore(XDocument document, XElement element)
        {
            Logger.Debug("[OptionChainWindow] Restore: Called");
        }

        public void Save(XDocument document, XElement element)
        {
            Logger.Debug("[OptionChainWindow] Save: Called");
        }

        public WorkspaceOptions WorkspaceOptions { get; set; }
    }

    /// <summary>
    /// Option Chain Tab Page - implements IInstrumentProvider for instrument linking
    /// When using NTTabPage with IInstrumentProvider, NinjaTrader automatically adds the link button
    /// </summary>
    public class OptionChainTabPage : NTTabPage, IInstrumentProvider
    {
        private ListView _listView;
        private GridView _gridView;
        private TextBlock _lblUnderlying;
        private TextBlock _lblExpiry;
        private TextBlock _lblATMStrike;
        private TextBlock _lblStatus;
        private TextBlock _lblSelectedInstrument;
        private ObservableCollection<OptionChainRow> _rows;

        // Instrument linking - this is the key field for IInstrumentProvider
        private Instrument _instrument;

        // Symbol to row mapping for quick updates (supports both generated and zerodha symbols)
        private Dictionary<string, (OptionChainRow row, string optionType)> _symbolToRowMap =
            new Dictionary<string, (OptionChainRow, string)>();

        // Mapping from generated symbol to zerodha symbol (resolved from DB)
        private Dictionary<string, string> _generatedToZerodhaMap = new Dictionary<string, string>();

        // Mapping from straddle symbol to row for synthetic straddle price updates
        private Dictionary<string, OptionChainRow> _straddleSymbolToRowMap = new Dictionary<string, OptionChainRow>();

        // NinjaTrader-style colors
        private static readonly SolidColorBrush _bgColor = new SolidColorBrush(Color.FromRgb(27, 27, 28));
        private static readonly SolidColorBrush _fgColor = new SolidColorBrush(Color.FromRgb(212, 212, 212));
        private static readonly SolidColorBrush _headerBg = new SolidColorBrush(Color.FromRgb(37, 37, 38));
        private static readonly SolidColorBrush _borderColor = new SolidColorBrush(Color.FromRgb(51, 51, 51));
        private static readonly SolidColorBrush _atmBg = new SolidColorBrush(Color.FromRgb(60, 80, 60)); // Greenish highlight for ATM
        private static readonly SolidColorBrush _strikeBg = new SolidColorBrush(Color.FromRgb(45, 45, 46));
        private static readonly FontFamily _ntFont = new FontFamily("Segoe UI");

        // Histogram colors - CE (Calls) in green tones, PE (Puts) in red tones
        private static readonly SolidColorBrush _ceHistogramBrush = new SolidColorBrush(Color.FromArgb(180, 38, 166, 91));  // Green with transparency
        private static readonly SolidColorBrush _peHistogramBrush = new SolidColorBrush(Color.FromArgb(180, 207, 70, 71));  // Red with transparency

        // Track max price for histogram scaling
        private double _maxOptionPrice = 0;

        private string _underlying = "NIFTY";
        private DateTime? _expiry;

        public OptionChainTabPage()
        {
            Logger.Info("[OptionChainTabPage] Constructor: Creating tab page");

            // Set the background for NTTabPage
            Background = Application.Current.TryFindResource("BackgroundMainWindow") as Brush ?? _bgColor;

            try
            {
                _rows = new ObservableCollection<OptionChainRow>();

                var dockPanel = new DockPanel { Background = _bgColor };
                Content = dockPanel;

                // Header panel with underlying info
                var headerPanel = CreateHeaderPanel();
                DockPanel.SetDock(headerPanel, Dock.Top);
                dockPanel.Children.Add(headerPanel);

                // Status bar at bottom
                var statusBar = CreateStatusBar();
                DockPanel.SetDock(statusBar, Dock.Bottom);
                dockPanel.Children.Add(statusBar);

                // Main ListView for option chain
                _listView = CreateOptionChainListView();
                dockPanel.Children.Add(_listView);

                // Hook Events
                Loaded += OnTabPageLoaded;
                Unloaded += OnTabPageUnloaded;

                Logger.Info("[OptionChainTabPage] Constructor: Tab page created successfully");
            }
            catch (Exception ex)
            {
                Logger.Error($"[OptionChainTabPage] Constructor: Exception - {ex.Message}", ex);
                throw;
            }
        }

        private Border CreateHeaderPanel()
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

            // Row 2: Selected instrument display (shows clicked option)
            var linkPanel = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Margin = new Thickness(0, 6, 0, 0)
            };

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

        private Border CreateStatusBar()
        {
            var border = new Border
            {
                Background = _headerBg,
                BorderBrush = _borderColor,
                BorderThickness = new Thickness(0, 1, 0, 0),
                Padding = new Thickness(8, 4, 8, 4)
            };

            _lblStatus = new TextBlock
            {
                Text = "Waiting for option data...",
                FontFamily = _ntFont,
                FontSize = 11,
                Foreground = _fgColor
            };

            border.Child = _lblStatus;
            return border;
        }

        private ListView CreateOptionChainListView()
        {
            var listView = new ListView
            {
                Background = _bgColor,
                Foreground = _fgColor,
                BorderThickness = new Thickness(0),
                FontFamily = _ntFont,
                FontSize = 12,
                ItemsSource = _rows
            };

            _gridView = new GridView { AllowsColumnReorder = false };

            // CE Side columns (Left)
            _gridView.Columns.Add(CreateColumn("CE Status", "CEStatus", 80, HorizontalAlignment.Center));
            _gridView.Columns.Add(CreateHistogramColumn("CE Last", "CELast", "CEHistogramWidth", 100, true));

            // Strike column (Center) - highlighted
            var strikeColumn = new GridViewColumn
            {
                Header = "Strike",
                Width = 80
            };
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
            _gridView.Columns.Add(strikeColumn);

            // PE Side columns (Right)
            _gridView.Columns.Add(CreateHistogramColumn("PE Last", "PELast", "PEHistogramWidth", 100, false));
            _gridView.Columns.Add(CreateColumn("PE Status", "PEStatus", 80, HorizontalAlignment.Center));

            // Straddle column
            var straddleColumn = new GridViewColumn
            {
                Header = "Straddle",
                Width = 90
            };
            var straddleTemplate = new DataTemplate();
            var straddleFactory = new FrameworkElementFactory(typeof(TextBlock));
            straddleFactory.SetBinding(TextBlock.TextProperty, new Binding("StraddlePrice"));
            straddleFactory.SetValue(TextBlock.HorizontalAlignmentProperty, HorizontalAlignment.Right);
            straddleFactory.SetValue(TextBlock.PaddingProperty, new Thickness(0, 0, 5, 0));
            straddleFactory.SetValue(TextBlock.FontWeightProperty, FontWeights.SemiBold);
            straddleFactory.SetValue(TextBlock.ForegroundProperty, new SolidColorBrush(Color.FromRgb(255, 200, 100)));
            straddleTemplate.VisualTree = straddleFactory;
            straddleColumn.CellTemplate = straddleTemplate;
            _gridView.Columns.Add(straddleColumn);

            listView.View = _gridView;

            // Style for rows with ATM highlighting
            var style = new Style(typeof(ListViewItem));
            style.Setters.Add(new Setter(ListViewItem.BackgroundProperty, _bgColor));
            style.Setters.Add(new Setter(ListViewItem.ForegroundProperty, _fgColor));
            style.Setters.Add(new Setter(ListViewItem.BorderThicknessProperty, new Thickness(0)));
            style.Setters.Add(new Setter(ListViewItem.PaddingProperty, new Thickness(2)));
            style.Setters.Add(new Setter(ListViewItem.FontFamilyProperty, _ntFont));

            // ATM row highlight trigger
            var atmTrigger = new DataTrigger { Binding = new Binding("IsATM"), Value = true };
            atmTrigger.Setters.Add(new Setter(ListViewItem.BackgroundProperty, _atmBg));
            atmTrigger.Setters.Add(new Setter(ListViewItem.FontWeightProperty, FontWeights.Bold));
            style.Triggers.Add(atmTrigger);

            listView.ItemContainerStyle = style;

            // Wire up click handler for instrument linking
            listView.MouseLeftButtonUp += OnListViewMouseLeftButtonUp;

            return listView;
        }

        private GridViewColumn CreateColumn(string header, string binding, double width, HorizontalAlignment align)
        {
            var column = new GridViewColumn
            {
                Header = header,
                Width = width
            };
            var template = new DataTemplate();
            var factory = new FrameworkElementFactory(typeof(TextBlock));
            factory.SetBinding(TextBlock.TextProperty, new Binding(binding));
            factory.SetValue(TextBlock.HorizontalAlignmentProperty, align);
            factory.SetValue(TextBlock.PaddingProperty, new Thickness(5, 0, 5, 0));
            template.VisualTree = factory;
            column.CellTemplate = template;
            return column;
        }

        /// <summary>
        /// Creates a column with a histogram bar behind the price text
        /// </summary>
        private GridViewColumn CreateHistogramColumn(string header, string priceBinding, string widthBinding, double columnWidth, bool isCall)
        {
            var column = new GridViewColumn
            {
                Header = header,
                Width = columnWidth
            };

            var template = new DataTemplate();

            // Outer Grid to hold the histogram bar and text
            var gridFactory = new FrameworkElementFactory(typeof(Grid));
            gridFactory.SetValue(Grid.HeightProperty, 20.0);

            // Histogram bar (colored rectangle)
            var barFactory = new FrameworkElementFactory(typeof(Border));
            barFactory.SetValue(Border.BackgroundProperty, isCall ? _ceHistogramBrush : _peHistogramBrush);
            barFactory.SetValue(Border.HorizontalAlignmentProperty, isCall ? HorizontalAlignment.Right : HorizontalAlignment.Left);
            barFactory.SetValue(Border.VerticalAlignmentProperty, VerticalAlignment.Stretch);
            barFactory.SetValue(Border.CornerRadiusProperty, new CornerRadius(2));
            barFactory.SetValue(Border.MarginProperty, new Thickness(1));

            // Bind the width to percentage of column width
            var widthMultiBinding = new MultiBinding
            {
                Converter = new PercentageToWidthConverter(),
                ConverterParameter = columnWidth - 4 // Account for margins
            };
            widthMultiBinding.Bindings.Add(new Binding(widthBinding));
            barFactory.SetBinding(Border.WidthProperty, widthMultiBinding);

            gridFactory.AppendChild(barFactory);

            // Price text overlay
            var textFactory = new FrameworkElementFactory(typeof(TextBlock));
            textFactory.SetBinding(TextBlock.TextProperty, new Binding(priceBinding));
            textFactory.SetValue(TextBlock.HorizontalAlignmentProperty, isCall ? HorizontalAlignment.Right : HorizontalAlignment.Left);
            textFactory.SetValue(TextBlock.VerticalAlignmentProperty, VerticalAlignment.Center);
            textFactory.SetValue(TextBlock.PaddingProperty, new Thickness(5, 0, 5, 0));
            textFactory.SetValue(TextBlock.ForegroundProperty, new SolidColorBrush(Colors.White));
            textFactory.SetValue(TextBlock.FontWeightProperty, FontWeights.SemiBold);

            gridFactory.AppendChild(textFactory);

            template.VisualTree = gridFactory;
            column.CellTemplate = template;
            return column;
        }

        private void OnTabPageLoaded(object sender, RoutedEventArgs e)
        {
            Logger.Info("[OptionChainTabPage] OnTabPageLoaded: Subscribing to events");

            // Subscribe to option events
            SubscriptionManager.Instance.OptionPriceUpdated += OnOptionPriceUpdated;
            SubscriptionManager.Instance.OptionStatusUpdated += OnOptionStatusUpdated;
            SubscriptionManager.Instance.SymbolResolved += OnSymbolResolved;
            MarketAnalyzerLogic.Instance.OptionsGenerated += OnOptionsGenerated;

            // Subscribe to synthetic straddle price events
            var adapter = Connector.Instance.GetAdapter() as QAAdapter;
            if (adapter?.SyntheticStraddleService != null)
            {
                adapter.SyntheticStraddleService.StraddlePriceCalculated += OnStraddlePriceCalculated;
                Logger.Info("[OptionChainTabPage] OnTabPageLoaded: Subscribed to SyntheticStraddleService.StraddlePriceCalculated");
            }
            else
            {
                Logger.Warn("[OptionChainTabPage] OnTabPageLoaded: SyntheticStraddleService not available");
            }

            _lblStatus.Text = "Waiting for option chain data...";
        }

        private void OnTabPageUnloaded(object sender, RoutedEventArgs e)
        {
            Logger.Info("[OptionChainTabPage] OnTabPageUnloaded: Unsubscribing from events");

            SubscriptionManager.Instance.OptionPriceUpdated -= OnOptionPriceUpdated;
            SubscriptionManager.Instance.OptionStatusUpdated -= OnOptionStatusUpdated;
            SubscriptionManager.Instance.SymbolResolved -= OnSymbolResolved;
            MarketAnalyzerLogic.Instance.OptionsGenerated -= OnOptionsGenerated;

            // Unsubscribe from synthetic straddle price events
            var adapter = Connector.Instance.GetAdapter() as QAAdapter;
            if (adapter?.SyntheticStraddleService != null)
            {
                adapter.SyntheticStraddleService.StraddlePriceCalculated -= OnStraddlePriceCalculated;
            }
        }

        private void OnOptionsGenerated(List<MappedInstrument> options)
        {
            Logger.Info($"[OptionChainTabPage] OnOptionsGenerated: Received {options.Count} options");

            Dispatcher.InvokeAsync(() =>
            {
                try
                {
                    _rows.Clear();
                    _symbolToRowMap.Clear();
                    _generatedToZerodhaMap.Clear();
                    _straddleSymbolToRowMap.Clear();

                    if (options.Count == 0) return;

                    // Get underlying and expiry from first option
                    var first = options.First();
                    _underlying = first.underlying;
                    _expiry = first.expiry;

                    _lblUnderlying.Text = _underlying;
                    _lblExpiry.Text = _expiry.HasValue ? _expiry.Value.ToString("dd-MMM-yyyy") : "---";

                    // Group options by strike
                    var strikeGroups = options
                        .Where(o => o.strike.HasValue)
                        .GroupBy(o => o.strike.Value)
                        .OrderBy(g => g.Key);

                    foreach (var group in strikeGroups)
                    {
                        var row = new OptionChainRow { Strike = group.Key };

                        var ce = group.FirstOrDefault(o => o.option_type == "CE");
                        var pe = group.FirstOrDefault(o => o.option_type == "PE");

                        if (ce != null)
                        {
                            row.CESymbol = ce.symbol;
                            row.CEStatus = "Pending";
                            _symbolToRowMap[ce.symbol] = (row, "CE");
                            Logger.Debug($"[OptionChainTabPage] Mapped CE: {ce.symbol} -> Strike {row.Strike}");
                        }

                        if (pe != null)
                        {
                            row.PESymbol = pe.symbol;
                            row.PEStatus = "Pending";
                            _symbolToRowMap[pe.symbol] = (row, "PE");
                            Logger.Debug($"[OptionChainTabPage] Mapped PE: {pe.symbol} -> Strike {row.Strike}");
                        }

                        // Create straddle symbol mapping (e.g., NIFTY25DEC24000_STRDL)
                        if (_expiry.HasValue && ce != null && pe != null)
                        {
                            string monthAbbr = _expiry.Value.ToString("MMM").ToUpper();
                            string straddleSymbol = $"{_underlying}{_expiry.Value:yy}{monthAbbr}{group.Key:F0}_STRDL";
                            row.StraddleSymbol = straddleSymbol;
                            _straddleSymbolToRowMap[straddleSymbol] = row;
                            Logger.Debug($"[OptionChainTabPage] Mapped Straddle: {straddleSymbol} -> Strike {row.Strike}");
                        }

                        _rows.Add(row);
                    }

                    _lblStatus.Text = $"Loaded {_rows.Count} strikes for {_underlying}";
                    _listView.Items.Refresh();

                    Logger.Info($"[OptionChainTabPage] OnOptionsGenerated: Created {_rows.Count} strike rows, mapped {_symbolToRowMap.Count} symbols, {_straddleSymbolToRowMap.Count} straddles");
                }
                catch (Exception ex)
                {
                    Logger.Error($"[OptionChainTabPage] OnOptionsGenerated: Exception - {ex.Message}", ex);
                }
            });
        }

        private void OnSymbolResolved(string generatedSymbol, string zerodhaSymbol)
        {
            Logger.Info($"[OptionChainTabPage] OnSymbolResolved: {generatedSymbol} -> {zerodhaSymbol}");

            Dispatcher.InvokeAsync(() =>
            {
                try
                {
                    _generatedToZerodhaMap[generatedSymbol] = zerodhaSymbol;

                    if (_symbolToRowMap.TryGetValue(generatedSymbol, out var mapping))
                    {
                        _symbolToRowMap[zerodhaSymbol] = mapping;
                        Logger.Debug($"[OptionChainTabPage] OnSymbolResolved: Added zerodha mapping {zerodhaSymbol} -> Strike {mapping.row.Strike} {mapping.optionType}");
                    }
                }
                catch (Exception ex)
                {
                    Logger.Error($"[OptionChainTabPage] OnSymbolResolved: Exception - {ex.Message}", ex);
                }
            });
        }

        private void OnOptionPriceUpdated(string symbol, double price)
        {
            Dispatcher.InvokeAsync(() =>
            {
                try
                {
                    if (_symbolToRowMap.TryGetValue(symbol, out var mapping))
                    {
                        var (row, optionType) = mapping;

                        if (optionType == "CE")
                        {
                            row.CELast = price.ToString("F2");
                            row.CEPrice = price;
                        }
                        else
                        {
                            row.PELast = price.ToString("F2");
                            row.PEPrice = price;
                        }

                        Logger.Debug($"[OptionChainTabPage] OnOptionPriceUpdated: {symbol} -> Strike {row.Strike} {optionType} = {price:F2}");

                        UpdateATMStrike();
                        _listView.Items.Refresh();
                    }
                    else
                    {
                        Logger.Warn($"[OptionChainTabPage] OnOptionPriceUpdated: No mapping found for symbol '{symbol}'");
                    }
                }
                catch (Exception ex)
                {
                    Logger.Error($"[OptionChainTabPage] OnOptionPriceUpdated: Exception - {ex.Message}", ex);
                }
            });
        }

        private void OnOptionStatusUpdated(string symbol, string status)
        {
            Dispatcher.InvokeAsync(() =>
            {
                try
                {
                    if (_symbolToRowMap.TryGetValue(symbol, out var mapping))
                    {
                        var (row, optionType) = mapping;

                        if (optionType == "CE")
                        {
                            row.CEStatus = status;
                        }
                        else
                        {
                            row.PEStatus = status;
                        }

                        Logger.Debug($"[OptionChainTabPage] OnOptionStatusUpdated: {symbol} -> Strike {row.Strike} {optionType} = {status}");
                        _listView.Items.Refresh();
                    }
                    else
                    {
                        Logger.Warn($"[OptionChainTabPage] OnOptionStatusUpdated: No mapping found for symbol '{symbol}'");
                    }
                }
                catch (Exception ex)
                {
                    Logger.Error($"[OptionChainTabPage] OnOptionStatusUpdated: Exception - {ex.Message}", ex);
                }
            });
        }

        /// <summary>
        /// Handles synthetic straddle price updates from SyntheticStraddleService
        /// </summary>
        private void OnStraddlePriceCalculated(string straddleSymbol, double price, double cePrice, double pePrice)
        {
            Dispatcher.InvokeAsync(() =>
            {
                try
                {
                    if (_straddleSymbolToRowMap.TryGetValue(straddleSymbol, out var row))
                    {
                        row.SyntheticStraddlePrice = price;
                        Logger.Debug($"[OptionChainTabPage] OnStraddlePriceCalculated: {straddleSymbol} -> Strike {row.Strike} = {price:F2} (CE={cePrice:F2}, PE={pePrice:F2})");

                        UpdateATMStrike();
                        _listView.Items.Refresh();
                    }
                    else
                    {
                        // Try to find by parsing the strike from the symbol (e.g., NIFTY25DEC24000_STRDL -> 24000)
                        Logger.Debug($"[OptionChainTabPage] OnStraddlePriceCalculated: No direct mapping for '{straddleSymbol}', attempting strike extraction");
                    }
                }
                catch (Exception ex)
                {
                    Logger.Error($"[OptionChainTabPage] OnStraddlePriceCalculated: Exception - {ex.Message}", ex);
                }
            });
        }

        private void UpdateATMStrike()
        {
            OptionChainRow atmRow = null;
            double minStraddle = double.MaxValue;

            double maxCE = 0;
            double maxPE = 0;

            foreach (var row in _rows)
            {
                row.IsATM = false;

                if (row.CEPrice > 0)
                    maxCE = Math.Max(maxCE, row.CEPrice);
                if (row.PEPrice > 0)
                    maxPE = Math.Max(maxPE, row.PEPrice);

                if (row.CEPrice > 0 && row.PEPrice > 0)
                {
                    double straddle = row.CEPrice + row.PEPrice;
                    if (straddle < minStraddle)
                    {
                        minStraddle = straddle;
                        atmRow = row;
                    }
                }
            }

            _maxOptionPrice = Math.Max(maxCE, maxPE);
            if (_maxOptionPrice > 0)
            {
                foreach (var row in _rows)
                {
                    row.CEHistogramWidth = row.CEPrice > 0 ? (row.CEPrice / _maxOptionPrice) * 100.0 : 0;
                    row.PEHistogramWidth = row.PEPrice > 0 ? (row.PEPrice / _maxOptionPrice) * 100.0 : 0;
                }
            }

            if (atmRow != null)
            {
                atmRow.IsATM = true;
                _lblATMStrike.Text = $"{atmRow.Strike:F0} ({minStraddle:F2})";
                Logger.Debug($"[OptionChainTabPage] UpdateATMStrike: ATM={atmRow.Strike}, Straddle={minStraddle:F2}");
            }
        }

        #region IInstrumentProvider Implementation

        /// <summary>
        /// IInstrumentProvider.Instrument - Required for instrument linking with colored link buttons
        /// When set, this propagates the instrument to other windows linked with the same color
        /// NinjaTrader automatically handles the link button UI when NTTabPage implements IInstrumentProvider
        /// </summary>
        public Instrument Instrument
        {
            get { return _instrument; }
            set
            {
                if (_instrument != null)
                {
                    Logger.Debug($"[OptionChainTabPage] Instrument setter: Unsubscribing from {_instrument.FullName}");
                }

                if (value != null)
                {
                    Logger.Info($"[OptionChainTabPage] Instrument setter: Setting instrument to {value.FullName}");

                    Dispatcher.InvokeAsync(() =>
                    {
                        _lblSelectedInstrument.Text = value.FullName;
                        _lblSelectedInstrument.FontStyle = FontStyles.Normal;
                        _lblSelectedInstrument.Foreground = new SolidColorBrush(Color.FromRgb(100, 200, 255));
                    });
                }

                _instrument = value;

                // Update the tab header name
                UpdateHeader();
            }
        }

        #endregion

        #region Row Click Handling for Instrument Linking

        /// <summary>
        /// Handles mouse click on ListView to detect which cell (CE or PE) was clicked
        /// </summary>
        private void OnListViewMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
        {
            try
            {
                var item = _listView.SelectedItem as OptionChainRow;
                if (item == null) return;

                var mousePos = e.GetPosition(_listView);
                var listViewItem = _listView.ItemContainerGenerator.ContainerFromItem(item) as ListViewItem;
                if (listViewItem == null) return;

                var itemPos = e.GetPosition(listViewItem);

                // Calculate column boundaries
                // CE Status (80) + CE Last (100) = 180 for CE side
                // Strike (80) = 260
                // PE Last (100) + PE Status (80) = 440 for PE side
                double ceEndX = 180;
                double strikeEndX = 260;

                string symbolToLink = null;
                string optionType = null;

                if (itemPos.X < ceEndX && !string.IsNullOrEmpty(item.CESymbol))
                {
                    symbolToLink = item.CESymbol;
                    optionType = "CE";
                }
                else if (itemPos.X > strikeEndX && !string.IsNullOrEmpty(item.PESymbol))
                {
                    symbolToLink = item.PESymbol;
                    optionType = "PE";
                }

                if (!string.IsNullOrEmpty(symbolToLink))
                {
                    Logger.Info($"[OptionChainTabPage] OnListViewMouseLeftButtonUp: Clicked {optionType} for strike {item.Strike}, symbol={symbolToLink}");

                    var ntInstrument = Instrument.GetInstrument(symbolToLink);
                    if (ntInstrument != null)
                    {
                        // Set the Instrument property - NinjaTrader's linking mechanism handles propagation
                        Instrument = ntInstrument;
                        Logger.Info($"[OptionChainTabPage] OnListViewMouseLeftButtonUp: Instrument set to {ntInstrument.FullName}");
                    }
                    else
                    {
                        Logger.Warn($"[OptionChainTabPage] OnListViewMouseLeftButtonUp: Could not get NinjaTrader instrument for {symbolToLink}");

                        _lblSelectedInstrument.Text = $"{symbolToLink} (not in NT)";
                        _lblSelectedInstrument.FontStyle = FontStyles.Italic;
                        _lblSelectedInstrument.Foreground = new SolidColorBrush(Color.FromRgb(255, 200, 100));
                    }
                }
            }
            catch (Exception ex)
            {
                Logger.Error($"[OptionChainTabPage] OnListViewMouseLeftButtonUp: Exception - {ex.Message}", ex);
            }
        }

        #endregion

        #region NTTabPage Required Overrides

        /// <summary>
        /// Called by TabControl when tab is being removed or window is closed
        /// </summary>
        public override void Cleanup()
        {
            Logger.Info("[OptionChainTabPage] Cleanup: Cleaning up resources");

            // Unsubscribe from events
            SubscriptionManager.Instance.OptionPriceUpdated -= OnOptionPriceUpdated;
            SubscriptionManager.Instance.OptionStatusUpdated -= OnOptionStatusUpdated;
            SubscriptionManager.Instance.SymbolResolved -= OnSymbolResolved;
            MarketAnalyzerLogic.Instance.OptionsGenerated -= OnOptionsGenerated;

            // Unsubscribe from synthetic straddle price events
            var adapter = Connector.Instance.GetAdapter() as QAAdapter;
            if (adapter?.SyntheticStraddleService != null)
            {
                adapter.SyntheticStraddleService.StraddlePriceCalculated -= OnStraddlePriceCalculated;
            }

            base.Cleanup();
        }

        /// <summary>
        /// NTTabPage member - determines the tab header name
        /// </summary>
        protected override string GetHeaderPart(string variable)
        {
            if (_instrument != null)
                return _instrument.FullName;

            if (!string.IsNullOrEmpty(_underlying))
                return $"{_underlying} Options";

            return "Option Chain";
        }

        /// <summary>
        /// NTTabPage member - restores elements from workspaces
        /// </summary>
        protected override void Restore(XElement element)
        {
            if (element == null)
                return;

            Logger.Debug("[OptionChainTabPage] Restore: Called");

            try
            {
                var instrumentAttr = element.Attribute("LastInstrument");
                if (instrumentAttr != null && !string.IsNullOrEmpty(instrumentAttr.Value))
                {
                    var instrument = Instrument.GetInstrument(instrumentAttr.Value);
                    if (instrument != null)
                    {
                        Instrument = instrument;
                    }
                }
            }
            catch (Exception ex)
            {
                Logger.Error($"[OptionChainTabPage] Restore: Exception - {ex.Message}", ex);
            }
        }

        /// <summary>
        /// NTTabPage member - saves elements to workspaces
        /// </summary>
        protected override void Save(XElement element)
        {
            if (element == null)
                return;

            Logger.Debug("[OptionChainTabPage] Save: Called");

            try
            {
                if (_instrument != null)
                {
                    element.SetAttributeValue("LastInstrument", _instrument.FullName);
                }
            }
            catch (Exception ex)
            {
                Logger.Error($"[OptionChainTabPage] Save: Exception - {ex.Message}", ex);
            }
        }

        /// <summary>
        /// Updates the tab header name by calling RefreshHeader() from base class
        /// </summary>
        private void UpdateHeader()
        {
            Dispatcher.InvokeAsync(() =>
            {
                try
                {
                    // Call the base NTTabPage.RefreshHeader() method to update the tab header
                    base.RefreshHeader();
                }
                catch { }
            });
        }

        #endregion
    }
}
