using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;
using System.Xml.Linq;
using NinjaTrader.Gui.Tools;
using QANinjaAdapter;
using QANinjaAdapter.Models;
using QANinjaAdapter.Services.Analysis;

namespace QANinjaAdapter.AddOns.MarketAnalyzer
{
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

        // Straddle = CE + PE
        public string StraddlePrice => (CEPrice > 0 && PEPrice > 0)
            ? (CEPrice + PEPrice).ToString("F2")
            : "---";

        public double StraddleValue => (CEPrice > 0 && PEPrice > 0) ? CEPrice + PEPrice : double.MaxValue;

        public bool IsATM { get; set; }
    }

    /// <summary>
    /// Option Chain window displaying CE/PE prices with Strike in the middle
    /// </summary>
    public class OptionChainWindow : NTWindow, IWorkspacePersistence
    {
        private ListView _listView;
        private GridView _gridView;
        private TextBlock _lblUnderlying;
        private TextBlock _lblExpiry;
        private TextBlock _lblATMStrike;
        private TextBlock _lblStatus;
        private ObservableCollection<OptionChainRow> _rows;

        // Symbol to row mapping for quick updates (supports both generated and zerodha symbols)
        private Dictionary<string, (OptionChainRow row, string optionType)> _symbolToRowMap =
            new Dictionary<string, (OptionChainRow, string)>();

        // Mapping from generated symbol to zerodha symbol (resolved from DB)
        private Dictionary<string, string> _generatedToZerodhaMap = new Dictionary<string, string>();

        // NinjaTrader-style colors
        private static readonly SolidColorBrush _bgColor = new SolidColorBrush(Color.FromRgb(27, 27, 28));
        private static readonly SolidColorBrush _fgColor = new SolidColorBrush(Color.FromRgb(212, 212, 212));
        private static readonly SolidColorBrush _headerBg = new SolidColorBrush(Color.FromRgb(37, 37, 38));
        private static readonly SolidColorBrush _borderColor = new SolidColorBrush(Color.FromRgb(51, 51, 51));
        private static readonly SolidColorBrush _atmBg = new SolidColorBrush(Color.FromRgb(60, 80, 60)); // Greenish highlight for ATM
        private static readonly SolidColorBrush _strikeBg = new SolidColorBrush(Color.FromRgb(45, 45, 46));
        private static readonly FontFamily _ntFont = new FontFamily("Segoe UI");

        private string _underlying = "NIFTY";
        private DateTime? _expiry;

        public OptionChainWindow()
        {
            Logger.Info("[OptionChainWindow] Constructor: Creating window");

            Caption = "Option Chain";
            Width = 700;
            Height = 600;

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
                Loaded += OnWindowLoaded;
                Unloaded += OnWindowUnloaded;

                Logger.Info("[OptionChainWindow] Constructor: Window created successfully");
            }
            catch (Exception ex)
            {
                Logger.Error($"[OptionChainWindow] Constructor: Exception - {ex.Message}", ex);
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

            border.Child = grid;
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
            _gridView.Columns.Add(CreateColumn("CE Last", "CELast", 80, HorizontalAlignment.Right));

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
            _gridView.Columns.Add(CreateColumn("PE Last", "PELast", 80, HorizontalAlignment.Right));
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

        private void OnWindowLoaded(object sender, RoutedEventArgs e)
        {
            Logger.Info("[OptionChainWindow] OnWindowLoaded: Subscribing to events");

            // Subscribe to option events
            SubscriptionManager.Instance.OptionPriceUpdated += OnOptionPriceUpdated;
            SubscriptionManager.Instance.OptionStatusUpdated += OnOptionStatusUpdated;
            SubscriptionManager.Instance.SymbolResolved += OnSymbolResolved;
            MarketAnalyzerLogic.Instance.OptionsGenerated += OnOptionsGenerated;

            _lblStatus.Text = "Waiting for option chain data...";
        }

        private void OnWindowUnloaded(object sender, RoutedEventArgs e)
        {
            Logger.Info("[OptionChainWindow] OnWindowUnloaded: Unsubscribing from events");

            SubscriptionManager.Instance.OptionPriceUpdated -= OnOptionPriceUpdated;
            SubscriptionManager.Instance.OptionStatusUpdated -= OnOptionStatusUpdated;
            SubscriptionManager.Instance.SymbolResolved -= OnSymbolResolved;
            MarketAnalyzerLogic.Instance.OptionsGenerated -= OnOptionsGenerated;
        }

        private void OnOptionsGenerated(List<MappedInstrument> options)
        {
            Logger.Info($"[OptionChainWindow] OnOptionsGenerated: Received {options.Count} options");

            Dispatcher.InvokeAsync(() =>
            {
                try
                {
                    _rows.Clear();
                    _symbolToRowMap.Clear();
                    _generatedToZerodhaMap.Clear();

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
                            // Use generated symbol initially - will be updated via SymbolResolved
                            row.CESymbol = ce.symbol;
                            row.CEStatus = "Pending";
                            _symbolToRowMap[ce.symbol] = (row, "CE");
                            Logger.Debug($"[OptionChainWindow] Mapped CE: {ce.symbol} -> Strike {row.Strike}");
                        }

                        if (pe != null)
                        {
                            // Use generated symbol initially - will be updated via SymbolResolved
                            row.PESymbol = pe.symbol;
                            row.PEStatus = "Pending";
                            _symbolToRowMap[pe.symbol] = (row, "PE");
                            Logger.Debug($"[OptionChainWindow] Mapped PE: {pe.symbol} -> Strike {row.Strike}");
                        }

                        _rows.Add(row);
                    }

                    _lblStatus.Text = $"Loaded {_rows.Count} strikes for {_underlying}";
                    _listView.Items.Refresh();

                    Logger.Info($"[OptionChainWindow] OnOptionsGenerated: Created {_rows.Count} strike rows, mapped {_symbolToRowMap.Count} symbols");
                }
                catch (Exception ex)
                {
                    Logger.Error($"[OptionChainWindow] OnOptionsGenerated: Exception - {ex.Message}", ex);
                }
            });
        }

        private void OnSymbolResolved(string generatedSymbol, string zerodhaSymbol)
        {
            Logger.Info($"[OptionChainWindow] OnSymbolResolved: {generatedSymbol} -> {zerodhaSymbol}");

            Dispatcher.InvokeAsync(() =>
            {
                try
                {
                    // Store the mapping
                    _generatedToZerodhaMap[generatedSymbol] = zerodhaSymbol;

                    // If we have a row mapped to the generated symbol, also map the zerodha symbol to it
                    if (_symbolToRowMap.TryGetValue(generatedSymbol, out var mapping))
                    {
                        _symbolToRowMap[zerodhaSymbol] = mapping;
                        Logger.Debug($"[OptionChainWindow] OnSymbolResolved: Added zerodha mapping {zerodhaSymbol} -> Strike {mapping.row.Strike} {mapping.optionType}");
                    }
                }
                catch (Exception ex)
                {
                    Logger.Error($"[OptionChainWindow] OnSymbolResolved: Exception - {ex.Message}", ex);
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

                        Logger.Debug($"[OptionChainWindow] OnOptionPriceUpdated: {symbol} -> Strike {row.Strike} {optionType} = {price:F2}");

                        // Recalculate ATM after price update
                        UpdateATMStrike();
                        _listView.Items.Refresh();
                    }
                    else
                    {
                        Logger.Warn($"[OptionChainWindow] OnOptionPriceUpdated: No mapping found for symbol '{symbol}'");
                    }
                }
                catch (Exception ex)
                {
                    Logger.Error($"[OptionChainWindow] OnOptionPriceUpdated: Exception - {ex.Message}", ex);
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

                        Logger.Debug($"[OptionChainWindow] OnOptionStatusUpdated: {symbol} -> Strike {row.Strike} {optionType} = {status}");
                        _listView.Items.Refresh();
                    }
                    else
                    {
                        Logger.Warn($"[OptionChainWindow] OnOptionStatusUpdated: No mapping found for symbol '{symbol}'");
                    }
                }
                catch (Exception ex)
                {
                    Logger.Error($"[OptionChainWindow] OnOptionStatusUpdated: Exception - {ex.Message}", ex);
                }
            });
        }

        private void UpdateATMStrike()
        {
            // Find the strike with cheapest straddle where both CE and PE have non-zero prices
            OptionChainRow atmRow = null;
            double minStraddle = double.MaxValue;

            foreach (var row in _rows)
            {
                row.IsATM = false; // Reset

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

            if (atmRow != null)
            {
                atmRow.IsATM = true;
                _lblATMStrike.Text = $"{atmRow.Strike:F0} ({minStraddle:F2})";
                Logger.Debug($"[OptionChainWindow] UpdateATMStrike: ATM={atmRow.Strike}, Straddle={minStraddle:F2}");
            }
        }

        // IWorkspacePersistence Implementation
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
}
