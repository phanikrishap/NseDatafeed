using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;
using System.Xml.Linq;
using NinjaTrader.Cbi;
using NinjaTrader.Gui;
using NinjaTrader.Gui.Tools;
using NinjaTrader.NinjaScript;
using ZerodhaDatafeedAdapter;
using ZerodhaDatafeedAdapter.Models;
using ZerodhaDatafeedAdapter.Services.Analysis;
using ZerodhaDatafeedAdapter.Services.Instruments;
using ZerodhaDatafeedAdapter.AddOns.MarketAnalyzer;

namespace NinjaTrader.NinjaScript.AddOns
{
    /// <summary>
    /// NinjaTrader AddOn that integrates the GIFT NIFTY Market Analyzer into the Control Center menu
    /// and auto-launches the window on startup.
    /// </summary>
    public class MarketAnalyzerAddOn : AddOnBase
    {
        private NTMenuItem _menuItem;
        private NTMenuItem _existingNewMenu;
        private static bool _autoOpened = false;

        protected override void OnStateChange()
        {
            if (State == State.SetDefaults)
            {
                Description = "GIFT NIFTY Market Analyzer AddOn";
                Name = "MarketAnalyzerAddOn";
                Logger.Info("[MarketAnalyzerAddOn] OnStateChange(): SetDefaults - AddOn initialized");
            }
            else if (State == State.Terminated)
            {
                Logger.Info("[MarketAnalyzerAddOn] OnStateChange(): Terminated");
            }
        }

        protected override void OnWindowCreated(Window window)
        {
            Logger.Debug($"[MarketAnalyzerAddOn] OnWindowCreated(): Window type = {window.GetType().Name}");

            var controlCenter = window as ControlCenter;
            if (controlCenter == null)
            {
                Logger.Debug("[MarketAnalyzerAddOn] OnWindowCreated(): Not a ControlCenter window, skipping");
                return;
            }

            Logger.Info("[MarketAnalyzerAddOn] OnWindowCreated(): ControlCenter detected");

            // Auto-Launch Logic - only on first ControlCenter creation
            if (!_autoOpened)
            {
                _autoOpened = true;
                Logger.Info("[MarketAnalyzerAddOn] OnWindowCreated(): Auto-launching MarketAnalyzerWindow...");

                Core.Globals.RandomDispatcher.BeginInvoke(new Action(() =>
                {
                    try
                    {
                        // Open Market Analyzer window
                        Logger.Debug("[MarketAnalyzerAddOn] OnWindowCreated(): Creating MarketAnalyzerWindow instance");
                        var win = new MarketAnalyzerWindow();
                        win.Show();
                        Logger.Info("[MarketAnalyzerAddOn] OnWindowCreated(): MarketAnalyzerWindow shown successfully");

                        // Also open Option Chain window automatically
                        Logger.Debug("[MarketAnalyzerAddOn] OnWindowCreated(): Creating OptionChainWindow instance");
                        var chainWin = new OptionChainWindow();
                        chainWin.Show();
                        Logger.Info("[MarketAnalyzerAddOn] OnWindowCreated(): OptionChainWindow shown successfully");
                    }
                    catch (Exception ex)
                    {
                        Logger.Error($"[MarketAnalyzerAddOn] OnWindowCreated(): Failed to create window - {ex.Message}", ex);
                    }
                }));
            }

            // Menu Integration
            Logger.Debug("[MarketAnalyzerAddOn] OnWindowCreated(): Setting up menu integration");
            _existingNewMenu = controlCenter.FindFirst("ControlCenterMenuItemNew") as NTMenuItem;

            if (_existingNewMenu == null)
            {
                Logger.Warn("[MarketAnalyzerAddOn] OnWindowCreated(): Could not find ControlCenterMenuItemNew - menu integration skipped");
                return;
            }

            _menuItem = new NTMenuItem
            {
                Header = "GIFT NIFTY Market Analyzer",
                Style = Application.Current.TryFindResource("MainMenuItem") as Style
            };

            _menuItem.Click += OnMenuItemClick;
            _existingNewMenu.Items.Add(_menuItem);
            Logger.Info("[MarketAnalyzerAddOn] OnWindowCreated(): Menu item added successfully");
        }

        protected override void OnWindowDestroyed(Window window)
        {
            if (_menuItem != null && window is ControlCenter)
            {
                Logger.Info("[MarketAnalyzerAddOn] OnWindowDestroyed(): ControlCenter closing, cleaning up menu item");

                if (_existingNewMenu != null && _existingNewMenu.Items.Contains(_menuItem))
                {
                    _existingNewMenu.Items.Remove(_menuItem);
                }
                _menuItem.Click -= OnMenuItemClick;
                _menuItem = null;

                Logger.Info("[MarketAnalyzerAddOn] OnWindowDestroyed(): Menu item cleanup complete");
            }
        }

        private void OnMenuItemClick(object sender, RoutedEventArgs e)
        {
            Logger.Info("[MarketAnalyzerAddOn] OnMenuItemClick(): User clicked menu item");

            Core.Globals.RandomDispatcher.BeginInvoke(new Action(() =>
            {
                try
                {
                    var win = new MarketAnalyzerWindow();
                    win.Show();
                    Logger.Info("[MarketAnalyzerAddOn] OnMenuItemClick(): New MarketAnalyzerWindow opened");
                }
                catch (Exception ex)
                {
                    Logger.Error($"[MarketAnalyzerAddOn] OnMenuItemClick(): Failed to create window - {ex.Message}", ex);
                }
            }));
        }
    }
}

namespace ZerodhaDatafeedAdapter.AddOns.MarketAnalyzer
{
    /// <summary>
    /// Row item for the Market Analyzer ListView
    /// </summary>
    public class AnalyzerRow
    {
        public string Symbol { get; set; }
        public string InternalSymbol { get; set; } // The actual trading symbol (e.g., NIFTY25DEC2325300CE)
        public string Last { get; set; }
        public string Change { get; set; }
        public string ProjOpen { get; set; }
        public string Expiry { get; set; }
        public string LastUpdate { get; set; } // HH:mm:ss format
        public string Status { get; set; }
        public bool IsPositive { get; set; }
        public bool IsOption { get; set; }
    }

    /// <summary>
    /// Dashboard window for the GIFT NIFTY Market Analyzer.
    /// Displays real-time prices in a NinjaTrader-style ListView with resizable columns.
    /// </summary>
    public class MarketAnalyzerWindow : NTWindow, IWorkspacePersistence
    {
        private ListView _listView;
        private GridView _gridView;
        private Button _btnRefresh;
        private TextBlock _lblStatus;
        private ObservableCollection<AnalyzerRow> _rows;

        // NinjaTrader-style colors
        private static readonly SolidColorBrush _bgColor = new SolidColorBrush(Color.FromRgb(27, 27, 28));
        private static readonly SolidColorBrush _fgColor = new SolidColorBrush(Color.FromRgb(212, 212, 212));
        private static readonly SolidColorBrush _greenColor = new SolidColorBrush(Color.FromRgb(38, 166, 91));
        private static readonly SolidColorBrush _redColor = new SolidColorBrush(Color.FromRgb(207, 70, 71));
        private static readonly SolidColorBrush _headerBg = new SolidColorBrush(Color.FromRgb(37, 37, 38));
        private static readonly SolidColorBrush _borderColor = new SolidColorBrush(Color.FromRgb(51, 51, 51));
        private static readonly SolidColorBrush _rowAltBg = new SolidColorBrush(Color.FromRgb(30, 30, 31));
        private static readonly SolidColorBrush _selectionBg = new SolidColorBrush(Color.FromRgb(51, 51, 52));
        private static readonly FontFamily _ntFont = new FontFamily("Segoe UI");

        public MarketAnalyzerWindow()
        {
            Logger.Info("[MarketAnalyzerWindow] Constructor: Creating window");

            Caption = "GIFT NIFTY Market Analyzer";
            Width = 650;
            Height = 400;

            try
            {
                Logger.Debug("[MarketAnalyzerWindow] Constructor: Building NinjaTrader-style UI");

                _rows = new ObservableCollection<AnalyzerRow>();

                // Initialize with index rows
                _rows.Add(new AnalyzerRow { Symbol = "GIFT NIFTY", Last = "---", Change = "---", ProjOpen = "N/A", Expiry = "---", Status = "Pending", IsPositive = true, IsOption = false });
                _rows.Add(new AnalyzerRow { Symbol = "NIFTY 50", Last = "---", Change = "---", ProjOpen = "---", Expiry = "---", Status = "Pending", IsPositive = true, IsOption = false });
                _rows.Add(new AnalyzerRow { Symbol = "SENSEX", Last = "---", Change = "---", ProjOpen = "---", Expiry = "---", Status = "Pending", IsPositive = true, IsOption = false });

                var dockPanel = new DockPanel { Background = _bgColor };
                Content = dockPanel;

                // Toolbar
                var toolbar = CreateToolbar();
                DockPanel.SetDock(toolbar, Dock.Top);
                dockPanel.Children.Add(toolbar);

                // Status bar at bottom
                var statusBar = CreateStatusBar();
                DockPanel.SetDock(statusBar, Dock.Bottom);
                dockPanel.Children.Add(statusBar);

                // Main ListView
                _listView = CreateListView();
                dockPanel.Children.Add(_listView);

                // Hook Events
                Loaded += OnWindowLoaded;
                Unloaded += OnWindowUnloaded;

                // Subscribe to Service Events (indices only - options handled by OptionChainWindow)
                Logger.Debug("[MarketAnalyzerWindow] Constructor: Subscribing to MarketAnalyzerLogic events");
                MarketAnalyzerLogic.Instance.StatusUpdated += OnStatusUpdated;
                MarketAnalyzerLogic.Instance.OptionsGenerated += OnOptionsGenerated;
                MarketAnalyzerLogic.Instance.TickerUpdated += OnTickerUpdated;
                MarketAnalyzerLogic.Instance.HistoricalDataStatusChanged += OnHistoricalDataStatusChanged;

                Logger.Info("[MarketAnalyzerWindow] Constructor: Window created successfully");
            }
            catch (Exception ex)
            {
                Logger.Error($"[MarketAnalyzerWindow] Constructor: Exception - {ex.Message}", ex);
                throw;
            }
        }

        private Border CreateToolbar()
        {
            var border = new Border
            {
                Background = _headerBg,
                BorderBrush = _borderColor,
                BorderThickness = new Thickness(0, 0, 0, 1),
                Padding = new Thickness(5)
            };

            var panel = new StackPanel { Orientation = Orientation.Horizontal };

            _btnRefresh = new Button
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
            _btnRefresh.Click += (s, e) =>
            {
                Logger.Info("[MarketAnalyzerWindow] Refresh button clicked");
                MarketAnalyzerService.Instance.Start();
            };
            panel.Children.Add(_btnRefresh);

            var title = new TextBlock
            {
                Text = "Index Monitor",
                FontFamily = _ntFont,
                FontSize = 12,
                FontWeight = FontWeights.SemiBold,
                Foreground = _fgColor,
                VerticalAlignment = VerticalAlignment.Center
            };
            panel.Children.Add(title);

            border.Child = panel;
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
                Text = "Waiting for data...",
                FontFamily = _ntFont,
                FontSize = 11,
                Foreground = _fgColor
            };

            border.Child = _lblStatus;
            return border;
        }

        private ListView CreateListView()
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

            // Create GridView with resizable columns
            _gridView = new GridView();
            _gridView.AllowsColumnReorder = true;

            // Symbol column
            var symbolColumn = new GridViewColumn
            {
                Header = "Symbol",
                Width = 120,
                DisplayMemberBinding = new Binding("Symbol")
            };
            _gridView.Columns.Add(symbolColumn);

            // Last column
            var lastColumn = new GridViewColumn
            {
                Header = "Last",
                Width = 90,
                DisplayMemberBinding = new Binding("Last")
            };
            _gridView.Columns.Add(lastColumn);

            // Change% column - using CellTemplate for colored text
            var changeColumn = new GridViewColumn
            {
                Header = "Chg%",
                Width = 80
            };
            var changeTemplate = new DataTemplate();
            var changeFactory = new FrameworkElementFactory(typeof(TextBlock));
            changeFactory.SetBinding(TextBlock.TextProperty, new Binding("Change"));
            changeFactory.SetValue(TextBlock.HorizontalAlignmentProperty, HorizontalAlignment.Right);
            changeFactory.SetValue(TextBlock.PaddingProperty, new Thickness(0, 0, 5, 0));
            changeTemplate.VisualTree = changeFactory;
            changeColumn.CellTemplate = changeTemplate;
            _gridView.Columns.Add(changeColumn);

            // Proj Open column
            var projColumn = new GridViewColumn
            {
                Header = "Proj Open",
                Width = 90,
                DisplayMemberBinding = new Binding("ProjOpen")
            };
            _gridView.Columns.Add(projColumn);

            // Expiry column
            var expiryColumn = new GridViewColumn
            {
                Header = "Expiry",
                Width = 90,
                DisplayMemberBinding = new Binding("Expiry")
            };
            _gridView.Columns.Add(expiryColumn);

            // Last Update column - shows HH:mm:ss of last price update
            var lastUpdateColumn = new GridViewColumn
            {
                Header = "Updated",
                Width = 70,
                DisplayMemberBinding = new Binding("LastUpdate")
            };
            _gridView.Columns.Add(lastUpdateColumn);

            // Status column
            var statusColumn = new GridViewColumn
            {
                Header = "Status",
                Width = 100,
                DisplayMemberBinding = new Binding("Status")
            };
            _gridView.Columns.Add(statusColumn);

            listView.View = _gridView;

            // Apply NinjaTrader-like styling
            var style = new Style(typeof(ListViewItem));
            style.Setters.Add(new Setter(ListViewItem.BackgroundProperty, _bgColor));
            style.Setters.Add(new Setter(ListViewItem.ForegroundProperty, _fgColor));
            style.Setters.Add(new Setter(ListViewItem.BorderThicknessProperty, new Thickness(0)));
            style.Setters.Add(new Setter(ListViewItem.PaddingProperty, new Thickness(2)));
            style.Setters.Add(new Setter(ListViewItem.FontFamilyProperty, _ntFont));

            // Alternating row colors
            var trigger = new Trigger { Property = ListViewItem.IsSelectedProperty, Value = true };
            trigger.Setters.Add(new Setter(ListViewItem.BackgroundProperty, _selectionBg));
            style.Triggers.Add(trigger);

            listView.ItemContainerStyle = style;

            return listView;
        }

        private void OnWindowLoaded(object sender, RoutedEventArgs e)
        {
            Logger.Info("[MarketAnalyzerWindow] OnWindowLoaded: Window loaded, starting service");

            try
            {
                MarketAnalyzerService.Instance.Start();
                Logger.Info("[MarketAnalyzerWindow] OnWindowLoaded: Service started");
                _lblStatus.Text = "Service started - connecting...";
            }
            catch (Exception ex)
            {
                Logger.Error($"[MarketAnalyzerWindow] OnWindowLoaded: Exception - {ex.Message}", ex);
                _lblStatus.Text = $"Error: {ex.Message}";
            }
        }

        private void OnWindowUnloaded(object sender, RoutedEventArgs e)
        {
            Logger.Info("[MarketAnalyzerWindow] OnWindowUnloaded: Cleaning up event handlers");

            MarketAnalyzerLogic.Instance.StatusUpdated -= OnStatusUpdated;
            MarketAnalyzerLogic.Instance.OptionsGenerated -= OnOptionsGenerated;
            MarketAnalyzerLogic.Instance.TickerUpdated -= OnTickerUpdated;
            MarketAnalyzerLogic.Instance.HistoricalDataStatusChanged -= OnHistoricalDataStatusChanged;

            Logger.Info("[MarketAnalyzerWindow] OnWindowUnloaded: Cleanup complete");
        }

        private void OnTickerUpdated(TickerData ticker)
        {
            Dispatcher.InvokeAsync(() =>
            {
                try
                {
                    var logic = MarketAnalyzerLogic.Instance;
                    AnalyzerRow row = null;

                    if (ticker.Symbol == "GIFT NIFTY")
                    {
                        row = _rows.FirstOrDefault(r => r.Symbol == "GIFT NIFTY");
                        if (row != null)
                        {
                            row.Last = ticker.LastPriceDisplay;
                            row.Change = ticker.NetChangePercentDisplay;
                            row.LastUpdate = ticker.LastUpdateTimeDisplay;
                            row.IsPositive = ticker.IsPositive;
                        }
                    }
                    else if (ticker.Symbol == "NIFTY")
                    {
                        row = _rows.FirstOrDefault(r => r.Symbol == "NIFTY 50");
                        if (row != null)
                        {
                            row.Last = ticker.LastPriceDisplay;
                            row.Change = ticker.NetChangePercentDisplay;
                            row.ProjOpen = logic.NiftyTicker.ProjectedOpenDisplay;
                            row.LastUpdate = ticker.LastUpdateTimeDisplay;
                            row.IsPositive = ticker.IsPositive;
                        }
                    }
                    else if (ticker.Symbol == "SENSEX")
                    {
                        row = _rows.FirstOrDefault(r => r.Symbol == "SENSEX");
                        if (row != null)
                        {
                            row.Last = ticker.LastPriceDisplay;
                            row.Change = ticker.NetChangePercentDisplay;
                            row.ProjOpen = logic.SensexTicker.ProjectedOpenDisplay;
                            row.LastUpdate = ticker.LastUpdateTimeDisplay;
                            row.IsPositive = ticker.IsPositive;
                        }
                    }

                    // Refresh ListView
                    _listView.Items.Refresh();
                    _lblStatus.Text = $"Last update: {DateTime.Now:HH:mm:ss}";
                }
                catch (Exception ex)
                {
                    Logger.Error($"[MarketAnalyzerWindow] OnTickerUpdated: Exception - {ex.Message}", ex);
                }
            });
        }

        private void OnHistoricalDataStatusChanged(string symbol, string status)
        {
            Dispatcher.InvokeAsync(() =>
            {
                try
                {
                    AnalyzerRow row = null;

                    if (symbol.Equals("GIFT_NIFTY", StringComparison.OrdinalIgnoreCase) || symbol == "GIFT NIFTY")
                    {
                        row = _rows.FirstOrDefault(r => r.Symbol == "GIFT NIFTY");
                    }
                    else if (symbol.Equals("NIFTY", StringComparison.OrdinalIgnoreCase))
                    {
                        row = _rows.FirstOrDefault(r => r.Symbol == "NIFTY 50");
                    }
                    else if (symbol.Equals("SENSEX", StringComparison.OrdinalIgnoreCase))
                    {
                        row = _rows.FirstOrDefault(r => r.Symbol == "SENSEX");
                    }

                    if (row != null)
                    {
                        row.Status = status;
                        _listView.Items.Refresh();
                    }
                }
                catch (Exception ex)
                {
                    Logger.Error($"[MarketAnalyzerWindow] OnHistoricalDataStatusChanged: Exception - {ex.Message}", ex);
                }
            });
        }

        private void OnStatusUpdated(string msg)
        {
            Logger.Debug($"[MarketAnalyzerWindow] OnStatusUpdated: {msg}");

            Dispatcher.InvokeAsync(() =>
            {
                try
                {
                    _lblStatus.Text = msg;
                }
                catch (Exception ex)
                {
                    Logger.Error($"[MarketAnalyzerWindow] OnStatusUpdated: Exception - {ex.Message}", ex);
                }
            });
        }

        private void OnOptionsGenerated(List<MappedInstrument> options)
        {
            // Options are handled by OptionChainWindow - just update status here
            Logger.Info($"[MarketAnalyzerWindow] OnOptionsGenerated: {options.Count} options generated (displayed in Option Chain window)");

            Dispatcher.InvokeAsync(() =>
            {
                if (options.Count > 0)
                {
                    var first = options.First();
                    _lblStatus.Text = $"Generated {options.Count} {first.underlying} options for {first.expiry:dd-MMM}";
                }
            });
        }

        // IWorkspacePersistence Implementation
        public void Restore(XDocument document, XElement element)
        {
            Logger.Debug("[MarketAnalyzerWindow] Restore: Called");
        }

        public void Save(XDocument document, XElement element)
        {
            Logger.Debug("[MarketAnalyzerWindow] Save: Called");
        }

        public WorkspaceOptions WorkspaceOptions { get; set; }
    }
}
