using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.Linq;
using System.Threading.Tasks;
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
using ZerodhaDatafeedAdapter.Helpers;
using ZerodhaDatafeedAdapter.Logging;
using ZerodhaDatafeedAdapter.AddOns.MarketAnalyzer;
using ZerodhaDatafeedAdapter.AddOns.TBSManager.Converters;
using ZerodhaDatafeedAdapter.ViewModels;
using SimService = ZerodhaDatafeedAdapter.Services.SimulationService;

namespace ZerodhaDatafeedAdapter.AddOns.TBSManager
{
    // Value converters extracted to AddOns/TBSManager/Converters/ folder:
    // - PnLToColorConverter.cs
    // - StatusToColorConverter.cs
    // - SLStatusToColorConverter.cs

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
        private DispatcherTimer _stoxxoPollingTimer;
        private string _selectedUnderlying;
        private DateTime? _selectedExpiry;
        private bool _optionChainReady = false;
        private int _delayCountdown = 45; // 45 second delay after Option Chain loads
        private const int OPTION_CHAIN_DELAY_SECONDS = 45;
        private int _nextTrancheId = 1; // Auto-increment tranche ID

        // Stoxxo service for order execution
        private StoxxoService _stoxxoService;

        // ViewModel for MVVM pattern - delegates to TBSExecutionService
        private TBSViewModel _viewModel;

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

            // Initialize ViewModel (MVVM pattern - delegates execution logic to TBSExecutionService)
            _viewModel = new TBSViewModel();

            // Initialize Stoxxo service
            _stoxxoService = StoxxoService.Instance;

            BuildUI();
            LoadConfigurations();
            SubscribeToEvents();
            SubscribeToViewModelEvents();
            StartStatusTimer();
            StartStoxxoPollingTimer();

            Logger.Info("[TBSManagerTabPage] Constructor: Completed");
        }

        /// <summary>
        /// Subscribe to ViewModel events for UI updates
        /// </summary>
        private void SubscribeToViewModelEvents()
        {
            _viewModel.PropertyChanged += OnViewModelPropertyChanged;
            _viewModel.TrancheStateChanged += OnViewModelTrancheStateChanged;
        }

        /// <summary>
        /// Unsubscribe from ViewModel events
        /// </summary>
        private void UnsubscribeFromViewModelEvents()
        {
            if (_viewModel != null)
            {
                _viewModel.PropertyChanged -= OnViewModelPropertyChanged;
                _viewModel.TrancheStateChanged -= OnViewModelTrancheStateChanged;
            }
        }

        private void OnViewModelPropertyChanged(object sender, PropertyChangedEventArgs e)
        {
            // Update UI when ViewModel properties change
            Dispatcher.InvokeAsync(() =>
            {
                switch (e.PropertyName)
                {
                    case nameof(TBSViewModel.TotalPnL):
                    case nameof(TBSViewModel.ExecutionSummaryText):
                        UpdateSummaryFromViewModel();
                        break;

                    case nameof(TBSViewModel.IsOptionChainReady):
                        if (_viewModel.IsOptionChainReady && !_optionChainReady)
                        {
                            // ViewModel says ready - sync local state
                            _optionChainReady = true;
                            StopOptionChainDelayTimer();
                            InitializeExecutionStatesFromViewModel();
                        }
                        break;
                }
            });
        }

        private void OnViewModelTrancheStateChanged(object sender, TBSExecutionState state)
        {
            // Refresh specific tranche UI when its state changes
            Dispatcher.InvokeAsync(() => UpdateTrancheUIForState(state));
        }

        /// <summary>
        /// Update summary labels from ViewModel
        /// </summary>
        private void UpdateSummaryFromViewModel()
        {
            var pnl = _viewModel.TotalPnL;
            _lblTotalPnL.Text = $"Total P&L: {(pnl >= 0 ? "+" : "")}{pnl:N2}";
            _lblTotalPnL.Foreground = pnl >= 0 ? _liveColor : new SolidColorBrush(Color.FromRgb(220, 80, 80));
            _lblLiveCount.Text = $"Live: {_viewModel.LiveCount}";
            _lblMonitoringCount.Text = $"Monitoring: {_viewModel.MonitoringCount}";
        }

        /// <summary>
        /// Initialize execution states using ViewModel (delegates to TBSExecutionService)
        /// </summary>
        private void InitializeExecutionStatesFromViewModel()
        {
            string underlying = _cboUnderlying.SelectedItem?.ToString();
            int? dte = null;
            if (int.TryParse(_txtDTE.Text, out int parsedDte))
                dte = parsedDte;

            // Tell ViewModel to refresh execution states with current filter
            _viewModel.SelectedUnderlying = underlying == "All" ? "All" : underlying;
            _viewModel.SelectedDTE = dte;
            _viewModel.RefreshExecutionStates();

            // Sync local collection from ViewModel's ExecutionStates
            _executionRows.Clear();
            foreach (var state in _viewModel.ExecutionStates)
            {
                _executionRows.Add(state);
            }

            RebuildExecutionUI();
            UpdateSummaryFromViewModel();
            _lblExecutionStatus.Text = $"Loaded {_executionRows.Count} tranches (via ViewModel)";
            TBSLogger.Info($"[TBSManagerTabPage] Initialized {_executionRows.Count} tranches from ViewModel");
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
            configGridView.Columns.Add(CreateColumn("Target", "TargetPercent", 60, "{0:P0}"));
            configGridView.Columns.Add(CreateColumn("Action", "HedgeAction", 100));
            configGridView.Columns.Add(CreateColumn("Qty", "Quantity", 50));
            configGridView.Columns.Add(CreateColumn("Active", "IsActive", 60));
            configGridView.Columns.Add(CreateColumn("ProfitCond", "ProfitCondition", 70));

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
            // Note: For true UI virtualization with 100+ tranches, convert to ItemsControl with DataTemplates
            // Current StackPanel approach works well for typical 10-30 tranches
            _executionScrollViewer = new ScrollViewer
            {
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
                Margin = new Thickness(5),
                // Smooth scrolling
                CanContentScroll = false
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
                    TrancheId = _nextTrancheId++,
                    // Cache config values in state for later use
                    LotSize = lotSize,
                    Quantity = config.Quantity,
                    IndividualSLPercent = config.IndividualSL,
                    CombinedSLPercent = config.CombinedSL,
                    TargetPercent = config.TargetPercent,
                    ProfitCondition = config.ProfitCondition,
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

                // INITIALIZATION LOGIC:
                // When TBS Manager starts, tranches whose entry time has already passed should be SKIPPED.
                // We only track tranches where:
                // 1. Entry time is in the future (Idle)
                // 2. Entry time is within 5 minutes (Monitoring)
                // 3. Entry time JUST passed (within last minute) - treat as Live
                // Tranches with entry time more than 1 minute ago are considered MISSED.

                var entryTime = config.EntryTime;
                var timeSinceEntry = now - entryTime;

                if (timeSinceEntry.TotalMinutes > 1)
                {
                    // Entry time passed more than 1 minute ago - this tranche was MISSED
                    // Mark as Idle with IsMissed=true to freeze status updates
                    state.Status = TBSExecutionStatus.Idle;
                    state.IsMissed = true;  // Prevent status transitions via timer
                    state.Message = $"Missed (entry was at {entryTime:hh\\:mm\\:ss})";
                    TBSLogger.Info($"Tranche #{state.TrancheId} {config.EntryTime:hh\\:mm\\:ss} MISSED - initialized after entry time passed, IsMissed=true");
                }
                else
                {
                    // Entry time is in the future or within monitoring window
                    // Update status based on time (skip exit time check during simulation)
                    state.UpdateStatusBasedOnTime(now, skipExitTimeCheck: isSimulationActive);

                    // IMPORTANT: If status went to Live during initialization, it means entry "just passed"
                    // These should be treated as MISSED - we don't track them or send Stoxxo orders
                    // Critical: They must NOT contribute to cumulative P&L for ProfitCondition evaluation
                    if (state.Status == TBSExecutionStatus.Live && !state.StrikeLocked)
                    {
                        // Entry just passed but we weren't running - treat as missed
                        state.Status = TBSExecutionStatus.Idle;
                        state.IsMissed = true;  // Prevent status transitions via timer
                        state.Message = $"Missed (entry just passed at {entryTime:hh\\:mm\\:ss})";
                        TBSLogger.Info($"Tranche #{state.TrancheId} {config.EntryTime:hh\\:mm\\:ss} Entry just passed - marking as MISSED (no Stoxxo order, no P&L contribution)");
                    }
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
        /// Get lot size for an underlying index (uses shared SymbolHelper)
        /// </summary>
        private int GetLotSizeForUnderlying(string underlying)
        {
            return SymbolHelper.GetLotSize(underlying);
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

        /// <summary>
        /// Update a specific tranche's UI when its state changes (from ViewModel event)
        /// </summary>
        private void UpdateTrancheUIForState(TBSExecutionState state)
        {
            // Find the Border with this state as Tag and trigger a binding refresh
            foreach (var child in _executionPanel.Children)
            {
                if (child is Border border && border.Tag == state)
                {
                    // Trigger binding updates by re-setting DataContext
                    border.DataContext = null;
                    border.DataContext = state;
                    break;
                }
            }

            // Also update summary
            UpdateSummaryFromViewModel();
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

            // Headline row: Tranche | Entry Time | Qty | Entry | Exit | Exit Time | Status | P&L | Stoxxo Name | Stoxxo Status | Stoxxo P&L | Message
            var headlineGrid = new Grid();
            headlineGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(40) });   // Tranche ID
            headlineGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(70) });   // Entry Time
            headlineGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(35) });   // Qty
            headlineGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(80) });   // Entry Price
            headlineGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(80) });   // Exit Price
            headlineGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(65) });   // Exit Time
            headlineGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(80) });   // Ninja Status
            headlineGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(80) });   // Ninja P&L
            headlineGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(110) });  // Stoxxo Name
            headlineGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(90) });   // Stoxxo Status
            headlineGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(80) });   // Stoxxo P&L
            headlineGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) }); // Message

            int col = 0;

            // Tranche ID
            var trancheText = new TextBlock
            {
                FontSize = 12,
                FontWeight = FontWeights.Bold,
                VerticalAlignment = VerticalAlignment.Center,
                Foreground = _fgColor
            };
            trancheText.SetBinding(TextBlock.TextProperty, new Binding("TrancheId") { StringFormat = "#{0}" });
            Grid.SetColumn(trancheText, col++);
            headlineGrid.Children.Add(trancheText);

            // Entry Time (static - doesn't change)
            var entryTimeText = new TextBlock
            {
                Text = state.EntryTimeDisplay,
                Foreground = _fgColor,
                FontSize = 12,
                FontWeight = FontWeights.SemiBold,
                VerticalAlignment = VerticalAlignment.Center
            };
            Grid.SetColumn(entryTimeText, col++);
            headlineGrid.Children.Add(entryTimeText);

            // Qty (number of lots)
            var qtyText = new TextBlock
            {
                Foreground = _fgColor,
                FontSize = 11,
                VerticalAlignment = VerticalAlignment.Center
            };
            qtyText.SetBinding(TextBlock.TextProperty, new Binding("Quantity") { StringFormat = "{0}" });
            Grid.SetColumn(qtyText, col++);
            headlineGrid.Children.Add(qtyText);

            // Entry Price (CE+PE combined)
            var entryPriceText = new TextBlock
            {
                Foreground = _fgColor,
                FontSize = 11,
                VerticalAlignment = VerticalAlignment.Center
            };
            entryPriceText.SetBinding(TextBlock.TextProperty, new Binding("EntryPriceDisplay"));
            Grid.SetColumn(entryPriceText, col++);
            headlineGrid.Children.Add(entryPriceText);

            // Exit Price (CE+PE combined)
            var exitPriceText = new TextBlock
            {
                Foreground = _fgColor,
                FontSize = 11,
                VerticalAlignment = VerticalAlignment.Center
            };
            exitPriceText.SetBinding(TextBlock.TextProperty, new Binding("ExitPriceDisplay"));
            Grid.SetColumn(exitPriceText, col++);
            headlineGrid.Children.Add(exitPriceText);

            // Exit Time (HH:mm:ss)
            var exitTimeText = new TextBlock
            {
                Foreground = _fgColor,
                FontSize = 11,
                VerticalAlignment = VerticalAlignment.Center
            };
            exitTimeText.SetBinding(TextBlock.TextProperty, new Binding("ExitTimeDisplay"));
            Grid.SetColumn(exitTimeText, col++);
            headlineGrid.Children.Add(exitTimeText);

            // Ninja Status with color - bound to StatusText property
            var ninjaStatusText = new TextBlock
            {
                FontSize = 11,
                FontWeight = FontWeights.SemiBold,
                VerticalAlignment = VerticalAlignment.Center
            };
            ninjaStatusText.SetBinding(TextBlock.TextProperty, new Binding("StatusText"));
            ninjaStatusText.SetBinding(TextBlock.ForegroundProperty, new Binding("Status") { Converter = new StatusToColorConverter() });
            Grid.SetColumn(ninjaStatusText, col++);
            headlineGrid.Children.Add(ninjaStatusText);

            // Ninja P&L - bound to CombinedPnL property
            var ninjaPnlText = new TextBlock
            {
                FontSize = 12,
                FontWeight = FontWeights.Bold,
                VerticalAlignment = VerticalAlignment.Center
            };
            ninjaPnlText.SetBinding(TextBlock.TextProperty, new Binding("CombinedPnL") { StringFormat = "F2" });
            ninjaPnlText.SetBinding(TextBlock.ForegroundProperty, new Binding("CombinedPnL") { Converter = new PnLToColorConverter() });
            Grid.SetColumn(ninjaPnlText, col++);
            headlineGrid.Children.Add(ninjaPnlText);

            // Stoxxo Name - bound to StoxxoPortfolioNameDisplay
            var stoxxoNameText = new TextBlock
            {
                FontSize = 11,
                VerticalAlignment = VerticalAlignment.Center,
                Foreground = _fgColor
            };
            stoxxoNameText.SetBinding(TextBlock.TextProperty, new Binding("StoxxoPortfolioNameDisplay"));
            Grid.SetColumn(stoxxoNameText, col++);
            headlineGrid.Children.Add(stoxxoNameText);

            // Stoxxo Status - bound to StoxxoStatusDisplay
            var stoxxoStatusText = new TextBlock
            {
                FontSize = 11,
                VerticalAlignment = VerticalAlignment.Center,
                Foreground = new SolidColorBrush(Color.FromRgb(180, 150, 220))  // Light purple for Stoxxo
            };
            stoxxoStatusText.SetBinding(TextBlock.TextProperty, new Binding("StoxxoStatusDisplay"));
            Grid.SetColumn(stoxxoStatusText, col++);
            headlineGrid.Children.Add(stoxxoStatusText);

            // Stoxxo P&L - bound to StoxxoPnL
            var stoxxoPnlText = new TextBlock
            {
                FontSize = 12,
                FontWeight = FontWeights.Bold,
                VerticalAlignment = VerticalAlignment.Center
            };
            stoxxoPnlText.SetBinding(TextBlock.TextProperty, new Binding("StoxxoPnLDisplay"));
            stoxxoPnlText.SetBinding(TextBlock.ForegroundProperty, new Binding("StoxxoPnL") { Converter = new PnLToColorConverter() });
            Grid.SetColumn(stoxxoPnlText, col++);
            headlineGrid.Children.Add(stoxxoPnlText);

            // Message - bound to Message property
            var messageText = new TextBlock
            {
                Foreground = new SolidColorBrush(Color.FromRgb(180, 180, 180)),
                FontSize = 10,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(10, 0, 0, 0),
                TextTrimming = TextTrimming.CharacterEllipsis
            };
            messageText.SetBinding(TextBlock.TextProperty, new Binding("Message"));
            Grid.SetColumn(messageText, col++);
            headlineGrid.Children.Add(messageText);

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

                // Define columns for leg details (including Stoxxo)
                // Order: Type, Symbol, Entry, LTP, SL, Exit, Time, P&L, Status, SL Sts, Stx Entry, Stx Exit, Stx Sts
                legGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(40) });   // Type (CE/PE)
                legGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(130) });  // Symbol
                legGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(60) });   // Entry
                legGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(60) });   // LTP
                legGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(60) });   // SL
                legGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(60) });   // Exit Price
                legGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(60) });   // Exit Time
                legGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(70) });   // P&L
                legGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(55) });   // Status
                legGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(55) });   // SL Status
                legGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(60) });   // Stoxxo Entry
                legGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(60) });   // Stoxxo Exit
                legGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(60) });   // Stoxxo Status

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

                // Exit Price - bound (moved after SL)
                var exitPriceText = CreateBoundTextBlock("ExitPriceDisplay", _fgColor);
                Grid.SetColumn(exitPriceText, col++);
                legGrid.Children.Add(exitPriceText);

                // Exit Time - bound (moved after Exit Price)
                var exitTimeText = CreateBoundTextBlock("ExitTimeDisplay", _fgColor);
                Grid.SetColumn(exitTimeText, col++);
                legGrid.Children.Add(exitTimeText);

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

                // Stoxxo Entry Price - bound
                var stoxxoEntryText = new TextBlock
                {
                    FontSize = 10,
                    VerticalAlignment = VerticalAlignment.Center,
                    Foreground = new SolidColorBrush(Color.FromRgb(180, 150, 220))  // Light purple for Stoxxo
                };
                stoxxoEntryText.SetBinding(TextBlock.TextProperty, new Binding("StoxxoEntryPriceDisplay"));
                Grid.SetColumn(stoxxoEntryText, col++);
                legGrid.Children.Add(stoxxoEntryText);

                // Stoxxo Exit Price - bound
                var stoxxoExitText = new TextBlock
                {
                    FontSize = 10,
                    VerticalAlignment = VerticalAlignment.Center,
                    Foreground = new SolidColorBrush(Color.FromRgb(180, 150, 220))
                };
                stoxxoExitText.SetBinding(TextBlock.TextProperty, new Binding("StoxxoExitPriceDisplay"));
                Grid.SetColumn(stoxxoExitText, col++);
                legGrid.Children.Add(stoxxoExitText);

                // Stoxxo Status - bound
                var stoxxoLegStatusText = new TextBlock
                {
                    FontSize = 10,
                    VerticalAlignment = VerticalAlignment.Center,
                    Foreground = new SolidColorBrush(Color.FromRgb(180, 150, 220))
                };
                stoxxoLegStatusText.SetBinding(TextBlock.TextProperty, new Binding("StoxxoStatusDisplay"));
                Grid.SetColumn(stoxxoLegStatusText, col++);
                legGrid.Children.Add(stoxxoLegStatusText);

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
            // Order: Leg, Symbol, Entry, LTP, SL, Exit, Time, P&L, Status, SL Sts, Stx Entry, Stx Exit, Stx Sts
            headerGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(40) });   // Leg
            headerGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(130) });  // Symbol
            headerGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(60) });   // Entry
            headerGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(60) });   // LTP
            headerGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(60) });   // SL
            headerGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(60) });   // Exit
            headerGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(60) });   // Time
            headerGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(70) });   // P&L
            headerGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(55) });   // Status
            headerGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(55) });   // SL Sts
            headerGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(60) });   // Stx Entry
            headerGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(60) });   // Stx Exit
            headerGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(60) });   // Stx Sts

            // Headers with text wrapping for compact display
            string[] headers = { "Leg", "Symbol", "Entry", "LTP", "SL", "Exit", "Time", "P&L", "Status", "SL\nSts", "Stx\nEntry", "Stx\nExit", "Stx\nSts" };
            for (int i = 0; i < headers.Length; i++)
            {
                var hdr = new TextBlock
                {
                    Text = headers[i],
                    Foreground = _fgColor,
                    FontWeight = FontWeights.Bold,
                    FontSize = 9,
                    TextWrapping = TextWrapping.Wrap,
                    LineHeight = 11
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
                    TBSLogger.LogStatusTransition(state.TrancheId, oldStatus.ToString(), state.Status.ToString(), $"StrikeLocked={state.StrikeLocked}");
                }

                // When entering Monitoring state, fetch ATM strike (if not locked)
                if (oldStatus != TBSExecutionStatus.Monitoring && state.Status == TBSExecutionStatus.Monitoring)
                {
                    TBSLogger.Info($"Tranche #{state.TrancheId} {state.Config?.EntryTime:hh\\:mm\\:ss} Entered Monitoring - fetching ATM strike");
                    UpdateStrikeForState(state);
                }

                // While in Monitoring, keep updating ATM strike (follows spot price)
                if (state.Status == TBSExecutionStatus.Monitoring && !state.StrikeLocked)
                {
                    UpdateStrikeForState(state);
                }

                // Stoxxo: Place order at 5 seconds before entry
                // For ProfitCondition tranches, evaluate condition BEFORE sending Stoxxo order
                // to avoid placing and then immediately exiting (wasteful 8 orders per skip)
                if (state.Status == TBSExecutionStatus.Monitoring && !state.StoxxoOrderPlaced && !state.SkippedDueToProfitCondition)
                {
                    var timeDiff = state.Config.EntryTime - now;

                    // At 5 seconds before entry - evaluate and send to Stoxxo
                    if (timeDiff.TotalSeconds <= 5 && timeDiff.TotalSeconds > 0)
                    {
                        // For ProfitCondition tranches, check condition BEFORE sending Stoxxo order
                        if (state.ProfitCondition && !ShouldDeployTranche(state))
                        {
                            // P&L <= 0 - skip this tranche entirely (don't send to Stoxxo)
                            TBSLogger.Warn($"Tranche #{state.TrancheId} {state.Config?.EntryTime:hh\\:mm\\:ss} ProfitCondition not met 5s before entry - skipping Stoxxo order");
                            state.SkippedDueToProfitCondition = true;
                            state.Status = TBSExecutionStatus.Skipped;
                            state.Message = "Skipped: Prior tranches P&L <= 0 (no Stoxxo order)";
                        }
                        else
                        {
                            // Condition met or no condition - proceed with Stoxxo order
                            PlaceStoxxoOrderAsync(state).SafeFireAndForget("TBSManager.PlaceStoxxo");
                        }
                    }
                }

                // When going Live, lock strike and enter positions
                // ProfitCondition is already evaluated at 5s before entry, so just proceed
                if (oldStatus == TBSExecutionStatus.Monitoring && state.Status == TBSExecutionStatus.Live)
                {
                    if (state.SkippedDueToProfitCondition)
                    {
                        // Already skipped during monitoring phase - force back to Skipped status
                        state.Status = TBSExecutionStatus.Skipped;
                    }
                    else
                    {
                        // Proceed normally - lock strike and enter positions
                        TBSLogger.Info($"Tranche #{state.TrancheId} {state.Config?.EntryTime:hh\\:mm\\:ss} Going Live - locking strike and entering positions");
                        LockStrikeAndEnter(state);
                    }
                }
                else if (state.SkippedDueToProfitCondition && state.Status == TBSExecutionStatus.Live)
                {
                    // Force back to Skipped status if it somehow went Live after being skipped
                    state.Status = TBSExecutionStatus.Skipped;
                }

                // Stoxxo: Reconcile legs 10 seconds after going Live
                if (state.Status == TBSExecutionStatus.Live && state.StoxxoOrderPlaced && !state.StoxxoReconciled)
                {
                    if (state.ActualEntryTime.HasValue)
                    {
                        var elapsed = DateTime.Now - state.ActualEntryTime.Value;
                        if (elapsed.TotalSeconds >= 10)
                        {
                            ReconcileStoxxoLegsAsync(state).SafeFireAndForget("TBSWindow.ReconcileStoxxo");
                        }
                    }
                }

                // While Live, check for SL conditions
                if (state.Status == TBSExecutionStatus.Live && state.StrikeLocked)
                {
                    CheckSLConditions(state);
                }

                // Stoxxo: Send SL modification when SL-to-cost is applied
                if (state.SLToCostApplied && !state.StoxxoSLModified && !string.IsNullOrEmpty(state.StoxxoPortfolioName))
                {
                    ModifyStoxxoSLAsync(state).SafeFireAndForget("TBSWindow.ModifyStoxxoSL");
                }

                // When going SquaredOff, record exit details
                if (oldStatus == TBSExecutionStatus.Live && state.Status == TBSExecutionStatus.SquaredOff)
                {
                    TBSLogger.Info($"Tranche #{state.TrancheId} {state.Config?.EntryTime:hh\\:mm\\:ss} Going SquaredOff - recording exit prices");
                    RecordExitPrices(state);

                    // Stoxxo: Call exit
                    if (!state.StoxxoExitCalled && !string.IsNullOrEmpty(state.StoxxoPortfolioName))
                    {
                        ExitStoxxoOrderAsync(state).SafeFireAndForget("TBSWindow.ExitStoxxo");
                    }
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

                // Debug: Log price lookup for SL checking
                if (currentPrice > 0 && Math.Abs(currentPrice - leg.CurrentPrice) > 5)
                {
                    TBSLogger.Debug($"Tranche #{state.TrancheId} {leg.OptionType} Price update: Symbol={leg.Symbol}, OldPrice={leg.CurrentPrice:F2}, NewPrice={currentPrice:F2}, Strike={state.Strike}");
                }

                if (currentPrice > 0)
                {
                    leg.CurrentPrice = currentPrice;
                }

                // Check individual leg SL (for short position: price goes UP above SL)
                if (leg.SLPrice > 0 && leg.CurrentPrice >= leg.SLPrice)
                {
                    // SL Hit! Log detailed info for debugging
                    TBSLogger.Warn($"Tranche #{state.TrancheId} {leg.OptionType} SL TRIGGERED: Symbol={leg.Symbol}, Entry={leg.EntryPrice:F2}, SL={leg.SLPrice:F2}, CurrentPrice={leg.CurrentPrice:F2}, LockedStrike={state.Strike}");

                    leg.Status = TBSLegStatus.SLHit;
                    leg.ExitPrice = leg.CurrentPrice;
                    leg.ExitTime = DateTime.Now;
                    leg.ExitReason = $"SL Hit @ {leg.CurrentPrice:F2}";
                    anyLegHitSLThisTick = true;
                    TBSLogger.Warn($"Tranche #{state.TrancheId} {leg.OptionType} SL HIT: Entry={leg.EntryPrice:F2}, SL={leg.SLPrice:F2}, Exit={leg.ExitPrice:F2}");
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
                    TBSLogger.Info($"Tranche #{state.TrancheId} HEDGE_TO_COST: {leg.OptionType} SL moved from {oldSL:F2} to cost {leg.SLPrice:F2}");
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
                        TBSLogger.Warn($"Tranche #{state.TrancheId} Combined SL HIT: EntryPremium={entryPremium:F2}, CurrentPremium={currentPremium:F2}, SL={combinedSLPrice:F2}");
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

            // Check Target% - ONLY when BOTH legs are still Active
            // Target is based on combined premium decay (short straddle profits when premium drops)
            if (state.TargetPercent > 0 && state.TargetProfitThreshold > 0 && !state.TargetHit)
            {
                // Count active legs - target only applies if BOTH legs are still open
                int activeLegsCount = state.Legs.Count(l => l.Status == TBSLegStatus.Active);

                if (activeLegsCount == 2) // Both legs still active
                {
                    // For short straddle: profit when combined premium drops
                    // Current P&L = (entry premium - current premium) * qty * lot size
                    // This is already calculated in UpdateCombinedPnL as leg.PnL sum
                    state.UpdateCombinedPnL();

                    if (state.CombinedPnL >= state.TargetProfitThreshold)
                    {
                        // Target hit! Exit both legs
                        TBSLogger.Info($"Tranche #{state.TrancheId} TARGET HIT: P&L={state.CombinedPnL:F2} >= Threshold={state.TargetProfitThreshold:F2}");
                        foreach (var leg in state.Legs.Where(l => l.Status == TBSLegStatus.Active))
                        {
                            leg.Status = TBSLegStatus.TargetHit;
                            leg.ExitPrice = leg.CurrentPrice;
                            leg.ExitTime = DateTime.Now;
                            leg.ExitReason = $"Target Hit @ P&L={state.CombinedPnL:F2}";
                        }
                        state.TargetHit = true;
                        state.Message = $"Target hit @ P&L {state.CombinedPnL:F2}";

                        // Stoxxo: Immediately send exit when target is hit
                        if (!state.StoxxoExitCalled && !string.IsNullOrEmpty(state.StoxxoPortfolioName))
                        {
                            ExitStoxxoOrderAsync(state).SafeFireAndForget("TBSManager.ExitStoxxo");
                        }
                    }
                }
                // If only 1 leg is active (other hit SL), we do NOT exit on target
                // Let it run to SL or exit time
            }

            // If all legs are closed, mark state as SquaredOff
            if (state.AllLegsClosed())
            {
                state.Status = TBSExecutionStatus.SquaredOff;
                if (state.TargetHit)
                    state.Message = "All legs exited (Target)";
                else
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

                        TBSLogger.LogLegState(state.TrancheId, leg.OptionType, leg.Symbol, leg.EntryPrice, exitPrice, leg.SLPrice, "Exited");
                    }
                }
            }
        }

        /// <summary>
        /// Determine if a tranche should be deployed based on profit condition.
        /// First tranche always deploys. Subsequent tranches with ProfitCondition=true
        /// only deploy if cumulative P&L of all prior deployed tranches > 0.
        /// </summary>
        private bool ShouldDeployTranche(TBSExecutionState state)
        {
            // If profit condition is false, always deploy
            if (!state.ProfitCondition)
            {
                TBSLogger.LogProfitConditionCheck(state.TrancheId, false, 0, true);
                return true;
            }

            // Get all tranches with earlier entry times that have been deployed (sent to Stoxxo or went Live)
            // Exclude: skipped tranches, missed tranches (no Stoxxo order, no P&L contribution)
            var priorDeployedTranches = _executionRows
                .Where(t => t.TrancheId < state.TrancheId)  // Earlier tranches by ID
                .Where(t => t.StoxxoOrderPlaced || t.Status == TBSExecutionStatus.Live || t.Status == TBSExecutionStatus.SquaredOff)
                .Where(t => !t.SkippedDueToProfitCondition)  // Don't count skipped tranches
                .Where(t => !t.IsMissed)  // Don't count missed tranches (no Stoxxo order placed)
                .ToList();

            // If no prior tranches deployed, this is effectively the first - always deploy
            if (priorDeployedTranches.Count == 0)
            {
                TBSLogger.Info($"Tranche #{state.TrancheId} - No prior deployed tranches, deploying as first");
                return true;
            }

            // Calculate cumulative P&L of all prior deployed tranches
            decimal cumulativePnL = priorDeployedTranches.Sum(t => t.CombinedPnL);

            TBSLogger.LogTrancheState(state.TrancheId, state.Config?.Underlying ?? "?",
                "ProfitConditionCheck", state.CombinedPnL, cumulativePnL,
                $"PriorTranches={priorDeployedTranches.Count}");

            // Only deploy if cumulative P&L > 0
            if (cumulativePnL > 0)
            {
                TBSLogger.LogProfitConditionCheck(state.TrancheId, true, cumulativePnL, true);
                return true;
            }
            else
            {
                TBSLogger.LogProfitConditionCheck(state.TrancheId, true, cumulativePnL, false);
                return false;
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

                // Use cached expiry and isMonthlyExpiry from MarketAnalyzerLogic (single source of truth)
                var expiry = MarketAnalyzerLogic.Instance.SelectedExpiry ?? _selectedExpiry ?? FindNearestExpiry(underlying, state.Config?.DTE ?? 0);
                if (expiry.HasValue)
                {
                    // Use cached isMonthlyExpiry from MarketAnalyzerLogic, or calculate it
                    bool isMonthlyExpiry = MarketAnalyzerLogic.Instance.SelectedIsMonthlyExpiry;

                    // If we don't have cached info (e.g., different underlying), recalculate
                    if (MarketAnalyzerLogic.Instance.SelectedUnderlying != underlying)
                    {
                        var expiries = MarketAnalyzerLogic.Instance.GetCachedExpiries(underlying);
                        isMonthlyExpiry = SymbolHelper.IsMonthlyExpiry(expiry.Value, expiries);
                    }

                    // Use shared SymbolHelper for symbol generation (single source of truth)
                    string ceSymbol = SymbolHelper.BuildOptionSymbol(underlying, expiry.Value, atmStrike, "CE", isMonthlyExpiry);
                    string peSymbol = SymbolHelper.BuildOptionSymbol(underlying, expiry.Value, atmStrike, "PE", isMonthlyExpiry);

                    var ceLeg = state.Legs.FirstOrDefault(l => l.OptionType == "CE");
                    var peLeg = state.Legs.FirstOrDefault(l => l.OptionType == "PE");

                    if (ceLeg != null) ceLeg.Symbol = ceSymbol;
                    if (peLeg != null) peLeg.Symbol = peSymbol;

                    TBSLogger.Debug($"Tranche #{state.TrancheId} Symbol update: {underlying} strike={atmStrike}, expiry={expiry.Value:yyyy-MM-dd}, isMonthly={isMonthlyExpiry}, CE={ceSymbol}, PE={peSymbol}");
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
                // Log the exact symbol being locked for debugging
                TBSLogger.Info($"Tranche #{state.TrancheId} {leg.OptionType} LOCK: Symbol={leg.Symbol}, Strike={state.Strike}");

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
                        TBSLogger.Debug($"Tranche #{state.TrancheId} {leg.OptionType} SL set: Entry={leg.EntryPrice:F2}, SL%={state.IndividualSLPercent:P0}, SLPrice={leg.SLPrice:F2}");
                    }
                }
                else
                {
                    TBSLogger.Warn($"Tranche #{state.TrancheId} {leg.OptionType} NO PRICE at lock time for symbol={leg.Symbol}");
                }
            }

            // Calculate combined entry premium and target threshold
            // Target only applies when both legs are still open
            var ceLeg = state.Legs.FirstOrDefault(l => l.OptionType == "CE");
            var peLeg = state.Legs.FirstOrDefault(l => l.OptionType == "PE");
            if (ceLeg != null && peLeg != null && ceLeg.EntryPrice > 0 && peLeg.EntryPrice > 0)
            {
                state.CombinedEntryPremium = ceLeg.EntryPrice + peLeg.EntryPrice;

                // Target profit threshold = combined premium * target% * qty * lot size
                // This is the P&L value at which we exit both legs if both are still open
                if (state.TargetPercent > 0)
                {
                    state.TargetProfitThreshold = state.CombinedEntryPremium * state.TargetPercent * state.Quantity * state.LotSize;
                    TBSLogger.Info($"Tranche #{state.TrancheId} Target set: CombinedPremium={state.CombinedEntryPremium:F2}, Target%={state.TargetPercent:P0}, Threshold={state.TargetProfitThreshold:F2}");
                }
            }

            TBSLogger.Info($"Tranche #{state.TrancheId} Locked strike {state.Strike} for {state.Config?.Underlying} at {state.Config?.EntryTime}, LotSize={state.LotSize}, Qty={state.Quantity}");
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

        #region Stoxxo Integration

        /// <summary>
        /// Start Stoxxo polling timer (every 5 seconds for status/MTM updates)
        /// </summary>
        private void StartStoxxoPollingTimer()
        {
            _stoxxoPollingTimer = new DispatcherTimer
            {
                Interval = TimeSpan.FromSeconds(5)
            };
            _stoxxoPollingTimer.Tick += OnStoxxoPollingTimerTick;
            _stoxxoPollingTimer.Start();
            TBSLogger.Info("Stoxxo polling timer started (5s interval)");
        }

        private void StopStoxxoPollingTimer()
        {
            if (_stoxxoPollingTimer != null)
            {
                _stoxxoPollingTimer.Stop();
                _stoxxoPollingTimer.Tick -= OnStoxxoPollingTimerTick;
                _stoxxoPollingTimer = null;
            }
        }

        /// <summary>
        /// Poll Stoxxo for status and MTM updates on active portfolios
        /// </summary>
        private void OnStoxxoPollingTimerTick(object sender, EventArgs e)
        {
            PollStoxxoPortfoliosAsync().SafeFireAndForget("TBSManager.PollStoxxo");
        }

        private async Task PollStoxxoPortfoliosAsync()
        {
            try
            {
                foreach (var state in _executionRows.Where(s => !string.IsNullOrEmpty(s.StoxxoPortfolioName)))
                {
                    // Only poll for Live or recently SquaredOff tranches
                    if (state.Status != TBSExecutionStatus.Live && state.Status != TBSExecutionStatus.SquaredOff)
                        continue;

                    try
                    {
                        // Get portfolio status (enum -> string)
                        var status = await _stoxxoService.GetPortfolioStatus(state.StoxxoPortfolioName);
                        if (status != StoxxoPortfolioStatus.Unknown)
                        {
                            state.StoxxoStatus = status.ToString();
                        }

                        // Get portfolio MTM (P&L)
                        var mtm = await _stoxxoService.GetPortfolioMTM(state.StoxxoPortfolioName);
                        state.StoxxoPnL = mtm;

                        // Update leg details if we have reconciled
                        if (state.StoxxoReconciled)
                        {
                            await UpdateStoxxoLegDetails(state);
                        }
                    }
                    catch (Exception ex)
                    {
                        TBSLogger.Debug($"Tranche #{state.TrancheId} Stoxxo poll error for {state.StoxxoPortfolioName}: {ex.Message}");
                        // Continue with other tranches - don't block on errors
                    }
                }
            }
            catch (Exception ex)
            {
                TBSLogger.Error($"Stoxxo polling error: {ex.Message}");
            }
        }

        /// <summary>
        /// Place Stoxxo order 5 seconds before entry time
        /// </summary>
        private async Task PlaceStoxxoOrderAsync(TBSExecutionState state)
        {
            try
            {
                state.StoxxoOrderPlaced = true; // Mark as placed to prevent duplicate calls

                var underlying = state.Config?.Underlying ?? "NIFTY";
                var portfolioName = _stoxxoService.Config.GetPortfolioName(underlying);
                int lots = state.Quantity;

                // Combined SL as percentage string (e.g., "50P" for 50%)
                string combinedLoss = state.CombinedSLPercent > 0
                    ? StoxxoHelper.FormatPercentage(state.CombinedSLPercent * 100)
                    : "0";

                // Individual leg SL as percentage string (for LegSL parameter - fallback if no PortLegs)
                string legSL = state.IndividualSLPercent > 0
                    ? StoxxoHelper.FormatPercentage(state.IndividualSLPercent * 100)
                    : "0";

                // SL-to-cost: 1 if hedge_to_cost is configured, 0 otherwise
                int slToCost = state.HedgeAction?.Contains("hedge_to_cost") == true ? 1 : 0;

                // Entry time in seconds from midnight (tranche-specific time)
                int startSeconds = StoxxoHelper.TimeSpanToSeconds(state.Config.EntryTime);

                // Build PortLegs with leg-wise details: ATM strike, PE first, CE second, with tranche SL%
                // Format: Strike:ATM|Txn:SELL|Ins:PE|Lots:2|SL:Premium:50P||Strike:ATM|Txn:SELL|Ins:CE|Lots:2|SL:Premium:50P
                string portLegs = StoxxoHelper.BuildPortLegs(
                    lots: lots,
                    slPercent: state.IndividualSLPercent,
                    strike: "ATM"
                );

                TBSLogger.LogStoxxoCall("PlaceMultiLegOrderAdv", $"{underlying}_T{state.TrancheId}",
                    $"Lots={lots}, CombSL={combinedLoss}, LegSL={legSL}, SLToCost={slToCost}, StartSec={startSeconds}",
                    "pending...");

                // PlaceMultiLegOrderAdv with PortLegs for leg-wise SL
                var result = await _stoxxoService.PlaceMultiLegOrderAdv(
                    underlying,
                    lots,
                    combinedLoss,
                    legSL,
                    slToCost,
                    startSeconds,
                    endSeconds: 0,
                    sqOffSeconds: 0,
                    portLegs: portLegs);

                if (!string.IsNullOrEmpty(result))
                {
                    state.StoxxoPortfolioName = result;
                    state.Message = $"Stoxxo order placed: {result}";
                    TBSLogger.Info($"Tranche #{state.TrancheId} Stoxxo order placed successfully: {result}");
                }
                else
                {
                    state.Message = "Stoxxo order failed";
                    TBSLogger.Warn($"Tranche #{state.TrancheId} Stoxxo order returned empty result");
                }
            }
            catch (Exception ex)
            {
                TBSLogger.Error($"Tranche #{state.TrancheId} PlaceStoxxoOrderAsync error: {ex.Message}");
                state.Message = $"Stoxxo error: {ex.Message}";
                // Don't reset StoxxoOrderPlaced - we tried, don't retry
            }
        }

        /// <summary>
        /// Reconcile Stoxxo legs with internal legs 10 seconds after going Live
        /// </summary>
        private async Task ReconcileStoxxoLegsAsync(TBSExecutionState state)
        {
            try
            {
                state.StoxxoReconciled = true; // Mark to prevent duplicate calls

                var legs = await _stoxxoService.GetUserLegs(state.StoxxoPortfolioName, onlyActiveLegs: true);
                if (legs == null || legs.Count == 0)
                {
                    TBSLogger.Warn($"Tranche #{state.TrancheId} No Stoxxo legs returned for {state.StoxxoPortfolioName}");
                    return;
                }

                MapStoxxoLegsToInternal(state, legs);
                TBSLogger.Info($"Tranche #{state.TrancheId} Reconciled {legs.Count} Stoxxo legs for {state.StoxxoPortfolioName}");
            }
            catch (Exception ex)
            {
                TBSLogger.Error($"Tranche #{state.TrancheId} ReconcileStoxxoLegsAsync error: {ex.Message}");
                // Don't reset StoxxoReconciled - we tried
            }
        }

        /// <summary>
        /// Map Stoxxo legs to internal TBS legs (SELL legs only, by CE/PE type)
        /// </summary>
        private void MapStoxxoLegsToInternal(TBSExecutionState state, List<StoxxoUserLeg> stoxxoLegs)
        {
            // Log all received legs for debugging
            TBSLogger.Info($"Tranche #{state.TrancheId} Stoxxo returned {stoxxoLegs.Count} legs total");
            foreach (var sl in stoxxoLegs)
            {
                TBSLogger.Info($"  - LegID={sl.LegID}, Ins={sl.Instrument}, Txn={sl.Txn}, Entry={sl.AvgEntryPrice:F2}, Status={sl.Status}");
            }

            // Filter to SELL legs only (straddle legs, not hedges)
            var sellLegs = stoxxoLegs.Where(l => l.Txn?.Equals("Sell", StringComparison.OrdinalIgnoreCase) == true).ToList();
            TBSLogger.Info($"Tranche #{state.TrancheId} Found {sellLegs.Count} SELL legs");

            // Match by instrument type
            var ceLeg = sellLegs.FirstOrDefault(l => l.Instrument?.Equals("CE", StringComparison.OrdinalIgnoreCase) == true);
            var peLeg = sellLegs.FirstOrDefault(l => l.Instrument?.Equals("PE", StringComparison.OrdinalIgnoreCase) == true);

            // Assign to our internal legs
            foreach (var leg in state.Legs)
            {
                var stoxxoLeg = leg.OptionType == "CE" ? ceLeg : peLeg;
                if (stoxxoLeg != null)
                {
                    leg.StoxxoLegID = stoxxoLeg.LegID;
                    leg.StoxxoQty = stoxxoLeg.EntryFilledQty;
                    leg.StoxxoEntryPrice = stoxxoLeg.AvgEntryPrice;
                    leg.StoxxoExitPrice = stoxxoLeg.AvgExitPrice;
                    leg.StoxxoStatus = stoxxoLeg.Status;

                    TBSLogger.Info($"Tranche #{state.TrancheId} Mapped {leg.OptionType} leg: LegID={stoxxoLeg.LegID}, Entry={stoxxoLeg.AvgEntryPrice:F2}, Qty={stoxxoLeg.EntryFilledQty}, Status={stoxxoLeg.Status}");
                }
                else
                {
                    TBSLogger.Warn($"Tranche #{state.TrancheId} No Stoxxo {leg.OptionType} SELL leg found to map");
                }
            }
        }

        /// <summary>
        /// Update Stoxxo leg details from GetUserLegs
        /// </summary>
        private async Task UpdateStoxxoLegDetails(TBSExecutionState state)
        {
            try
            {
                var legs = await _stoxxoService.GetUserLegs(state.StoxxoPortfolioName, onlyActiveLegs: false);
                if (legs == null || legs.Count == 0)
                    return;

                // Update existing mapped legs
                var sellLegs = legs.Where(l => l.Txn?.Equals("Sell", StringComparison.OrdinalIgnoreCase) == true).ToList();

                foreach (var leg in state.Legs)
                {
                    var stoxxoLeg = sellLegs.FirstOrDefault(l =>
                        l.Instrument?.Equals(leg.OptionType, StringComparison.OrdinalIgnoreCase) == true);

                    if (stoxxoLeg != null)
                    {
                        leg.StoxxoEntryPrice = stoxxoLeg.AvgEntryPrice;
                        leg.StoxxoExitPrice = stoxxoLeg.AvgExitPrice;
                        leg.StoxxoStatus = stoxxoLeg.Status;
                    }
                }
            }
            catch (Exception ex)
            {
                TBSLogger.Debug($"Tranche #{state.TrancheId} UpdateStoxxoLegDetails error: {ex.Message}");
            }
        }

        /// <summary>
        /// Modify Stoxxo SL when SL-to-cost is triggered
        /// Sends the actual entry price as the new SL value (not percentage)
        /// </summary>
        private async Task ModifyStoxxoSLAsync(TBSExecutionState state)
        {
            try
            {
                state.StoxxoSLModified = true; // Mark to prevent duplicate calls

                // Find the leg that is still active (the one we're moving SL to cost for)
                foreach (var leg in state.Legs.Where(l => l.Status == TBSLegStatus.Active))
                {
                    // Send the entry price as the new SL value
                    // Format: ModifyPortfolio(portfolio, "LegSL", "entry_price", "INS:CE" or "INS:PE")
                    string legFilter = $"INS:{leg.OptionType}";
                    string slValue = leg.EntryPrice.ToString("F2");

                    TBSLogger.Info($"Tranche #{state.TrancheId} Modifying Stoxxo SL for {leg.OptionType}: SL={slValue} (entry price)");

                    var success = await _stoxxoService.ModifyPortfolio(
                        state.StoxxoPortfolioName,
                        "LegSL",
                        slValue,
                        legFilter);

                    if (success)
                    {
                        TBSLogger.Info($"Tranche #{state.TrancheId} Stoxxo SL modified successfully for {leg.OptionType}");
                    }
                    else
                    {
                        TBSLogger.Warn($"Tranche #{state.TrancheId} Stoxxo SL modification failed for {leg.OptionType}");
                    }
                }
            }
            catch (Exception ex)
            {
                TBSLogger.Error($"Tranche #{state.TrancheId} ModifyStoxxoSLAsync error: {ex.Message}");
            }
        }

        /// <summary>
        /// Exit Stoxxo portfolio when squaring off
        /// </summary>
        private async Task ExitStoxxoOrderAsync(TBSExecutionState state)
        {
            try
            {
                state.StoxxoExitCalled = true; // Mark to prevent duplicate calls

                TBSLogger.Info($"Tranche #{state.TrancheId} Exiting Stoxxo portfolio: {state.StoxxoPortfolioName}");

                var success = await _stoxxoService.ExitMultiLegOrder(state.StoxxoPortfolioName);

                if (success)
                {
                    state.Message = "Stoxxo exit sent";
                    TBSLogger.Info($"Tranche #{state.TrancheId} Stoxxo exit successful for {state.StoxxoPortfolioName}");
                }
                else
                {
                    state.Message = "Stoxxo exit failed";
                    TBSLogger.Warn($"Tranche #{state.TrancheId} Stoxxo exit failed for {state.StoxxoPortfolioName}");
                }
            }
            catch (Exception ex)
            {
                TBSLogger.Error($"Tranche #{state.TrancheId} ExitStoxxoOrderAsync error: {ex.Message}");
                state.Message = $"Stoxxo exit error: {ex.Message}";
            }
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

            // Subscribe to PriceSyncReady for event-driven initialization (replaces 45s delay)
            MarketAnalyzerLogic.Instance.PriceSyncReady += OnPriceSyncReady;
        }

        private void UnsubscribeFromEvents()
        {
            MarketAnalyzerLogic.Instance.PriceUpdated -= OnPriceHubUpdated;
            MarketAnalyzerLogic.Instance.OptionsGenerated -= OnOptionsGenerated;
            MarketAnalyzerLogic.Instance.PriceSyncReady -= OnPriceSyncReady;
        }

        /// <summary>
        /// Called when enough option prices have been received (event-driven init).
        /// This replaces the hardcoded 45-second delay timer.
        /// </summary>
        private void OnPriceSyncReady(string underlying, int priceCount)
        {
            if (_optionChainReady) return; // Already ready

            TBSLogger.Info($"[TBSManagerTabPage] PriceSyncReady: {underlying} with {priceCount} prices - initializing immediately");

            Dispatcher.InvokeAsync(() =>
            {
                // Stop the delay timer if running (no longer needed)
                StopOptionChainDelayTimer();

                // Mark as ready
                _optionChainReady = true;
                _delayCountdown = 0;

                // Initialize execution states immediately
                InitializeExecutionStates();

                _lblConfigStatus.Text = $"Option Chain ready - {_selectedUnderlying ?? underlying} (via price sync)";
                TBSLogger.Info("[TBSManagerTabPage] Execution states initialized via PriceSyncReady event");
            });
        }

        /// <summary>
        /// Called when Option Chain generates options - auto-derives underlying and DTE for filtering.
        /// Starts a fallback delay timer in case PriceSyncReady event doesn't fire.
        /// Primary initialization now happens via PriceSyncReady event (event-driven).
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
                TBSLogger.Warn("OnOptionsGenerated: Missing underlying or expiry");
                return;
            }

            // Calculate DTE
            int dte = (int)(expiry.Value.Date - DateTime.Today).TotalDays;
            if (dte < 0) dte = 0;

            TBSLogger.Info($"OnOptionsGenerated: Auto-derived underlying={underlying}, expiry={expiry.Value:dd-MMM-yyyy}, DTE={dte}");

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

                    // Start fallback delay timer if not already running and PriceSyncReady hasn't fired
                    if (_optionChainDelayTimer == null && !_optionChainReady)
                    {
                        StartOptionChainDelayTimer();
                        _lblConfigStatus.Text = $"Auto-filtered for {underlying} DTE={dte} - Waiting for price data...";
                    }
                    else
                    {
                        _lblConfigStatus.Text = $"Auto-filtered for {underlying} DTE={dte} (Expiry: {expiry.Value:dd-MMM-yyyy})";
                    }

                    TBSLogger.Info($"OnOptionsGenerated: Applied auto-filter, showing {_configRows.Count} configs");
                }
                catch (Exception ex)
                {
                    TBSLogger.Error($"OnOptionsGenerated: Error applying filter - {ex.Message}");
                }
            });
        }

        /// <summary>
        /// Start a fallback delay timer (45s) in case PriceSyncReady event doesn't fire.
        /// This is a safety net - normally PriceSyncReady will fire much sooner.
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

            TBSLogger.Info($"Started {OPTION_CHAIN_DELAY_SECONDS}s delay timer for Option Chain loading");

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

                TBSLogger.Info("Option Chain delay complete - initializing execution states");

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
                    TBSLogger.Debug($"Option Chain delay: {_delayCountdown}s remaining");
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
                    TBSLogger.Debug($"OnPriceHubUpdated error: {ex.Message}");
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
            StopStoxxoPollingTimer();
            UnsubscribeFromEvents();
            UnsubscribeFromViewModelEvents();

            // Cleanup ViewModel (stops execution, unsubscribes from service events)
            _viewModel?.Cleanup();

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
