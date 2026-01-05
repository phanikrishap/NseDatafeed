using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Reactive.Linq;
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
using ZerodhaDatafeedAdapter.Services;
using ZerodhaDatafeedAdapter.Services.Analysis;
using ZerodhaDatafeedAdapter.Services.Instruments;
using ZerodhaDatafeedAdapter.AddOns.MarketAnalyzer;
using ZerodhaDatafeedAdapter.AddOns.TBSManager;
using ZerodhaDatafeedAdapter.AddOns.SimulationEngine;

namespace NinjaTrader.NinjaScript.AddOns
{
    /// <summary>
    /// NinjaTrader AddOn that integrates Index Watch, Option Chain, and TBS Manager into the Control Center menu
    /// and auto-launches windows on startup.
    /// </summary>
    public class MarketAnalyzerAddOn : AddOnBase
    {
        private NTMenuItem _menuItem;
        private NTMenuItem _tbsMenuItem;
        private NTMenuItem _simMenuItem;
        private NTMenuItem _existingNewMenu;
        private static bool _autoOpened = false;

        protected override void OnStateChange()
        {
            if (State == State.SetDefaults)
            {
                Description = "Zerodha Adapter AddOns";
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
                Logger.Info("[MarketAnalyzerAddOn] OnWindowCreated(): Auto-launching windows...");

                Core.Globals.RandomDispatcher.BeginInvoke(new Action(() =>
                {
                    try
                    {
                        // Open Index Watch window (formerly GIFT NIFTY Market Analyzer)
                        Logger.Debug("[MarketAnalyzerAddOn] OnWindowCreated(): Creating IndexWatch (MarketAnalyzerWindow) instance");
                        var win = new MarketAnalyzerWindow();
                        win.Show();
                        Logger.Info("[MarketAnalyzerAddOn] OnWindowCreated(): IndexWatch shown successfully");

                        // Open Option Chain window
                        Logger.Debug("[MarketAnalyzerAddOn] OnWindowCreated(): Creating OptionChainWindow instance");
                        var chainWin = new OptionChainWindow();
                        chainWin.Show();
                        Logger.Info("[MarketAnalyzerAddOn] OnWindowCreated(): OptionChainWindow shown successfully");

                        // Open TBS Manager window
                        Logger.Debug("[MarketAnalyzerAddOn] OnWindowCreated(): Creating TBSManagerWindow instance");
                        var tbsWin = new TBSManagerWindow();
                        tbsWin.Show();
                        Logger.Info("[MarketAnalyzerAddOn] OnWindowCreated(): TBSManagerWindow shown successfully");
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
                Header = "Index Watch",
                Style = Application.Current.TryFindResource("MainMenuItem") as Style
            };

            _menuItem.Click += OnMenuItemClick;
            _existingNewMenu.Items.Add(_menuItem);

            // Add TBS Manager menu item
            _tbsMenuItem = new NTMenuItem
            {
                Header = "TBS Manager",
                Style = Application.Current.TryFindResource("MainMenuItem") as Style
            };

            _tbsMenuItem.Click += OnTBSMenuItemClick;
            _existingNewMenu.Items.Add(_tbsMenuItem);

            // Add Simulation Engine menu item (does NOT auto-launch)
            _simMenuItem = new NTMenuItem
            {
                Header = "Simulation Engine",
                Style = Application.Current.TryFindResource("MainMenuItem") as Style
            };

            _simMenuItem.Click += OnSimMenuItemClick;
            _existingNewMenu.Items.Add(_simMenuItem);

            Logger.Info("[MarketAnalyzerAddOn] OnWindowCreated(): Menu items added successfully");
        }

        protected override void OnWindowDestroyed(Window window)
        {
            if (window is ControlCenter)
            {
                Logger.Info("[MarketAnalyzerAddOn] OnWindowDestroyed(): ControlCenter closing, cleaning up menu items");

                if (_existingNewMenu != null)
                {
                    if (_menuItem != null && _existingNewMenu.Items.Contains(_menuItem))
                    {
                        _existingNewMenu.Items.Remove(_menuItem);
                        _menuItem.Click -= OnMenuItemClick;
                        _menuItem = null;
                    }

                    if (_tbsMenuItem != null && _existingNewMenu.Items.Contains(_tbsMenuItem))
                    {
                        _existingNewMenu.Items.Remove(_tbsMenuItem);
                        _tbsMenuItem.Click -= OnTBSMenuItemClick;
                        _tbsMenuItem = null;
                    }

                    if (_simMenuItem != null && _existingNewMenu.Items.Contains(_simMenuItem))
                    {
                        _existingNewMenu.Items.Remove(_simMenuItem);
                        _simMenuItem.Click -= OnSimMenuItemClick;
                        _simMenuItem = null;
                    }
                }

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

        private void OnTBSMenuItemClick(object sender, RoutedEventArgs e)
        {
            Logger.Info("[MarketAnalyzerAddOn] OnTBSMenuItemClick(): User clicked TBS Manager menu item");

            Core.Globals.RandomDispatcher.BeginInvoke(new Action(() =>
            {
                try
                {
                    var win = new TBSManagerWindow();
                    win.Show();
                    Logger.Info("[MarketAnalyzerAddOn] OnTBSMenuItemClick(): New TBSManagerWindow opened");
                }
                catch (Exception ex)
                {
                    Logger.Error($"[MarketAnalyzerAddOn] OnTBSMenuItemClick(): Failed to create window - {ex.Message}", ex);
                }
            }));
        }

        private void OnSimMenuItemClick(object sender, RoutedEventArgs e)
        {
            Logger.Info("[MarketAnalyzerAddOn] OnSimMenuItemClick(): User clicked Simulation Engine menu item");

            Core.Globals.RandomDispatcher.BeginInvoke(new Action(() =>
            {
                try
                {
                    var win = new SimulationEngineWindow();
                    win.Show();
                    Logger.Info("[MarketAnalyzerAddOn] OnSimMenuItemClick(): New SimulationEngineWindow opened");
                }
                catch (Exception ex)
                {
                    Logger.Error($"[MarketAnalyzerAddOn] OnSimMenuItemClick(): Failed to create window - {ex.Message}", ex);
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
        public string PriorClose { get; set; } // Prior day close (static, populated once)
        public string Change { get; set; } // Chg% = (Last - PriorClose) / PriorClose
        public string ProjOpen { get; set; } // Projected Open based on GIFT NIFTY change from prior close
        public string Expiry { get; set; }
        public string LastUpdate { get; set; } // HH:mm:ss format
        public string Status { get; set; }
        public bool IsPositive { get; set; }
        public bool IsOption { get; set; }

        // Internal values for calculations
        public double LastValue { get; set; }
        public double PriorCloseValue { get; set; }
    }

    /// <summary>
    /// Dashboard window for Index Watch (formerly GIFT NIFTY Market Analyzer).
    /// Displays real-time prices in a NinjaTrader-style ListView with resizable columns.
    /// </summary>
    public class MarketAnalyzerWindow : NTWindow, IWorkspacePersistence
    {
        private ListView _listView;
        private GridView _gridView;
        private Button _btnRefresh;
        private TextBlock _lblStatus;
        private TextBlock _lblPriorDate;
        private ObservableCollection<AnalyzerRow> _rows;
        private DateTime _priorWorkingDay;
        private IDisposable _projectedOpenSubscription; // System.Reactive subscription for projected opens

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

            Caption = "Index Watch";
            Width = 720;
            Height = 400;

            try
            {
                Logger.Debug("[MarketAnalyzerWindow] Constructor: Building NinjaTrader-style UI");

                // Compute prior working day from holiday calendar
                _priorWorkingDay = HolidayCalendarService.Instance.GetPriorWorkingDay();
                Logger.Info($"[MarketAnalyzerWindow] Prior working day: {_priorWorkingDay:dd-MMM-yyyy}");

                _rows = new ObservableCollection<AnalyzerRow>();

                // Initialize with index rows (includes NIFTY_I for NIFTY Futures)
                _rows.Add(new AnalyzerRow { Symbol = "GIFT NIFTY", Last = "---", PriorClose = "---", Change = "---", ProjOpen = "N/A", Expiry = "---", Status = "Pending", IsPositive = true, IsOption = false });
                _rows.Add(new AnalyzerRow { Symbol = "NIFTY 50", Last = "---", PriorClose = "---", Change = "---", ProjOpen = "---", Expiry = "---", Status = "Pending", IsPositive = true, IsOption = false });
                _rows.Add(new AnalyzerRow { Symbol = "SENSEX", Last = "---", PriorClose = "---", Change = "---", ProjOpen = "---", Expiry = "---", Status = "Pending", IsPositive = true, IsOption = false });

                // NIFTY_I row - internal symbol will be resolved dynamically (e.g., NIFTY26JANFUT, NIFTY26MAYFUT)
                var logic = MarketAnalyzerLogic.Instance;
                string niftyFutInternal = !string.IsNullOrEmpty(logic.NiftyFuturesSymbol) ? logic.NiftyFuturesSymbol : "NIFTY_I";
                string niftyFutExpiry = logic.NiftyFuturesExpiry != default ? logic.NiftyFuturesExpiry.ToString("dd-MMM") : "---";
                _rows.Add(new AnalyzerRow { Symbol = "NIFTY_I", InternalSymbol = niftyFutInternal, Last = "---", PriorClose = "---", Change = "---", ProjOpen = "N/A", Expiry = niftyFutExpiry, Status = "Pending", IsPositive = true, IsOption = false });

                var dockPanel = new DockPanel { Background = _bgColor };
                Content = dockPanel;

                // Toolbar with Prior Date header
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

                // Subscribe to System.Reactive stream for projected opens (event-driven, fires ONCE)
                // Using Dispatcher.InvokeAsync inside the handler instead of ObserveOn to avoid WinForms reference
                _projectedOpenSubscription = MarketAnalyzerLogic.Instance.ProjectedOpenStream
                    .Subscribe(update => Dispatcher.InvokeAsync(() => OnProjectedOpenCalculated(update)));

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
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 0, 20, 0)
            };
            panel.Children.Add(title);

            // Prior Date label (static, computed from holiday calendar)
            var priorDateLabel = new TextBlock
            {
                Text = "Prior Date:",
                FontFamily = _ntFont,
                FontSize = 11,
                Foreground = new SolidColorBrush(Color.FromRgb(150, 150, 150)),
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 0, 5, 0)
            };
            panel.Children.Add(priorDateLabel);

            _lblPriorDate = new TextBlock
            {
                Text = _priorWorkingDay.ToString("dd-MMM-yyyy (ddd)"),
                FontFamily = _ntFont,
                FontSize = 11,
                FontWeight = FontWeights.SemiBold,
                Foreground = new SolidColorBrush(Color.FromRgb(100, 180, 255)),
                VerticalAlignment = VerticalAlignment.Center
            };
            panel.Children.Add(_lblPriorDate);

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
                Width = 100,
                DisplayMemberBinding = new Binding("Symbol")
            };
            _gridView.Columns.Add(symbolColumn);

            // Last column
            var lastColumn = new GridViewColumn
            {
                Header = "Last",
                Width = 85,
                DisplayMemberBinding = new Binding("Last")
            };
            _gridView.Columns.Add(lastColumn);

            // Prior Close column (static, populated once)
            var priorCloseColumn = new GridViewColumn
            {
                Header = "Prior Close",
                Width = 85,
                DisplayMemberBinding = new Binding("PriorClose")
            };
            _gridView.Columns.Add(priorCloseColumn);

            // Change% column - (Last - PriorClose) / PriorClose
            var changeColumn = new GridViewColumn
            {
                Header = "Chg%",
                Width = 70
            };
            var changeTemplate = new DataTemplate();
            var changeFactory = new FrameworkElementFactory(typeof(TextBlock));
            changeFactory.SetBinding(TextBlock.TextProperty, new Binding("Change"));
            changeFactory.SetValue(TextBlock.HorizontalAlignmentProperty, HorizontalAlignment.Right);
            changeFactory.SetValue(TextBlock.PaddingProperty, new Thickness(0, 0, 5, 0));
            changeTemplate.VisualTree = changeFactory;
            changeColumn.CellTemplate = changeTemplate;
            _gridView.Columns.Add(changeColumn);

            // Proj Open column - based on GIFT NIFTY change from prior close
            var projColumn = new GridViewColumn
            {
                Header = "Proj Open",
                Width = 85,
                DisplayMemberBinding = new Binding("ProjOpen")
            };
            _gridView.Columns.Add(projColumn);

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

            // Dispose Rx subscription
            _projectedOpenSubscription?.Dispose();
            _projectedOpenSubscription = null;

            Logger.Info("[MarketAnalyzerWindow] OnWindowUnloaded: Cleanup complete");
        }

        /// <summary>
        /// System.Reactive handler for projected open calculations.
        /// Called ONCE when all required data is available (GIFT change% + NIFTY/SENSEX prior close).
        /// </summary>
        private void OnProjectedOpenCalculated(ProjectedOpenUpdate update)
        {
            try
            {
                Logger.Info($"[MarketAnalyzerWindow] Rx: Projected Opens received - GIFT Chg%: {update.GiftChangePercent:+0.00;-0.00}%, NIFTY: {update.NiftyProjectedOpen:F0}, SENSEX: {update.SensexProjectedOpen:F0}");

                var niftyRow = _rows.FirstOrDefault(r => r.Symbol == "NIFTY 50");
                var sensexRow = _rows.FirstOrDefault(r => r.Symbol == "SENSEX");

                if (niftyRow != null && niftyRow.ProjOpen == "---")
                {
                    niftyRow.ProjOpen = update.NiftyProjectedOpen.ToString("F0");
                }

                if (sensexRow != null && sensexRow.ProjOpen == "---")
                {
                    sensexRow.ProjOpen = update.SensexProjectedOpen.ToString("F0");
                }

                _listView.Items.Refresh();
            }
            catch (Exception ex)
            {
                Logger.Error($"[MarketAnalyzerWindow] OnProjectedOpenCalculated: Exception - {ex.Message}", ex);
            }
        }

        private void OnTickerUpdated(string symbol)
        {
            Dispatcher.InvokeAsync(() =>
            {
                try
                {
                    var logic = MarketAnalyzerLogic.Instance;
                    AnalyzerRow row = null;
                    AnalyzerRow giftRow = _rows.FirstOrDefault(r => r.Symbol == "GIFT NIFTY");

                    if (symbol == "GIFT NIFTY")
                    {
                        var ticker = logic.GiftNiftyTicker;
                        if (ticker == null) return;
                        row = giftRow;
                        if (row != null)
                        {
                            row.LastValue = ticker.CurrentPrice;
                            row.Last = ticker.LastPriceDisplay;
                            row.LastUpdate = ticker.LastUpdateTimeDisplay;

                            // GIFT NIFTY: Use Chg% directly from API (NetChangePercent computed from LastClose callback)
                            // Don't need Prior Close column for GIFT NIFTY - the API provides the change directly
                            if (ticker.NetChangePercent != 0)
                            {
                                row.Change = ticker.NetChangePercentDisplay;
                                row.IsPositive = ticker.IsPositive;
                                // Also set PriorClose for display if Close is available
                                if (ticker.Close > 0)
                                {
                                    row.PriorCloseValue = ticker.Close;
                                    row.PriorClose = ticker.Close.ToString("F2");
                                }
                                // Try to calculate projected opens when we have GIFT NIFTY change
                                TryCalculateProjectedOpensOnce();
                            }
                        }
                    }
                    else if (symbol == "NIFTY" || symbol == "NIFTY 50")
                    {
                        var ticker = logic.NiftyTicker;
                        if (ticker == null) return;
                        row = _rows.FirstOrDefault(r => r.Symbol == "NIFTY 50");
                        if (row != null)
                        {
                            row.LastValue = ticker.CurrentPrice;
                            row.Last = ticker.LastPriceDisplay;
                            row.LastUpdate = ticker.LastUpdateTimeDisplay;

                            // Set Prior Close from ticker.Close (once populated)
                            if (ticker.Close > 0 && row.PriorCloseValue == 0)
                            {
                                row.PriorCloseValue = ticker.Close;
                                row.PriorClose = ticker.Close.ToString("F2");
                                // Try to calculate projected opens once when prior close is set
                                TryCalculateProjectedOpensOnce();
                            }

                            // Calculate Chg% from Prior Close
                            if (row.PriorCloseValue > 0)
                            {
                                double chgPercent = (row.LastValue - row.PriorCloseValue) / row.PriorCloseValue * 100;
                                row.Change = $"{chgPercent:+0.00;-0.00;0.00}%";
                                row.IsPositive = chgPercent >= 0;
                            }
                        }
                    }
                    else if (symbol == "SENSEX")
                    {
                        var ticker = logic.SensexTicker;
                        if (ticker == null) return;
                        row = _rows.FirstOrDefault(r => r.Symbol == "SENSEX");
                        if (row != null)
                        {
                            row.LastValue = ticker.CurrentPrice;
                            row.Last = ticker.LastPriceDisplay;
                            row.LastUpdate = ticker.LastUpdateTimeDisplay;

                            // Set Prior Close from ticker.Close (once populated)
                            if (ticker.Close > 0 && row.PriorCloseValue == 0)
                            {
                                row.PriorCloseValue = ticker.Close;
                                row.PriorClose = ticker.Close.ToString("F2");
                                // Try to calculate projected opens once when prior close is set
                                TryCalculateProjectedOpensOnce();
                            }

                            // Calculate Chg% from Prior Close
                            if (row.PriorCloseValue > 0)
                            {
                                double chgPercent = (row.LastValue - row.PriorCloseValue) / row.PriorCloseValue * 100;
                                row.Change = $"{chgPercent:+0.00;-0.00;0.00}%";
                                row.IsPositive = chgPercent >= 0;
                            }
                        }
                    }
                    else if (symbol == "NIFTY_I")
                    {
                        var ticker = logic.NiftyFuturesTicker;
                        if (ticker == null) return;
                        row = _rows.FirstOrDefault(r => r.Symbol == "NIFTY_I");
                        if (row != null)
                        {
                            row.LastValue = ticker.CurrentPrice;
                            row.Last = ticker.LastPriceDisplay;
                            row.LastUpdate = ticker.LastUpdateTimeDisplay;

                            // Update internal symbol if it was resolved
                            if (!string.IsNullOrEmpty(logic.NiftyFuturesSymbol))
                            {
                                row.InternalSymbol = logic.NiftyFuturesSymbol;
                                if (logic.NiftyFuturesExpiry != default)
                                {
                                    row.Expiry = logic.NiftyFuturesExpiry.ToString("dd-MMM");
                                }
                            }

                            // Set Prior Close from ticker.Close (once populated)
                            if (ticker.Close > 0 && row.PriorCloseValue == 0)
                            {
                                row.PriorCloseValue = ticker.Close;
                                row.PriorClose = ticker.Close.ToString("F2");
                            }

                            // Calculate Chg% from Prior Close
                            if (row.PriorCloseValue > 0)
                            {
                                double chgPercent = (row.LastValue - row.PriorCloseValue) / row.PriorCloseValue * 100;
                                row.Change = $"{chgPercent:+0.00;-0.00;0.00}%";
                                row.IsPositive = chgPercent >= 0;
                            }
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

        /// <summary>
        /// Calculates projected opens when data is available.
        /// Projected Open = Prior Close × (1 + GIFT NIFTY Change%)
        /// GIFT NIFTY Change% comes from the API via NetChangePercent.
        /// Retries on each tick until both projected opens are populated.
        /// </summary>
        private void TryCalculateProjectedOpensOnce()
        {
            var logic = MarketAnalyzerLogic.Instance;
            var giftTicker = logic.GiftNiftyTicker;
            var niftyRow = _rows.FirstOrDefault(r => r.Symbol == "NIFTY 50");
            var sensexRow = _rows.FirstOrDefault(r => r.Symbol == "SENSEX");

            // Need GIFT NIFTY change% from API
            if (giftTicker == null || giftTicker.NetChangePercent == 0) return;

            // GIFT NIFTY NetChangePercent is already in percentage form (e.g., 0.09 means 0.09%)
            // Convert to decimal multiplier
            double giftChgDecimal = giftTicker.NetChangePercent / 100.0;

            bool updated = false;

            // Calculate NIFTY 50 projected open: PriorClose × (1 + GIFT change%)
            if (niftyRow != null && niftyRow.PriorCloseValue > 0 && niftyRow.ProjOpen == "---")
            {
                double projOpen = niftyRow.PriorCloseValue * (1 + giftChgDecimal);
                niftyRow.ProjOpen = projOpen.ToString("F0");
                updated = true;
            }

            // Calculate SENSEX projected open: PriorClose × (1 + GIFT change%)
            if (sensexRow != null && sensexRow.PriorCloseValue > 0 && sensexRow.ProjOpen == "---")
            {
                double projOpen = sensexRow.PriorCloseValue * (1 + giftChgDecimal);
                sensexRow.ProjOpen = projOpen.ToString("F0");
                updated = true;
            }

            if (updated)
            {
                Logger.Info($"[MarketAnalyzerWindow] Projected Opens calculated - GIFT Chg%: {giftTicker.NetChangePercent:+0.00;-0.00}%, NIFTY: {niftyRow?.ProjOpen}, SENSEX: {sensexRow?.ProjOpen}");
            }
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
                    else if (symbol.Equals("NIFTY_I", StringComparison.OrdinalIgnoreCase))
                    {
                        row = _rows.FirstOrDefault(r => r.Symbol == "NIFTY_I");
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
            // Status updates from logic are logged but NOT shown in status bar
            // The status bar shows only "Last update: HH:mm:ss"
            Logger.Debug($"[MarketAnalyzerWindow] OnStatusUpdated: {msg}");
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
