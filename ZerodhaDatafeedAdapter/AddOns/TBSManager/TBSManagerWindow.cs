using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using System.Xml.Linq;
using NinjaTrader.Cbi;
using NinjaTrader.Gui.Tools;
using ZerodhaDatafeedAdapter.Models;
using ZerodhaDatafeedAdapter.Services;
using ZerodhaDatafeedAdapter.Services.Analysis;
using ZerodhaDatafeedAdapter.AddOns.MarketAnalyzer;
using SimService = ZerodhaDatafeedAdapter.Services.SimulationService;

namespace ZerodhaDatafeedAdapter.AddOns.TBSManager
{
    #region Value Converters

    /// <summary>
    /// Converts P&L value to color (green for profit, red for loss)
    /// </summary>
    public class PnLToColorConverter : IValueConverter
    {
        private static readonly SolidColorBrush GreenBrush = new SolidColorBrush(Color.FromRgb(100, 200, 100));
        private static readonly SolidColorBrush RedBrush = new SolidColorBrush(Color.FromRgb(220, 80, 80));
        private static readonly SolidColorBrush NeutralBrush = new SolidColorBrush(Color.FromRgb(212, 212, 212));

        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            if (value is decimal pnl)
            {
                if (pnl > 0) return GreenBrush;
                if (pnl < 0) return RedBrush;
            }
            return NeutralBrush;
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        {
            throw new NotImplementedException();
        }
    }

    /// <summary>
    /// Converts execution status to color
    /// </summary>
    public class StatusToColorConverter : IValueConverter
    {
        private static readonly SolidColorBrush SkippedBrush = new SolidColorBrush(Color.FromRgb(120, 120, 120));   // Gray
        private static readonly SolidColorBrush IdleBrush = new SolidColorBrush(Color.FromRgb(150, 150, 150));      // Light Gray
        private static readonly SolidColorBrush MonitoringBrush = new SolidColorBrush(Color.FromRgb(200, 180, 80)); // Yellow/Amber
        private static readonly SolidColorBrush LiveBrush = new SolidColorBrush(Color.FromRgb(100, 200, 100));      // Green
        private static readonly SolidColorBrush SquaredOffBrush = new SolidColorBrush(Color.FromRgb(100, 150, 220));// Blue

        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            if (value is TBSExecutionStatus status)
            {
                switch (status)
                {
                    case TBSExecutionStatus.Skipped: return SkippedBrush;
                    case TBSExecutionStatus.Idle: return IdleBrush;
                    case TBSExecutionStatus.Monitoring: return MonitoringBrush;
                    case TBSExecutionStatus.Live: return LiveBrush;
                    case TBSExecutionStatus.SquaredOff: return SquaredOffBrush;
                }
            }
            return IdleBrush;
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        {
            throw new NotImplementedException();
        }
    }

    /// <summary>
    /// Converts SL status to color (SAFE=green, WARNING=yellow, HIT=red)
    /// </summary>
    public class SLStatusToColorConverter : IValueConverter
    {
        private static readonly SolidColorBrush SafeBrush = new SolidColorBrush(Color.FromRgb(100, 200, 100));
        private static readonly SolidColorBrush WarningBrush = new SolidColorBrush(Color.FromRgb(220, 180, 50));
        private static readonly SolidColorBrush HitBrush = new SolidColorBrush(Color.FromRgb(220, 80, 80));
        private static readonly SolidColorBrush NeutralBrush = new SolidColorBrush(Color.FromRgb(150, 150, 150));

        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            if (value is string status)
            {
                switch (status.ToUpper())
                {
                    case "SAFE": return SafeBrush;
                    case "WARNING": return WarningBrush;
                    case "HIT": return HitBrush;
                }
            }
            return NeutralBrush;
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        {
            throw new NotImplementedException();
        }
    }

    #endregion

    /// <summary>
    /// TBS Manager Window - hosts the TBSManagerTabPage
    /// Main NTWindow containing TabControl with Config and Execution tabs
    /// </summary>
    public class TBSManagerWindow : NTWindow, IWorkspacePersistence
    {
        public TBSManagerWindow()
        {
            Logger.Info("[TBSManagerWindow] Constructor: Creating window");

            Caption = "TBS Manager";
            Width = 900;
            Height = 700;

            // Create the tab control
            TabControl tabControl = new TabControl();
            tabControl.Style = Application.Current.TryFindResource("TabControlStyle") as Style;

            // Create and add our tab page
            TBSManagerTabPage tabPage = new TBSManagerTabPage();
            tabControl.Items.Add(tabPage);

            Content = tabControl;

            Logger.Info("[TBSManagerWindow] Constructor: Window created");
        }

        public void Restore(XDocument document, XElement element)
        {
            Logger.Debug("[TBSManagerWindow] Restore: Called");
        }

        public void Save(XDocument document, XElement element)
        {
            Logger.Debug("[TBSManagerWindow] Save: Called");
        }

        public WorkspaceOptions WorkspaceOptions { get; set; }
    }

    /// <summary>
    /// TBS Manager Tab Page with Config and Execution sub-tabs
    /// </summary>
    public class TBSManagerTabPage : NTTabPage, IInstrumentProvider
    {
        // UI Elements
        private TabControl _innerTabControl;
        private TabItem _configTab;
        private TabItem _executionTab;

        // Config Tab Elements
        private ListView _configListView;
        private ComboBox _cboUnderlying;
        private TextBox _txtDTE;
        private Button _btnRefresh;
        private TextBlock _lblConfigStatus;
        private ObservableCollection<TBSConfigEntry> _configRows;

        // Execution Tab Elements
        private ScrollViewer _executionScrollViewer;
        private StackPanel _executionPanel;
        private TextBlock _lblTotalPnL;
        private TextBlock _lblLiveCount;
        private TextBlock _lblMonitoringCount;
        private TextBlock _lblExecutionStatus;
        private ObservableCollection<TBSExecutionState> _executionRows;

        // State
        private Instrument _instrument;
        private DispatcherTimer _statusTimer;
        private DispatcherTimer _optionChainDelayTimer;
        private string _selectedUnderlying;
        private DateTime? _selectedExpiry;
        private bool _optionChainReady = false;
        private int _delayCountdown = 45; // 45 second delay after Option Chain loads
        private const int OPTION_CHAIN_DELAY_SECONDS = 45;

        // NOTE: Local price cache removed - using centralized PriceHub in MarketAnalyzerLogic
        // This avoids duplicate WebSocket subscriptions and ensures all consumers see same prices

        // NinjaTrader-style colors
        private static readonly SolidColorBrush _bgColor = new SolidColorBrush(Color.FromRgb(27, 27, 28));
        private static readonly SolidColorBrush _fgColor = new SolidColorBrush(Color.FromRgb(212, 212, 212));
        private static readonly SolidColorBrush _headerBg = new SolidColorBrush(Color.FromRgb(37, 37, 38));
        private static readonly SolidColorBrush _borderColor = new SolidColorBrush(Color.FromRgb(51, 51, 51));
        private static readonly SolidColorBrush _rowAltBg = new SolidColorBrush(Color.FromRgb(32, 32, 33));
        private static readonly SolidColorBrush _expanderBg = new SolidColorBrush(Color.FromRgb(40, 40, 42));
        private static readonly FontFamily _ntFont = new FontFamily("Segoe UI");

        // Status colors
        private static readonly SolidColorBrush _skippedColor = new SolidColorBrush(Color.FromRgb(120, 120, 120));
        private static readonly SolidColorBrush _idleColor = new SolidColorBrush(Color.FromRgb(150, 150, 150));
        private static readonly SolidColorBrush _monitoringColor = new SolidColorBrush(Color.FromRgb(200, 180, 80));
        private static readonly SolidColorBrush _liveColor = new SolidColorBrush(Color.FromRgb(100, 200, 100));
        private static readonly SolidColorBrush _squaredOffColor = new SolidColorBrush(Color.FromRgb(100, 150, 220));

        public TBSManagerTabPage()
        {
            Logger.Info("[TBSManagerTabPage] Constructor: Initializing");

            _configRows = new ObservableCollection<TBSConfigEntry>();
            _executionRows = new ObservableCollection<TBSExecutionState>();

            BuildUI();
            LoadConfigurations();
            SubscribeToEvents();
            StartStatusTimer();

            Logger.Info("[TBSManagerTabPage] Constructor: Completed");
        }

        private void BuildUI()
        {
            // Main grid
            var mainGrid = new Grid
            {
                Background = _bgColor
            };

            // Inner TabControl for Config and Execution tabs
            _innerTabControl = new TabControl
            {
                Background = _bgColor,
                Foreground = _fgColor,
                FontFamily = _ntFont,
                FontSize = 12,
                Margin = new Thickness(5)
            };

            // Build Config Tab
            _configTab = new TabItem
            {
                Header = "Configuration",
                Foreground = _fgColor
            };
            _configTab.Content = BuildConfigTabContent();
            _innerTabControl.Items.Add(_configTab);

            // Build Execution Tab
            _executionTab = new TabItem
            {
                Header = "Execution",
                Foreground = _fgColor
            };
            _executionTab.Content = BuildExecutionTabContent();
            _innerTabControl.Items.Add(_executionTab);

            mainGrid.Children.Add(_innerTabControl);
            Content = mainGrid;
        }

        #region Config Tab

        private Grid BuildConfigTabContent()
        {
            var grid = new Grid();
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto }); // Filter row
            grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) }); // ListView
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto }); // Status row

            // Filter panel
            var filterPanel = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Margin = new Thickness(5),
                Background = _headerBg
            };

            filterPanel.Children.Add(new TextBlock
            {
                Text = "Underlying:",
                Foreground = _fgColor,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(5)
            });

            _cboUnderlying = new ComboBox
            {
                Width = 120,
                Margin = new Thickness(5),
                IsEditable = false
            };
            _cboUnderlying.Items.Add("All");
            _cboUnderlying.SelectedIndex = 0;
            _cboUnderlying.SelectionChanged += (s, e) => ApplyFilter();
            filterPanel.Children.Add(_cboUnderlying);

            filterPanel.Children.Add(new TextBlock
            {
                Text = "DTE:",
                Foreground = _fgColor,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(15, 5, 5, 5)
            });

            _txtDTE = new TextBox
            {
                Width = 60,
                Margin = new Thickness(5),
                Text = ""
            };
            _txtDTE.TextChanged += (s, e) => ApplyFilter();
            filterPanel.Children.Add(_txtDTE);

            _btnRefresh = new Button
            {
                Content = "Refresh",
                Margin = new Thickness(15, 5, 5, 5),
                Padding = new Thickness(10, 3, 10, 3)
            };
            _btnRefresh.Click += (s, e) => LoadConfigurations(forceReload: true);
            filterPanel.Children.Add(_btnRefresh);

            Grid.SetRow(filterPanel, 0);
            grid.Children.Add(filterPanel);

            // Config ListView
            _configListView = new ListView
            {
                Background = _bgColor,
                Foreground = _fgColor,
                BorderBrush = _borderColor,
                BorderThickness = new Thickness(1),
                Margin = new Thickness(5),
                ItemsSource = _configRows
            };

            var configGridView = new GridView();
            configGridView.Columns.Add(CreateColumn("Underlying", "Underlying", 100));
            configGridView.Columns.Add(CreateColumn("DTE", "DTE", 50));
            configGridView.Columns.Add(CreateColumn("Entry Time", "EntryTime", 90, @"{0:hh\:mm\:ss}"));
            configGridView.Columns.Add(CreateColumn("Exit Time", "ExitTime", 90, @"{0:hh\:mm\:ss}"));
            configGridView.Columns.Add(CreateColumn("Ind SL", "IndividualSL", 70, "{0:P0}"));
            configGridView.Columns.Add(CreateColumn("Comb SL", "CombinedSL", 70, "{0:P0}"));
            configGridView.Columns.Add(CreateColumn("Action", "HedgeAction", 100));
            configGridView.Columns.Add(CreateColumn("Qty", "Quantity", 50));
            configGridView.Columns.Add(CreateColumn("Active", "IsActive", 60));

            _configListView.View = configGridView;
            Grid.SetRow(_configListView, 1);
            grid.Children.Add(_configListView);

            // Status bar
            _lblConfigStatus = new TextBlock
            {
                Foreground = _fgColor,
                Margin = new Thickness(5),
                Text = "Ready"
            };
            Grid.SetRow(_lblConfigStatus, 2);
            grid.Children.Add(_lblConfigStatus);

            return grid;
        }

        private void LoadConfigurations(bool forceReload = false)
        {
            try
            {
                _lblConfigStatus.Text = "Loading configurations...";

                var configs = TBSConfigurationService.Instance.LoadConfigurations(forceReload);

                // Populate underlying dropdown
                var underlyings = configs.Select(c => c.Underlying).Distinct().OrderBy(u => u).ToList();
                _cboUnderlying.Items.Clear();
                _cboUnderlying.Items.Add("All");
                foreach (var u in underlyings)
                    _cboUnderlying.Items.Add(u);
                _cboUnderlying.SelectedIndex = 0;

                // Apply filter and display
                ApplyFilter();

                _lblConfigStatus.Text = $"Loaded {configs.Count} configurations from {TBSConfigurationService.Instance.ConfigFilePath}";
            }
            catch (Exception ex)
            {
                _lblConfigStatus.Text = $"Error: {ex.Message}";
                Logger.Error($"[TBSManagerTabPage] LoadConfigurations error: {ex.Message}", ex);
            }
        }

        private void ApplyFilter()
        {
            try
            {
                string underlying = _cboUnderlying.SelectedItem?.ToString();
                int? dte = null;

                if (int.TryParse(_txtDTE.Text, out int parsedDte))
                    dte = parsedDte;

                var filtered = TBSConfigurationService.Instance.GetConfigurations(
                    underlying == "All" ? null : underlying,
                    dte);

                _configRows.Clear();
                foreach (var config in filtered)
                    _configRows.Add(config);

                // Only initialize execution states if Option Chain is ready (delay completed)
                if (_optionChainReady)
                {
                    InitializeExecutionStates();
                }
                else
                {
                    // Show waiting message in execution tab
                    ShowWaitingForOptionChain();
                }

                _lblConfigStatus.Text = $"Showing {_configRows.Count} configurations";
            }
            catch (Exception ex)
            {
                Logger.Error($"[TBSManagerTabPage] ApplyFilter error: {ex.Message}");
            }
        }

        private void ShowWaitingForOptionChain()
        {
            _executionPanel.Children.Clear();
            _executionRows.Clear();

            var waitingMessage = new TextBlock
            {
                Text = _optionChainDelayTimer != null
                    ? $"Waiting for Option Chain to load... ({_delayCountdown}s remaining)"
                    : "Waiting for Option Chain window to open...",
                Foreground = _monitoringColor,
                FontSize = 14,
                FontWeight = FontWeights.SemiBold,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(20)
            };

            _executionPanel.Children.Add(waitingMessage);
            _lblExecutionStatus.Text = "Waiting for Option Chain to complete loading";
        }

        #endregion

        #region Execution Tab

        private Grid BuildExecutionTabContent()
        {
            var grid = new Grid();
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto }); // Summary row
            grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) }); // Scrollable content
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto }); // Status row

            // Summary panel
            var summaryPanel = new Border
            {
                Background = _headerBg,
                Margin = new Thickness(5),
                Padding = new Thickness(10, 8, 10, 8)
            };

            var summaryStack = new StackPanel { Orientation = Orientation.Horizontal };

            summaryStack.Children.Add(new TextBlock
            {
                Text = "Total P&L:",
                Foreground = _fgColor,
                VerticalAlignment = VerticalAlignment.Center,
                FontWeight = FontWeights.Bold,
                Margin = new Thickness(0, 0, 5, 0)
            });

            _lblTotalPnL = new TextBlock
            {
                Text = "0.00",
                Foreground = _fgColor,
                VerticalAlignment = VerticalAlignment.Center,
                FontWeight = FontWeights.Bold,
                MinWidth = 80
            };
            summaryStack.Children.Add(_lblTotalPnL);

            summaryStack.Children.Add(new TextBlock
            {
                Text = "Live:",
                Foreground = _liveColor,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(30, 0, 5, 0)
            });

            _lblLiveCount = new TextBlock
            {
                Text = "0",
                Foreground = _liveColor,
                VerticalAlignment = VerticalAlignment.Center,
                FontWeight = FontWeights.Bold,
                MinWidth = 30
            };
            summaryStack.Children.Add(_lblLiveCount);

            summaryStack.Children.Add(new TextBlock
            {
                Text = "Monitoring:",
                Foreground = _monitoringColor,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(20, 0, 5, 0)
            });

            _lblMonitoringCount = new TextBlock
            {
                Text = "0",
                Foreground = _monitoringColor,
                VerticalAlignment = VerticalAlignment.Center,
                FontWeight = FontWeights.Bold,
                MinWidth = 30
            };
            summaryStack.Children.Add(_lblMonitoringCount);

            summaryPanel.Child = summaryStack;
            Grid.SetRow(summaryPanel, 0);
            grid.Children.Add(summaryPanel);

            // Scrollable execution panel
            _executionScrollViewer = new ScrollViewer
            {
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
                Margin = new Thickness(5)
            };

            _executionPanel = new StackPanel
            {
                Background = _bgColor
            };

            _executionScrollViewer.Content = _executionPanel;
            Grid.SetRow(_executionScrollViewer, 1);
            grid.Children.Add(_executionScrollViewer);

            // Status bar
            _lblExecutionStatus = new TextBlock
            {
                Foreground = _fgColor,
                Margin = new Thickness(5),
                Text = "Ready"
            };
            Grid.SetRow(_lblExecutionStatus, 2);
            grid.Children.Add(_lblExecutionStatus);

            return grid;
        }

        private void InitializeExecutionStates()
        {
            _executionRows.Clear();

            // Get configs that match current filter
            string underlying = _cboUnderlying.SelectedItem?.ToString();
            int? dte = null;
            if (int.TryParse(_txtDTE.Text, out int parsedDte))
                dte = parsedDte;

            var configs = TBSConfigurationService.Instance.GetConfigurations(
                underlying == "All" ? null : underlying,
                dte);

            // ALWAYS use real system time for TBS status logic
            // Simulation only affects price data replay, not time-based execution
            var isSimulationActive = SimService.Instance.IsSimulationActive;
            var now = DateTime.Now.TimeOfDay;

            foreach (var config in configs)
            {
                // Get lot size for the underlying
                int lotSize = GetLotSizeForUnderlying(config.Underlying);

                var state = new TBSExecutionState
                {
                    Config = config,
                    // Cache config values in state for later use
                    LotSize = lotSize,
                    Quantity = config.Quantity,
                    IndividualSLPercent = config.IndividualSL,
                    CombinedSLPercent = config.CombinedSL,
                    ConfigExitTime = config.ExitTime,
                    HedgeAction = config.HedgeAction ?? "exit_both"
                };

                // Initialize legs with lot size and quantity
                state.Legs.Add(new TBSLegState
                {
                    OptionType = "CE",
                    Quantity = config.Quantity,
                    LotSize = lotSize
                });
                state.Legs.Add(new TBSLegState
                {
                    OptionType = "PE",
                    Quantity = config.Quantity,
                    LotSize = lotSize
                });

                // Always populate ATM strike for all tranches (so they're ready)
                UpdateStrikeForState(state);

                // Update initial status based on time (skip exit time check during simulation)
                state.UpdateStatusBasedOnTime(now, skipExitTimeCheck: isSimulationActive);

                // For tranches that are already Live or past entry (but before exit),
                // lock strike and populate entry prices immediately
                if (state.Status == TBSExecutionStatus.Live && !state.StrikeLocked)
                {
                    Logger.Info($"[TBSManager] {config.EntryTime:hh\\:mm\\:ss} Initialized as Live - locking strike immediately");
                    LockStrikeAndEnter(state);
                }

                // For tranches that are SquaredOff (past exit time), mark as historical
                if (state.Status == TBSExecutionStatus.SquaredOff)
                {
                    // These missed both entry and exit - they're historical
                    state.Message = "Missed (initialized after exit time)";
                }

                _executionRows.Add(state);
            }

            RebuildExecutionUI();
            UpdateSummary();
            _lblExecutionStatus.Text = $"Loaded {_executionRows.Count} tranches";
        }

        /// <summary>
        /// Get lot size for an underlying index
        /// </summary>
        private int GetLotSizeForUnderlying(string underlying)
        {
            if (string.IsNullOrEmpty(underlying)) return 1;

            var upper = underlying.ToUpperInvariant();
            switch (upper)
            {
                case "NIFTY":
                    return 25;  // NIFTY lot size as of 2024
                case "BANKNIFTY":
                    return 15;  // BANKNIFTY lot size as of 2024
                case "SENSEX":
                    return 10;  // SENSEX lot size
                case "FINNIFTY":
                    return 25;  // FINNIFTY lot size
                case "MIDCPNIFTY":
                    return 50;  // MIDCPNIFTY lot size
                default:
                    return 25;  // Default to NIFTY lot size
            }
        }

        private void RebuildExecutionUI()
        {
            _executionPanel.Children.Clear();

            bool isAlt = false;
            foreach (var state in _executionRows.OrderBy(s => s.Config?.EntryTime ?? TimeSpan.Zero))
            {
                var trancheUI = CreateTrancheUI(state, isAlt);
                _executionPanel.Children.Add(trancheUI);
                isAlt = !isAlt;
            }
        }

        private Border CreateTrancheUI(TBSExecutionState state, bool isAlt)
        {
            var border = new Border
            {
                Background = isAlt ? _rowAltBg : _bgColor,
                BorderBrush = _borderColor,
                BorderThickness = new Thickness(0, 0, 0, 1),
                Padding = new Thickness(10, 6, 10, 6),
                Tag = state,
                DataContext = state  // Set DataContext for bindings
            };

            var mainStack = new StackPanel();

            // Headline row: Entry Time | Status | Combined P&L
            var headlineGrid = new Grid();
            headlineGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(120) }); // Entry Time
            headlineGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(100) }); // Status
            headlineGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) }); // Message
            headlineGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(120) }); // P&L

            // Entry Time (static - doesn't change)
            var entryTimePanel = new StackPanel { Orientation = Orientation.Horizontal };
            entryTimePanel.Children.Add(new TextBlock
            {
                Text = state.EntryTimeDisplay,
                Foreground = _fgColor,
                FontSize = 14,
                FontWeight = FontWeights.Bold,
                VerticalAlignment = VerticalAlignment.Center
            });
            Grid.SetColumn(entryTimePanel, 0);
            headlineGrid.Children.Add(entryTimePanel);

            // Status with color - bound to StatusText property
            var statusText = new TextBlock
            {
                FontSize = 12,
                FontWeight = FontWeights.SemiBold,
                VerticalAlignment = VerticalAlignment.Center
            };
            statusText.SetBinding(TextBlock.TextProperty, new Binding("StatusText"));
            statusText.SetBinding(TextBlock.ForegroundProperty, new Binding("Status") { Converter = new StatusToColorConverter() });
            Grid.SetColumn(statusText, 1);
            headlineGrid.Children.Add(statusText);

            // Message - bound to Message property
            var messageText = new TextBlock
            {
                Foreground = new SolidColorBrush(Color.FromRgb(180, 180, 180)),
                FontSize = 11,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(10, 0, 0, 0)
            };
            messageText.SetBinding(TextBlock.TextProperty, new Binding("Message"));
            Grid.SetColumn(messageText, 2);
            headlineGrid.Children.Add(messageText);

            // Combined P&L - bound to CombinedPnL property
            var pnlText = new TextBlock
            {
                FontSize = 14,
                FontWeight = FontWeights.Bold,
                VerticalAlignment = VerticalAlignment.Center,
                HorizontalAlignment = HorizontalAlignment.Right
            };
            pnlText.SetBinding(TextBlock.TextProperty, new Binding("CombinedPnL") { StringFormat = "F2" });
            pnlText.SetBinding(TextBlock.ForegroundProperty, new Binding("CombinedPnL") { Converter = new PnLToColorConverter() });
            Grid.SetColumn(pnlText, 3);
            headlineGrid.Children.Add(pnlText);

            mainStack.Children.Add(headlineGrid);

            // Create collapsible legs section using a simple clickable header + content panel
            // (Avoids Expander control which has template issues in NinjaTrader's theme)

            // Clickable header row
            var headerBorder = new Border
            {
                Background = new SolidColorBrush(Color.FromRgb(50, 50, 55)),
                BorderBrush = _borderColor,
                BorderThickness = new Thickness(1, 1, 1, 0),
                Padding = new Thickness(8, 5, 8, 5),
                Margin = new Thickness(0, 5, 0, 0),
                Cursor = System.Windows.Input.Cursors.Hand
            };

            var headerPanel = new StackPanel { Orientation = Orientation.Horizontal };

            // Expand/collapse arrow indicator
            var arrowText = new TextBlock
            {
                Text = "▶",
                Foreground = _fgColor,
                FontSize = 10,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 0, 8, 0)
            };
            headerPanel.Children.Add(arrowText);

            headerPanel.Children.Add(new TextBlock
            {
                Text = "Legs",
                Foreground = _fgColor,
                FontWeight = FontWeights.SemiBold,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 0, 10, 0)
            });

            // CE leg summary
            var ceLeg = state.Legs.FirstOrDefault(l => l.OptionType == "CE");
            var peLeg = state.Legs.FirstOrDefault(l => l.OptionType == "PE");

            if (ceLeg != null)
            {
                var ceText = new TextBlock
                {
                    FontSize = 11,
                    VerticalAlignment = VerticalAlignment.Center,
                    Margin = new Thickness(5, 0, 10, 0)
                };
                ceText.SetBinding(TextBlock.TextProperty, new Binding("SymbolDisplay") { Source = ceLeg, StringFormat = "CE: {0}" });
                ceText.Foreground = new SolidColorBrush(Color.FromRgb(100, 200, 100));
                headerPanel.Children.Add(ceText);
            }

            if (peLeg != null)
            {
                var peText = new TextBlock
                {
                    FontSize = 11,
                    VerticalAlignment = VerticalAlignment.Center,
                    Margin = new Thickness(5, 0, 0, 0)
                };
                peText.SetBinding(TextBlock.TextProperty, new Binding("SymbolDisplay") { Source = peLeg, StringFormat = "PE: {0}" });
                peText.Foreground = new SolidColorBrush(Color.FromRgb(220, 100, 100));
                headerPanel.Children.Add(peText);
            }

            headerBorder.Child = headerPanel;

            // Content panel (legs details)
            var legsContent = CreateLegsPanel(state);
            legsContent.Visibility = state.IsExpanded ? Visibility.Visible : Visibility.Collapsed;

            // Update arrow based on expanded state
            if (state.IsExpanded) arrowText.Text = "▼";

            // Click handler for toggle
            headerBorder.MouseLeftButtonUp += (s, e) =>
            {
                state.IsExpanded = !state.IsExpanded;
                legsContent.Visibility = state.IsExpanded ? Visibility.Visible : Visibility.Collapsed;
                arrowText.Text = state.IsExpanded ? "▼" : "▶";
            };

            mainStack.Children.Add(headerBorder);
            mainStack.Children.Add(legsContent);

            border.Child = mainStack;
            return border;
        }

        private StackPanel CreateLegsPanel(TBSExecutionState state)
        {
            var panel = new StackPanel
            {
                Background = _expanderBg,
                Margin = new Thickness(0, 5, 0, 0)
            };

            // Create a row for each leg with data bindings
            foreach (var leg in state.Legs)
            {
                var legBorder = new Border
                {
                    BorderBrush = _borderColor,
                    BorderThickness = new Thickness(0, 0, 0, 1),
                    Padding = new Thickness(10, 8, 10, 8),
                    DataContext = leg
                };

                var legGrid = new Grid();

                // Define columns for leg details
                legGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(50) });   // Type (CE/PE)
                legGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(140) });  // Symbol
                legGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(70) });   // Entry
                legGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(70) });   // LTP
                legGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(70) });   // SL
                legGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(80) });   // P&L
                legGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(70) });   // Status
                legGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(70) });   // SL Status
                legGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(80) });   // Exit Price
                legGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(70) });   // Exit Time

                int col = 0;

                // Type (CE/PE) with color
                var typeText = new TextBlock
                {
                    Text = leg.OptionType,
                    FontWeight = FontWeights.Bold,
                    FontSize = 12,
                    VerticalAlignment = VerticalAlignment.Center,
                    Foreground = leg.OptionType == "CE"
                        ? new SolidColorBrush(Color.FromRgb(100, 200, 100))
                        : new SolidColorBrush(Color.FromRgb(220, 100, 100))
                };
                Grid.SetColumn(typeText, col++);
                legGrid.Children.Add(typeText);

                // Symbol - bound
                var symbolText = new TextBlock { FontSize = 11, VerticalAlignment = VerticalAlignment.Center, Foreground = _fgColor };
                symbolText.SetBinding(TextBlock.TextProperty, new Binding("SymbolDisplay"));
                Grid.SetColumn(symbolText, col++);
                legGrid.Children.Add(symbolText);

                // Entry Price - bound
                var entryText = CreateBoundTextBlock("EntryPriceDisplay", _fgColor);
                Grid.SetColumn(entryText, col++);
                legGrid.Children.Add(entryText);

                // LTP - bound
                var ltpText = CreateBoundTextBlock("CurrentPriceDisplay", _fgColor);
                Grid.SetColumn(ltpText, col++);
                legGrid.Children.Add(ltpText);

                // SL - bound
                var slText = CreateBoundTextBlock("SLPriceDisplay", _fgColor);
                Grid.SetColumn(slText, col++);
                legGrid.Children.Add(slText);

                // P&L - bound with color converter
                var pnlText = new TextBlock { FontSize = 11, FontWeight = FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center };
                pnlText.SetBinding(TextBlock.TextProperty, new Binding("PnLDisplay"));
                pnlText.SetBinding(TextBlock.ForegroundProperty, new Binding("PnL") { Converter = new PnLToColorConverter() });
                Grid.SetColumn(pnlText, col++);
                legGrid.Children.Add(pnlText);

                // Status - bound
                var statusText = CreateBoundTextBlock("StatusText", _fgColor);
                Grid.SetColumn(statusText, col++);
                legGrid.Children.Add(statusText);

                // SL Status - bound with color
                var slStatusText = new TextBlock { FontSize = 11, FontWeight = FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center };
                slStatusText.SetBinding(TextBlock.TextProperty, new Binding("SLStatus"));
                slStatusText.SetBinding(TextBlock.ForegroundProperty, new Binding("SLStatus") { Converter = new SLStatusToColorConverter() });
                Grid.SetColumn(slStatusText, col++);
                legGrid.Children.Add(slStatusText);

                // Exit Price - bound
                var exitPriceText = CreateBoundTextBlock("ExitPriceDisplay", _fgColor);
                Grid.SetColumn(exitPriceText, col++);
                legGrid.Children.Add(exitPriceText);

                // Exit Time - bound
                var exitTimeText = CreateBoundTextBlock("ExitTimeDisplay", _fgColor);
                Grid.SetColumn(exitTimeText, col++);
                legGrid.Children.Add(exitTimeText);

                legBorder.Child = legGrid;
                panel.Children.Add(legBorder);
            }

            // Add header labels above the first row
            var headerBorder = new Border
            {
                Background = _headerBg,
                Padding = new Thickness(10, 4, 10, 4)
            };

            var headerGrid = new Grid();
            headerGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(50) });
            headerGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(140) });
            headerGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(70) });
            headerGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(70) });
            headerGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(70) });
            headerGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(80) });
            headerGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(70) });
            headerGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(70) });
            headerGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(80) });
            headerGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(70) });

            string[] headers = { "Leg", "Symbol", "Entry", "LTP", "SL", "P&L", "Status", "SL Status", "Exit Price", "Exit Time" };
            for (int i = 0; i < headers.Length; i++)
            {
                var hdr = new TextBlock
                {
                    Text = headers[i],
                    Foreground = _fgColor,
                    FontWeight = FontWeights.Bold,
                    FontSize = 10
                };
                Grid.SetColumn(hdr, i);
                headerGrid.Children.Add(hdr);
            }

            headerBorder.Child = headerGrid;
            panel.Children.Insert(0, headerBorder);

            return panel;
        }

        private TextBlock CreateBoundTextBlock(string bindingPath, Brush foreground)
        {
            var tb = new TextBlock
            {
                FontSize = 11,
                VerticalAlignment = VerticalAlignment.Center,
                Foreground = foreground
            };
            tb.SetBinding(TextBlock.TextProperty, new Binding(bindingPath));
            return tb;
        }

        private SolidColorBrush GetStatusColor(TBSExecutionStatus status)
        {
            switch (status)
            {
                case TBSExecutionStatus.Skipped: return _skippedColor;
                case TBSExecutionStatus.Idle: return _idleColor;
                case TBSExecutionStatus.Monitoring: return _monitoringColor;
                case TBSExecutionStatus.Live: return _liveColor;
                case TBSExecutionStatus.SquaredOff: return _squaredOffColor;
                default: return _fgColor;
            }
        }

        private SolidColorBrush GetSLStatusColor(string status)
        {
            switch (status?.ToUpper())
            {
                case "SAFE": return new SolidColorBrush(Color.FromRgb(100, 200, 100));
                case "WARNING": return new SolidColorBrush(Color.FromRgb(220, 180, 50));
                case "HIT": return new SolidColorBrush(Color.FromRgb(220, 80, 80));
                default: return new SolidColorBrush(Color.FromRgb(150, 150, 150));
            }
        }

        private void UpdateSummary()
        {
            decimal totalPnL = _executionRows.Sum(s => s.CombinedPnL);
            int liveCount = _executionRows.Count(s => s.Status == TBSExecutionStatus.Live);
            int monitoringCount = _executionRows.Count(s => s.Status == TBSExecutionStatus.Monitoring);

            _lblTotalPnL.Text = totalPnL.ToString("F2");
            _lblTotalPnL.Foreground = totalPnL >= 0
                ? new SolidColorBrush(Color.FromRgb(100, 200, 100))
                : new SolidColorBrush(Color.FromRgb(220, 80, 80));

            _lblLiveCount.Text = liveCount.ToString();
            _lblMonitoringCount.Text = monitoringCount.ToString();
        }

        #endregion

        #region Status Timer

        private void StartStatusTimer()
        {
            _statusTimer = new DispatcherTimer
            {
                Interval = TimeSpan.FromSeconds(1)
            };
            _statusTimer.Tick += OnStatusTimerTick;
            _statusTimer.Start();
        }

        private void StopStatusTimer()
        {
            if (_statusTimer != null)
            {
                _statusTimer.Stop();
                _statusTimer.Tick -= OnStatusTimerTick;
                _statusTimer = null;
            }
        }

        private void OnStatusTimerTick(object sender, EventArgs e)
        {
            // Skip processing if Option Chain is not ready yet
            if (!_optionChainReady)
                return;

            // ALWAYS use real system time for TBS status logic
            // Simulation only affects price data replay, not time-based execution
            var isSimulationActive = SimService.Instance.IsSimulationActive;
            var now = DateTime.Now.TimeOfDay;

            foreach (var state in _executionRows)
            {
                var oldStatus = state.Status;
                // Skip exit time check when simulation is running (so positions don't auto-exit)
                bool statusChanged = state.UpdateStatusBasedOnTime(now, skipExitTimeCheck: isSimulationActive);

                // Log status changes
                if (statusChanged)
                {
                    Logger.Info($"[TBSManager] {state.Config?.EntryTime:hh\\:mm\\:ss} Status changed: {oldStatus} -> {state.Status} (StrikeLocked={state.StrikeLocked})");
                }

                // When entering Monitoring state, fetch ATM strike (if not locked)
                if (oldStatus != TBSExecutionStatus.Monitoring && state.Status == TBSExecutionStatus.Monitoring)
                {
                    Logger.Info($"[TBSManager] {state.Config?.EntryTime:hh\\:mm\\:ss} Entered Monitoring - fetching ATM strike");
                    UpdateStrikeForState(state);
                }

                // While in Monitoring, keep updating ATM strike (follows spot price)
                if (state.Status == TBSExecutionStatus.Monitoring && !state.StrikeLocked)
                {
                    UpdateStrikeForState(state);
                }

                // When going Live, lock the strike and record entry prices
                if (oldStatus == TBSExecutionStatus.Monitoring && state.Status == TBSExecutionStatus.Live)
                {
                    Logger.Info($"[TBSManager] {state.Config?.EntryTime:hh\\:mm\\:ss} Going Live - locking strike and entering positions");
                    LockStrikeAndEnter(state);
                }

                // While Live, check for SL conditions
                if (state.Status == TBSExecutionStatus.Live && state.StrikeLocked)
                {
                    CheckSLConditions(state);
                }

                // When going SquaredOff, record exit details
                if (oldStatus == TBSExecutionStatus.Live && state.Status == TBSExecutionStatus.SquaredOff)
                {
                    Logger.Info($"[TBSManager] {state.Config?.EntryTime:hh\\:mm\\:ss} Going SquaredOff - recording exit prices");
                    RecordExitPrices(state);
                }

                // Update combined P&L from legs
                state.UpdateCombinedPnL();
            }

            UpdateSummary();
        }

        /// <summary>
        /// Check SL conditions for a Live position
        /// </summary>
        private void CheckSLConditions(TBSExecutionState state)
        {
            bool anyLegHitSLThisTick = false;

            foreach (var leg in state.Legs)
            {
                // Skip if leg is already exited
                if (leg.Status != TBSLegStatus.Active)
                    continue;

                // Update current price from PriceHub
                decimal currentPrice = GetCurrentPrice(leg.Symbol);
                if (currentPrice > 0)
                {
                    leg.CurrentPrice = currentPrice;
                }

                // Check individual leg SL (for short position: price goes UP above SL)
                if (leg.SLPrice > 0 && leg.CurrentPrice >= leg.SLPrice)
                {
                    // SL Hit!
                    leg.Status = TBSLegStatus.SLHit;
                    leg.ExitPrice = leg.CurrentPrice;
                    leg.ExitTime = DateTime.Now;
                    leg.ExitReason = $"SL Hit @ {leg.CurrentPrice:F2}";
                    anyLegHitSLThisTick = true;
                    Logger.Warn($"[TBSManager] {leg.OptionType} SL HIT: Entry={leg.EntryPrice:F2}, SL={leg.SLPrice:F2}, Exit={leg.ExitPrice:F2}");
                }
            }

            // Handle hedge_to_cost: When one leg hits SL, move the other leg's SL to entry price (cost)
            if (anyLegHitSLThisTick && !state.SLToCostApplied &&
                state.HedgeAction != null && state.HedgeAction.Contains("hedge_to_cost"))
            {
                // Find the other leg that is still active and move its SL to cost
                foreach (var leg in state.Legs.Where(l => l.Status == TBSLegStatus.Active))
                {
                    decimal oldSL = leg.SLPrice;
                    leg.SLPrice = leg.EntryPrice; // Move SL to entry price (cost)
                    Logger.Info($"[TBSManager] HEDGE_TO_COST: {leg.OptionType} SL moved from {oldSL:F2} to cost {leg.SLPrice:F2}");
                }
                state.SLToCostApplied = true;
                state.Message = "SL moved to cost (hedge)";
            }

            // Check combined SL (if configured)
            if (state.CombinedSLPercent > 0)
            {
                // Calculate combined premium at entry vs now
                decimal entryPremium = state.Legs.Sum(l => l.EntryPrice);
                decimal currentPremium = state.Legs.Sum(l => l.CurrentPrice);

                if (entryPremium > 0)
                {
                    decimal combinedSLPrice = entryPremium * (1 + state.CombinedSLPercent);
                    if (currentPremium >= combinedSLPrice)
                    {
                        // Combined SL hit - exit all active legs
                        Logger.Warn($"[TBSManager] Combined SL HIT: EntryPremium={entryPremium:F2}, CurrentPremium={currentPremium:F2}, SL={combinedSLPrice:F2}");
                        foreach (var leg in state.Legs.Where(l => l.Status == TBSLegStatus.Active))
                        {
                            leg.Status = TBSLegStatus.SLHit;
                            leg.ExitPrice = leg.CurrentPrice;
                            leg.ExitTime = DateTime.Now;
                            leg.ExitReason = "Combined SL Hit";
                        }
                    }
                }
            }

            // If all legs are closed, mark state as SquaredOff
            if (state.AllLegsClosed())
            {
                state.Status = TBSExecutionStatus.SquaredOff;
                state.Message = "All legs exited (SL)";
                state.ExitTime = DateTime.Now;
            }
        }

        /// <summary>
        /// Record exit prices for all legs when going SquaredOff
        /// </summary>
        private void RecordExitPrices(TBSExecutionState state)
        {
            foreach (var leg in state.Legs)
            {
                if (leg.Status == TBSLegStatus.Active)
                {
                    decimal exitPrice = GetCurrentPrice(leg.Symbol);
                    if (exitPrice > 0)
                    {
                        leg.ExitPrice = exitPrice;
                        leg.ExitTime = DateTime.Now;
                        leg.ExitReason = "Time-based exit";
                        leg.Status = TBSLegStatus.Exited;

                        Logger.Info($"[TBSManager] Leg {leg.OptionType} {leg.Symbol}: Exit @ {exitPrice:F2}, P&L={leg.PnL:F2}");
                    }
                }
            }
        }

        /// <summary>
        /// Update ATM strike for a state based on current spot price
        /// </summary>
        private void UpdateStrikeForState(TBSExecutionState state)
        {
            if (state.StrikeLocked) return;

            var underlying = state.Config?.Underlying;
            if (string.IsNullOrEmpty(underlying)) return;

            // Get ATM strike from spot price
            decimal atmStrike = FindATMStrike(underlying);
            if (atmStrike <= 0) return;

            // Only update if strike changed
            if (state.Strike != atmStrike)
            {
                state.Strike = atmStrike;

                // Update leg symbols
                var expiry = _selectedExpiry ?? FindNearestExpiry(underlying, state.Config?.DTE ?? 0);
                if (expiry.HasValue)
                {
                    string monthAbbr = expiry.Value.ToString("MMM").ToUpper();
                    string ceSymbol = $"{underlying}{expiry.Value:yy}{monthAbbr}{atmStrike:F0}CE";
                    string peSymbol = $"{underlying}{expiry.Value:yy}{monthAbbr}{atmStrike:F0}PE";

                    var ceLeg = state.Legs.FirstOrDefault(l => l.OptionType == "CE");
                    var peLeg = state.Legs.FirstOrDefault(l => l.OptionType == "PE");

                    if (ceLeg != null) ceLeg.Symbol = ceSymbol;
                    if (peLeg != null) peLeg.Symbol = peSymbol;
                }
            }
        }

        /// <summary>
        /// Lock the strike and record entry prices when going Live
        /// </summary>
        private void LockStrikeAndEnter(TBSExecutionState state)
        {
            state.StrikeLocked = true;

            foreach (var leg in state.Legs)
            {
                // Get current price as entry price
                decimal price = GetCurrentPrice(leg.Symbol);
                if (price > 0)
                {
                    leg.EntryPrice = price;
                    leg.CurrentPrice = price;
                    leg.Status = TBSLegStatus.Active;

                    // Calculate SL based on cached config (entry + SL%)
                    // For short straddle: SL is when price goes UP above entry + SL%
                    if (state.IndividualSLPercent > 0)
                    {
                        // SL% is stored as decimal (e.g., 0.2 for 20%, 0.5 for 50%)
                        leg.SLPrice = leg.EntryPrice * (1 + state.IndividualSLPercent);
                        Logger.Debug($"[TBSManager] {leg.OptionType} SL set: Entry={leg.EntryPrice:F2}, SL%={state.IndividualSLPercent:P0}, SLPrice={leg.SLPrice:F2}");
                    }
                }
            }

            Logger.Info($"[TBSManagerTabPage] Locked strike {state.Strike} for {state.Config?.Underlying} at {state.Config?.EntryTime}, LotSize={state.LotSize}, Qty={state.Quantity}");
        }

        private decimal FindATMStrike(string underlying)
        {
            // Get ATM strike directly from MarketAnalyzerLogic (set by Option Chain)
            // This avoids recalculating and ensures we use the same ATM as Option Chain displays
            return MarketAnalyzerLogic.Instance.GetATMStrike(underlying);
        }

        private DateTime? FindNearestExpiry(string underlying, int targetDTE)
        {
            var today = DateTime.Today;
            var targetDate = today.AddDays(targetDTE);

            // Find next Thursday on or after target date (for weekly expiry)
            while (targetDate.DayOfWeek != DayOfWeek.Thursday)
                targetDate = targetDate.AddDays(1);

            return targetDate;
        }

        private decimal GetCurrentPrice(string symbol)
        {
            if (string.IsNullOrEmpty(symbol)) return 0;

            // Use centralized PriceHub from MarketAnalyzerLogic (populated by Option Chain)
            // This avoids duplicate WebSocket subscriptions - Option Chain already subscribes
            return MarketAnalyzerLogic.Instance.GetPrice(symbol);
        }

        #endregion

        #region Event Subscriptions

        private void SubscribeToEvents()
        {
            // Subscribe to price updates from PriceHub (centralized, populated by Option Chain)
            // This avoids duplicate WebSocket subscriptions - we get prices that Option Chain already receives
            MarketAnalyzerLogic.Instance.PriceUpdated += OnPriceHubUpdated;

            // Subscribe to options generated event to auto-derive underlying and DTE from Option Chain
            MarketAnalyzerLogic.Instance.OptionsGenerated += OnOptionsGenerated;
        }

        private void UnsubscribeFromEvents()
        {
            MarketAnalyzerLogic.Instance.PriceUpdated -= OnPriceHubUpdated;
            MarketAnalyzerLogic.Instance.OptionsGenerated -= OnOptionsGenerated;
        }

        /// <summary>
        /// Called when Option Chain generates options - auto-derives underlying and DTE for filtering
        /// Starts a 45-second delay timer to let Option Chain fully load before TBS processes
        /// </summary>
        private void OnOptionsGenerated(List<MappedInstrument> options)
        {
            if (options == null || options.Count == 0)
                return;

            var first = options.First();
            string underlying = first.underlying;
            DateTime? expiry = first.expiry;

            if (string.IsNullOrEmpty(underlying) || !expiry.HasValue)
            {
                Logger.Warn($"[TBSManagerTabPage] OnOptionsGenerated: Missing underlying or expiry");
                return;
            }

            // Calculate DTE
            int dte = (int)(expiry.Value.Date - DateTime.Today).TotalDays;
            if (dte < 0) dte = 0;

            Logger.Info($"[TBSManagerTabPage] OnOptionsGenerated: Auto-derived underlying={underlying}, expiry={expiry.Value:dd-MMM-yyyy}, DTE={dte}");

            // Store for later use
            _selectedUnderlying = underlying;
            _selectedExpiry = expiry;

            // Update UI on dispatcher thread
            Dispatcher.InvokeAsync(() =>
            {
                try
                {
                    // Auto-populate filter controls
                    if (!_cboUnderlying.Items.Contains(underlying.ToUpperInvariant()))
                    {
                        _cboUnderlying.Items.Add(underlying.ToUpperInvariant());
                    }

                    // Select the underlying
                    for (int i = 0; i < _cboUnderlying.Items.Count; i++)
                    {
                        if (_cboUnderlying.Items[i].ToString().Equals(underlying, StringComparison.OrdinalIgnoreCase))
                        {
                            _cboUnderlying.SelectedIndex = i;
                            break;
                        }
                    }

                    // Set DTE filter
                    _txtDTE.Text = dte.ToString();

                    // Apply filter for config tab (but not execution tab yet)
                    ApplyFilter();

                    // Start delay timer if not already running
                    if (_optionChainDelayTimer == null && !_optionChainReady)
                    {
                        StartOptionChainDelayTimer();
                        _lblConfigStatus.Text = $"Auto-filtered for {underlying} DTE={dte} - Waiting {OPTION_CHAIN_DELAY_SECONDS}s for Option Chain...";
                    }
                    else
                    {
                        _lblConfigStatus.Text = $"Auto-filtered for {underlying} DTE={dte} (Expiry: {expiry.Value:dd-MMM-yyyy})";
                    }

                    Logger.Info($"[TBSManagerTabPage] OnOptionsGenerated: Applied auto-filter, showing {_configRows.Count} configs");
                }
                catch (Exception ex)
                {
                    Logger.Error($"[TBSManagerTabPage] OnOptionsGenerated: Error applying filter - {ex.Message}");
                }
            });
        }

        /// <summary>
        /// Start a 45-second delay timer to let Option Chain fully load
        /// </summary>
        private void StartOptionChainDelayTimer()
        {
            _delayCountdown = OPTION_CHAIN_DELAY_SECONDS;
            _optionChainDelayTimer = new DispatcherTimer
            {
                Interval = TimeSpan.FromSeconds(1)
            };
            _optionChainDelayTimer.Tick += OnOptionChainDelayTick;
            _optionChainDelayTimer.Start();

            Logger.Info($"[TBSManagerTabPage] Started {OPTION_CHAIN_DELAY_SECONDS}s delay timer for Option Chain loading");

            // Show waiting message
            ShowWaitingForOptionChain();
        }

        private void OnOptionChainDelayTick(object sender, EventArgs e)
        {
            _delayCountdown--;

            if (_delayCountdown <= 0)
            {
                // Delay complete - stop timer and initialize execution
                StopOptionChainDelayTimer();
                _optionChainReady = true;

                Logger.Info("[TBSManagerTabPage] Option Chain delay complete - initializing execution states");

                // Now initialize execution states
                InitializeExecutionStates();

                _lblConfigStatus.Text = $"Option Chain loaded - {_selectedUnderlying} DTE={_txtDTE.Text}";
            }
            else
            {
                // Update countdown in UI
                ShowWaitingForOptionChain();

                if (_delayCountdown % 10 == 0)
                {
                    Logger.Debug($"[TBSManagerTabPage] Option Chain delay: {_delayCountdown}s remaining");
                }
            }
        }

        private void StopOptionChainDelayTimer()
        {
            if (_optionChainDelayTimer != null)
            {
                _optionChainDelayTimer.Stop();
                _optionChainDelayTimer.Tick -= OnOptionChainDelayTick;
                _optionChainDelayTimer = null;
            }
        }

        /// <summary>
        /// Called when PriceHub receives a price update (from Option Chain's WebSocket subscription)
        /// No local cache needed - PriceHub is the single source of truth
        /// </summary>
        private void OnPriceHubUpdated(string symbol, decimal price)
        {
            // Update leg prices if matching - no local cache needed
            Dispatcher.InvokeAsync(() =>
            {
                try
                {
                    foreach (var state in _executionRows)
                    {
                        foreach (var leg in state.Legs)
                        {
                            if (leg.Symbol == symbol)
                            {
                                leg.CurrentPrice = price;
                            }
                        }
                    }
                }
                catch (Exception ex)
                {
                    Logger.Debug($"[TBSManagerTabPage] OnPriceHubUpdated error: {ex.Message}");
                }
            });
        }

        #endregion

        #region Helper Methods

        private GridViewColumn CreateColumn(string header, string binding, double width, string format = null)
        {
            var column = new GridViewColumn
            {
                Header = header,
                Width = width
            };

            if (format != null)
            {
                column.DisplayMemberBinding = new Binding(binding)
                {
                    StringFormat = format
                };
            }
            else
            {
                column.DisplayMemberBinding = new Binding(binding);
            }

            return column;
        }

        #endregion

        #region IInstrumentProvider Implementation

        public Instrument Instrument
        {
            get => _instrument;
            set
            {
                if (_instrument != value)
                {
                    _instrument = value;
                    OnInstrumentChanged();
                }
            }
        }

        private void OnInstrumentChanged()
        {
            if (_instrument == null)
                return;

            var symbol = _instrument.FullName;
            if (!string.IsNullOrEmpty(symbol))
            {
                _selectedUnderlying = symbol;
                Logger.Debug($"[TBSManagerTabPage] Instrument changed to: {symbol}");
            }
        }

        #endregion

        #region NTTabPage Overrides

        protected override string GetHeaderPart(string variable)
        {
            return "TBS Manager";
        }

        public override void Cleanup()
        {
            Logger.Info("[TBSManagerTabPage] Cleanup: Starting");

            StopStatusTimer();
            StopOptionChainDelayTimer();
            UnsubscribeFromEvents();

            _configRows?.Clear();
            _executionRows?.Clear();

            base.Cleanup();
        }

        protected override void Restore(XElement element)
        {
            // Restore saved state if needed
        }

        protected override void Save(XElement element)
        {
            // Save state if needed
        }

        #endregion
    }
}
