using System;
using System.ComponentModel;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using System.Xml.Linq;
using NinjaTrader.Cbi;
using NinjaTrader.Gui.Tools;
using ZerodhaDatafeedAdapter.Models;
using ZerodhaDatafeedAdapter.Services;
using ZerodhaDatafeedAdapter.Helpers;

namespace ZerodhaDatafeedAdapter.AddOns.SimulationEngine
{
    #region Value Converters

    /// <summary>
    /// Converts SimulationState to color
    /// </summary>
    public class SimStateToColorConverter : IValueConverter
    {
        private static readonly SolidColorBrush IdleBrush = new SolidColorBrush(Color.FromRgb(150, 150, 150));
        private static readonly SolidColorBrush LoadingBrush = new SolidColorBrush(Color.FromRgb(200, 180, 80));
        private static readonly SolidColorBrush ReadyBrush = new SolidColorBrush(Color.FromRgb(100, 150, 220));
        private static readonly SolidColorBrush PlayingBrush = new SolidColorBrush(Color.FromRgb(100, 200, 100));
        private static readonly SolidColorBrush PausedBrush = new SolidColorBrush(Color.FromRgb(220, 180, 50));
        private static readonly SolidColorBrush CompletedBrush = new SolidColorBrush(Color.FromRgb(100, 200, 100));
        private static readonly SolidColorBrush ErrorBrush = new SolidColorBrush(Color.FromRgb(220, 80, 80));

        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            if (value is SimulationState state)
            {
                switch (state)
                {
                    case SimulationState.Idle: return IdleBrush;
                    case SimulationState.Loading: return LoadingBrush;
                    case SimulationState.Ready: return ReadyBrush;
                    case SimulationState.Playing: return PlayingBrush;
                    case SimulationState.Paused: return PausedBrush;
                    case SimulationState.Completed: return CompletedBrush;
                    case SimulationState.Error: return ErrorBrush;
                }
            }
            return IdleBrush;
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        {
            throw new NotImplementedException();
        }
    }

    #endregion

    /// <summary>
    /// Simulation Engine Window - replays historical tick data for offline development
    /// </summary>
    public class SimulationEngineWindow : NTWindow, IWorkspacePersistence
    {
        public SimulationEngineWindow()
        {
            Logger.Info("[SimulationEngineWindow] Constructor: Creating window");

            Caption = "Simulation Engine";
            Width = 500;
            Height = 450;

            // Create the tab control
            TabControl tabControl = new TabControl();
            tabControl.Style = Application.Current.TryFindResource("TabControlStyle") as Style;

            // Create and add our tab page
            SimulationEngineTabPage tabPage = new SimulationEngineTabPage();
            tabControl.Items.Add(tabPage);

            Content = tabControl;

            Logger.Info("[SimulationEngineWindow] Constructor: Window created");
        }

        public void Restore(XDocument document, XElement element)
        {
            Logger.Debug("[SimulationEngineWindow] Restore: Called");
        }

        public void Save(XDocument document, XElement element)
        {
            Logger.Debug("[SimulationEngineWindow] Save: Called");
        }

        public WorkspaceOptions WorkspaceOptions { get; set; }
    }

    /// <summary>
    /// Simulation Engine Tab Page with all controls
    /// </summary>
    public class SimulationEngineTabPage : NTTabPage
    {
        // UI Elements - Configuration
        private DatePicker _datePicker;
        private TextBox _txtTimeFrom;
        private TextBox _txtTimeTo;
        private ComboBox _cboUnderlying;
        private DatePicker _expiryPicker;
        private TextBox _txtProjectedOpen;
        private TextBox _txtStepSize;
        private TextBox _txtStrikeCount;
        private TextBox _txtSymbolPrefix;
        private TextBlock _lblSymbolPreview;
        private Button _btnLoad;

        // UI Elements - Playback Controls
        private Button _btnPlay;
        private Button _btnPause;
        private Button _btnStop;
        private ComboBox _cboSpeed;

        // UI Elements - Status
        private TextBlock _lblState;
        private TextBlock _lblStatus;
        private TextBlock _lblCurrentTime;
        private ProgressBar _progressBar;
        private TextBlock _lblSymbolsLoaded;
        private TextBlock _lblTicksLoaded;
        private TextBlock _lblPricesInjected;

        // Configuration
        private SimulationConfig _config;

        // Event handler reference for proper cleanup (prevents memory leak)
        private PropertyChangedEventHandler _servicePropertyChangedHandler;

        // NinjaTrader-style colors
        private static readonly SolidColorBrush _bgColor = new SolidColorBrush(Color.FromRgb(27, 27, 28));
        private static readonly SolidColorBrush _fgColor = new SolidColorBrush(Color.FromRgb(212, 212, 212));
        private static readonly SolidColorBrush _headerBg = new SolidColorBrush(Color.FromRgb(37, 37, 38));
        private static readonly SolidColorBrush _borderColor = new SolidColorBrush(Color.FromRgb(51, 51, 51));
        private static readonly SolidColorBrush _accentColor = new SolidColorBrush(Color.FromRgb(0, 122, 204));
        private static readonly FontFamily _ntFont = new FontFamily("Segoe UI");

        public SimulationEngineTabPage()
        {
            try
            {
                Background = _bgColor;
                _config = new SimulationConfig();

                BuildUI();
                BindToService();

                Logger.Info("[SimulationEngineTabPage] Initialized");
            }
            catch (Exception ex)
            {
                Logger.Error($"[SimulationEngineTabPage] Constructor error: {ex.Message}\n{ex.StackTrace}", ex);
                throw;
            }
        }

        private void BuildUI()
        {
            var mainPanel = new DockPanel { Background = _bgColor, Margin = new Thickness(10) };

            // Header
            var header = CreateHeader();
            DockPanel.SetDock(header, Dock.Top);
            mainPanel.Children.Add(header);

            // Configuration Section
            var configSection = CreateConfigSection();
            DockPanel.SetDock(configSection, Dock.Top);
            mainPanel.Children.Add(configSection);

            // Playback Controls Section
            var playbackSection = CreatePlaybackSection();
            DockPanel.SetDock(playbackSection, Dock.Top);
            mainPanel.Children.Add(playbackSection);

            // Status Section
            var statusSection = CreateStatusSection();
            DockPanel.SetDock(statusSection, Dock.Top);
            mainPanel.Children.Add(statusSection);

            Content = mainPanel;
        }

        private Border CreateHeader()
        {
            var border = new Border
            {
                Background = _headerBg,
                BorderBrush = _borderColor,
                BorderThickness = new Thickness(1),
                Padding = new Thickness(10, 8, 10, 8),
                Margin = new Thickness(0, 0, 0, 10)
            };

            var headerText = new TextBlock
            {
                Text = "Historical Data Replay",
                FontFamily = _ntFont,
                FontSize = 14,
                FontWeight = FontWeights.SemiBold,
                Foreground = _fgColor
            };

            border.Child = headerText;
            return border;
        }

        private Border CreateConfigSection()
        {
            var border = new Border
            {
                Background = _headerBg,
                BorderBrush = _borderColor,
                BorderThickness = new Thickness(1),
                Padding = new Thickness(10),
                Margin = new Thickness(0, 0, 0, 10)
            };

            var grid = new Grid();
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(110) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(60) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

            // Row definitions (8 rows now)
            for (int i = 0; i < 8; i++)
            {
                grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(32) });
            }

            // Row 0: Date
            AddLabel(grid, "Date:", 0, 0);
            _datePicker = CreateStyledDatePicker(DateTime.Today.AddDays(-1));
            Grid.SetRow(_datePicker, 0);
            Grid.SetColumn(_datePicker, 1);
            Grid.SetColumnSpan(_datePicker, 3);
            grid.Children.Add(_datePicker);

            // Row 1: Time From/To
            AddLabel(grid, "Time From:", 1, 0);
            _txtTimeFrom = CreateTextBox("09:15");
            Grid.SetRow(_txtTimeFrom, 1);
            Grid.SetColumn(_txtTimeFrom, 1);
            grid.Children.Add(_txtTimeFrom);

            AddLabel(grid, "To:", 1, 2);
            _txtTimeTo = CreateTextBox("09:30");
            Grid.SetRow(_txtTimeTo, 1);
            Grid.SetColumn(_txtTimeTo, 3);
            grid.Children.Add(_txtTimeTo);

            // Row 2: Underlying and Expiry
            AddLabel(grid, "Underlying:", 2, 0);
            _cboUnderlying = new ComboBox
            {
                Background = _bgColor,
                Foreground = _fgColor,
                FontFamily = _ntFont,
                Margin = new Thickness(5, 2, 5, 2)
            };
            _cboUnderlying.Items.Add("NIFTY");
            _cboUnderlying.Items.Add("SENSEX");
            _cboUnderlying.SelectedIndex = 0;
            Grid.SetRow(_cboUnderlying, 2);
            Grid.SetColumn(_cboUnderlying, 1);
            grid.Children.Add(_cboUnderlying);

            AddLabel(grid, "Expiry:", 2, 2);
            _expiryPicker = CreateStyledDatePicker(DateTime.Today);
            Grid.SetRow(_expiryPicker, 2);
            Grid.SetColumn(_expiryPicker, 3);
            grid.Children.Add(_expiryPicker);

            // Row 3: Projected Open
            AddLabel(grid, "Projected Open:", 3, 0);
            _txtProjectedOpen = CreateTextBox("24000");
            _txtProjectedOpen.TextChanged += OnSymbolParamsChanged;
            Grid.SetRow(_txtProjectedOpen, 3);
            Grid.SetColumn(_txtProjectedOpen, 1);
            grid.Children.Add(_txtProjectedOpen);

            // Row 4: Step Size and Strike Count
            AddLabel(grid, "Step Size:", 4, 0);
            _txtStepSize = CreateTextBox("50");
            _txtStepSize.Width = 60;
            _txtStepSize.TextChanged += OnSymbolParamsChanged;
            Grid.SetRow(_txtStepSize, 4);
            Grid.SetColumn(_txtStepSize, 1);
            grid.Children.Add(_txtStepSize);

            AddLabel(grid, "Strikes:", 4, 2);
            _txtStrikeCount = CreateTextBox("5");
            _txtStrikeCount.Width = 60;
            _txtStrikeCount.TextChanged += OnSymbolParamsChanged;
            Grid.SetRow(_txtStrikeCount, 4);
            Grid.SetColumn(_txtStrikeCount, 3);
            grid.Children.Add(_txtStrikeCount);

            // Row 5: Symbol Prefix (for DB lookup)
            AddLabel(grid, "Symbol Prefix:", 5, 0);
            _txtSymbolPrefix = CreateTextBox("");
            _txtSymbolPrefix.TextChanged += OnSymbolParamsChanged;
            Grid.SetRow(_txtSymbolPrefix, 5);
            Grid.SetColumn(_txtSymbolPrefix, 1);
            Grid.SetColumnSpan(_txtSymbolPrefix, 3);
            grid.Children.Add(_txtSymbolPrefix);

            // Row 6: Symbol preview (shows calculated ATM and sample symbol)
            AddLabel(grid, "Preview:", 6, 0);
            _lblSymbolPreview = new TextBlock
            {
                Text = "ATM: 24000, Symbols: 22",
                Foreground = new SolidColorBrush(Color.FromRgb(150, 200, 150)),
                FontFamily = _ntFont,
                FontSize = 11,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(5, 0, 0, 0)
            };
            Grid.SetRow(_lblSymbolPreview, 6);
            Grid.SetColumn(_lblSymbolPreview, 1);
            Grid.SetColumnSpan(_lblSymbolPreview, 3);
            grid.Children.Add(_lblSymbolPreview);
            UpdateSymbolPreview();

            // Row 7: Load Button
            _btnLoad = new Button
            {
                Content = "Load Historical Data",
                Background = _accentColor,
                Foreground = Brushes.White,
                FontFamily = _ntFont,
                FontWeight = FontWeights.SemiBold,
                Padding = new Thickness(20, 5, 20, 5),
                Margin = new Thickness(5, 5, 5, 5),
                Cursor = Cursors.Hand
            };
            _btnLoad.Click += OnLoadClick;
            Grid.SetRow(_btnLoad, 7);
            Grid.SetColumn(_btnLoad, 0);
            Grid.SetColumnSpan(_btnLoad, 4);
            grid.Children.Add(_btnLoad);

            border.Child = grid;
            return border;
        }

        private Border CreatePlaybackSection()
        {
            var border = new Border
            {
                Background = _headerBg,
                BorderBrush = _borderColor,
                BorderThickness = new Thickness(1),
                Padding = new Thickness(10),
                Margin = new Thickness(0, 0, 0, 10)
            };

            var panel = new StackPanel { Orientation = Orientation.Horizontal };

            _btnPlay = CreateButton("Play", OnPlayClick);
            _btnPlay.IsEnabled = false;
            panel.Children.Add(_btnPlay);

            _btnPause = CreateButton("Pause", OnPauseClick);
            _btnPause.IsEnabled = false;
            panel.Children.Add(_btnPause);

            _btnStop = CreateButton("Stop", OnStopClick);
            _btnStop.IsEnabled = false;
            panel.Children.Add(_btnStop);

            // Speed label
            var speedLabel = new TextBlock
            {
                Text = "Speed:",
                Foreground = _fgColor,
                FontFamily = _ntFont,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(20, 0, 5, 0)
            };
            panel.Children.Add(speedLabel);

            // Speed combo
            _cboSpeed = new ComboBox
            {
                Width = 60,
                Background = _bgColor,
                Foreground = _fgColor,
                FontFamily = _ntFont,
                Margin = new Thickness(5, 0, 0, 0)
            };
            _cboSpeed.Items.Add("1x");
            _cboSpeed.Items.Add("2x");
            _cboSpeed.Items.Add("5x");
            _cboSpeed.Items.Add("10x");
            _cboSpeed.SelectedIndex = 0;
            _cboSpeed.SelectionChanged += OnSpeedChanged;
            panel.Children.Add(_cboSpeed);

            border.Child = panel;
            return border;
        }

        private Border CreateStatusSection()
        {
            var border = new Border
            {
                Background = _headerBg,
                BorderBrush = _borderColor,
                BorderThickness = new Thickness(1),
                Padding = new Thickness(10)
            };

            var grid = new Grid();
            grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(28) });
            grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(28) });
            grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(28) });
            grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(28) });

            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(110) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

            // Row 0: State
            AddLabel(grid, "State:", 0, 0);
            _lblState = CreateStatusLabel("Idle");
            Grid.SetRow(_lblState, 0);
            Grid.SetColumn(_lblState, 1);
            grid.Children.Add(_lblState);

            // Row 1: Status Message
            AddLabel(grid, "Status:", 1, 0);
            _lblStatus = CreateStatusLabel("Ready to load data");
            Grid.SetRow(_lblStatus, 1);
            Grid.SetColumn(_lblStatus, 1);
            grid.Children.Add(_lblStatus);

            // Row 2: Current Time and Progress
            AddLabel(grid, "Current Time:", 2, 0);
            var timePanel = new StackPanel { Orientation = Orientation.Horizontal };

            _lblCurrentTime = CreateStatusLabel("--:--:--");
            _lblCurrentTime.Width = 80;
            timePanel.Children.Add(_lblCurrentTime);

            _progressBar = new ProgressBar
            {
                Width = 150,
                Height = 12,
                Minimum = 0,
                Maximum = 100,
                Value = 0,
                Margin = new Thickness(10, 0, 0, 0)
            };
            timePanel.Children.Add(_progressBar);

            Grid.SetRow(timePanel, 2);
            Grid.SetColumn(timePanel, 1);
            grid.Children.Add(timePanel);

            // Row 3: Symbols and Prices
            AddLabel(grid, "Stats:", 3, 0);
            var statsPanel = new StackPanel { Orientation = Orientation.Horizontal };

            var symbolsLabel = new TextBlock
            {
                Text = "Symbols:",
                Foreground = _fgColor,
                FontFamily = _ntFont,
                VerticalAlignment = VerticalAlignment.Center
            };
            statsPanel.Children.Add(symbolsLabel);

            _lblSymbolsLoaded = CreateStatusLabel("0");
            _lblSymbolsLoaded.Width = 30;
            _lblSymbolsLoaded.Margin = new Thickness(5, 0, 15, 0);
            statsPanel.Children.Add(_lblSymbolsLoaded);

            var ticksLabel = new TextBlock
            {
                Text = "Ticks:",
                Foreground = _fgColor,
                FontFamily = _ntFont,
                VerticalAlignment = VerticalAlignment.Center
            };
            statsPanel.Children.Add(ticksLabel);

            _lblTicksLoaded = CreateStatusLabel("0");
            _lblTicksLoaded.Width = 60;
            _lblTicksLoaded.Margin = new Thickness(5, 0, 15, 0);
            statsPanel.Children.Add(_lblTicksLoaded);

            var pricesLabel = new TextBlock
            {
                Text = "Injected:",
                Foreground = _fgColor,
                FontFamily = _ntFont,
                VerticalAlignment = VerticalAlignment.Center
            };
            statsPanel.Children.Add(pricesLabel);

            _lblPricesInjected = CreateStatusLabel("0");
            _lblPricesInjected.Width = 60;
            _lblPricesInjected.Margin = new Thickness(5, 0, 0, 0);
            statsPanel.Children.Add(_lblPricesInjected);

            Grid.SetRow(statsPanel, 3);
            Grid.SetColumn(statsPanel, 1);
            grid.Children.Add(statsPanel);

            border.Child = grid;
            return border;
        }

        private void AddLabel(Grid grid, string text, int row, int col)
        {
            var label = new TextBlock
            {
                Text = text,
                Foreground = _fgColor,
                FontFamily = _ntFont,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(5, 0, 5, 0)
            };
            Grid.SetRow(label, row);
            Grid.SetColumn(label, col);
            grid.Children.Add(label);
        }

        private TextBox CreateTextBox(string defaultValue)
        {
            return new TextBox
            {
                Text = defaultValue,
                Background = _bgColor,
                Foreground = _fgColor,
                FontFamily = _ntFont,
                BorderBrush = _borderColor,
                Padding = new Thickness(5, 3, 5, 3),
                Margin = new Thickness(5, 2, 5, 2)
            };
        }

        private DatePicker CreateStyledDatePicker(DateTime defaultDate)
        {
            var picker = new DatePicker
            {
                SelectedDate = defaultDate,
                FontFamily = _ntFont,
                Background = _bgColor,
                Foreground = _fgColor,
                BorderBrush = _borderColor,
                Margin = new Thickness(5, 2, 5, 2)
            };

            // Try to apply NinjaTrader's DatePicker style if available
            var datePickerStyle = Application.Current.TryFindResource("DatePickerStyle") as Style;
            if (datePickerStyle != null)
            {
                picker.Style = datePickerStyle;
            }

            return picker;
        }

        private TextBlock CreateStatusLabel(string defaultValue)
        {
            return new TextBlock
            {
                Text = defaultValue,
                Foreground = _fgColor,
                FontFamily = _ntFont,
                VerticalAlignment = VerticalAlignment.Center
            };
        }

        private Button CreateButton(string content, RoutedEventHandler handler)
        {
            var btn = new Button
            {
                Content = content,
                Background = _bgColor,
                Foreground = _fgColor,
                FontFamily = _ntFont,
                Padding = new Thickness(15, 5, 15, 5),
                Margin = new Thickness(0, 0, 10, 0),
                Cursor = Cursors.Hand,
                BorderBrush = _borderColor
            };
            btn.Click += handler;
            return btn;
        }

        private void BindToService()
        {
            var service = SimulationService.Instance;

            // Use named handler for proper cleanup (prevents memory leak)
            _servicePropertyChangedHandler = OnServicePropertyChanged;
            service.PropertyChanged += _servicePropertyChangedHandler;
        }

        private void OnServicePropertyChanged(object sender, PropertyChangedEventArgs e)
        {
            Dispatcher.InvokeAsync(() =>
            {
                var service = SimulationService.Instance;
                switch (e.PropertyName)
                {
                    case nameof(service.State):
                        _lblState.Text = service.State.ToString();
                        UpdateButtonStates();
                        break;
                    case nameof(service.StatusMessage):
                        _lblStatus.Text = service.StatusMessage;
                        break;
                    case nameof(service.CurrentSimTimeDisplay):
                        _lblCurrentTime.Text = service.CurrentSimTimeDisplay;
                        break;
                    case nameof(service.Progress):
                        _progressBar.Value = service.Progress;
                        break;
                    case nameof(service.LoadedSymbolCount):
                        _lblSymbolsLoaded.Text = service.LoadedSymbolCount.ToString();
                        break;
                    case nameof(service.TotalTickCount):
                        _lblTicksLoaded.Text = service.TotalTickCount.ToString("N0");
                        break;
                    case nameof(service.PricesInjectedCount):
                        _lblPricesInjected.Text = service.PricesInjectedCount.ToString("N0");
                        break;
                }
            });
        }

        private void UnbindFromService()
        {
            if (_servicePropertyChangedHandler != null)
            {
                var service = SimulationService.Instance;
                service.PropertyChanged -= _servicePropertyChangedHandler;
                _servicePropertyChangedHandler = null;
                Logger.Debug("[SimulationEngineTabPage] Unbound from SimulationService PropertyChanged event");
            }
        }

        private void UpdateButtonStates()
        {
            var service = SimulationService.Instance;
            _btnPlay.IsEnabled = service.CanStart;
            _btnPause.IsEnabled = service.CanPause;
            _btnStop.IsEnabled = service.CanStop;
            _btnLoad.IsEnabled = service.State == SimulationState.Idle ||
                                  service.State == SimulationState.Ready ||
                                  service.State == SimulationState.Completed ||
                                  service.State == SimulationState.Error;
        }

        private bool UpdateConfigFromUI()
        {
            try
            {
                _config.SimulationDate = _datePicker.SelectedDate ?? DateTime.Today.AddDays(-1);
                _config.Underlying = _cboUnderlying.SelectedItem?.ToString() ?? "NIFTY";
                _config.ExpiryDate = _expiryPicker.SelectedDate ?? DateTime.Today;

                // Parse time from
                if (TimeSpan.TryParse(_txtTimeFrom.Text, out TimeSpan timeFrom))
                    _config.TimeFrom = timeFrom;
                else
                {
                    _lblStatus.Text = "Invalid Time From format (use HH:mm)";
                    return false;
                }

                // Parse time to
                if (TimeSpan.TryParse(_txtTimeTo.Text, out TimeSpan timeTo))
                    _config.TimeTo = timeTo;
                else
                {
                    _lblStatus.Text = "Invalid Time To format (use HH:mm)";
                    return false;
                }

                // Parse projected open
                if (decimal.TryParse(_txtProjectedOpen.Text, out decimal projectedOpen))
                    _config.ProjectedOpen = projectedOpen;
                else
                {
                    _lblStatus.Text = "Invalid Projected Open (must be a number)";
                    return false;
                }

                // Parse speed
                var speedText = _cboSpeed.SelectedItem?.ToString() ?? "1x";
                _config.SpeedMultiplier = int.Parse(speedText.Replace("x", ""));

                // Parse step size
                if (int.TryParse(_txtStepSize.Text, out int stepSize) && stepSize > 0)
                    _config.StepSize = stepSize;
                else
                {
                    _lblStatus.Text = "Invalid Step Size (must be a positive number)";
                    return false;
                }

                // Parse strike count
                if (int.TryParse(_txtStrikeCount.Text, out int strikeCount) && strikeCount > 0)
                    _config.StrikeCount = strikeCount;
                else
                {
                    _lblStatus.Text = "Invalid Strike Count (must be a positive number)";
                    return false;
                }

                // Parse symbol prefix (optional)
                _config.SymbolPrefix = _txtSymbolPrefix.Text?.Trim() ?? "";

                return true;
            }
            catch (Exception ex)
            {
                _lblStatus.Text = $"Configuration error: {ex.Message}";
                return false;
            }
        }

        private void OnSymbolParamsChanged(object sender, TextChangedEventArgs e)
        {
            UpdateSymbolPreview();
        }

        private void UpdateSymbolPreview()
        {
            try
            {
                if (_lblSymbolPreview == null) return;

                decimal projectedOpen = 0;
                int stepSize = 50;
                int strikeCount = 5;
                string prefix = _txtSymbolPrefix?.Text?.Trim() ?? "";

                decimal.TryParse(_txtProjectedOpen?.Text ?? "0", out projectedOpen);
                int.TryParse(_txtStepSize?.Text ?? "50", out stepSize);
                int.TryParse(_txtStrikeCount?.Text ?? "5", out strikeCount);

                if (stepSize <= 0) stepSize = 50;
                if (strikeCount <= 0) strikeCount = 5;

                decimal atmStrike = projectedOpen > 0 && stepSize > 0
                    ? Math.Round(projectedOpen / stepSize) * stepSize
                    : 0;

                int totalSymbols = (2 * strikeCount + 1) * 2; // CE + PE for each strike

                // Show sample symbol based on prefix
                string sampleSymbol;
                if (!string.IsNullOrEmpty(prefix))
                {
                    sampleSymbol = $"{prefix}{atmStrike:F0}CE";
                }
                else
                {
                    string underlying = _cboUnderlying?.SelectedItem?.ToString() ?? "NIFTY";
                    DateTime expiry = _expiryPicker?.SelectedDate ?? DateTime.Today;
                    string monthAbbr = expiry.ToString("MMM").ToUpper();
                    int year = expiry.Year % 100;
                    sampleSymbol = $"{underlying}{year}{monthAbbr}{atmStrike:F0}CE";
                }

                _lblSymbolPreview.Text = $"ATM: {atmStrike:F0}, Symbols: {totalSymbols}, e.g. {sampleSymbol}";
            }
            catch
            {
                // Ignore parsing errors during preview update
            }
        }

        private async void OnLoadClick(object sender, RoutedEventArgs e)
        {
            try
            {
                if (!UpdateConfigFromUI())
                    return;

                _btnLoad.IsEnabled = false;
                Logger.Info($"[SimulationEngineTabPage] Loading data: {_config.Underlying} {_config.SimulationDate:yyyy-MM-dd} {_config.TimeFrom}-{_config.TimeTo}");

                var success = await SimulationService.Instance.LoadHistoricalBars(_config);

                UpdateButtonStates();

                if (success)
                {
                    Logger.Info("[SimulationEngineTabPage] Data loaded successfully");

                    // Publish the simulated option chain to MarketDataReactiveHub
                    // This regenerates the Option Chain window with simulation config (underlying, expiry, strikes)
                    SimulationService.Instance.PublishSimulatedOptionChain();
                    Logger.Info("[SimulationEngineTabPage] Simulated option chain published to Option Chain window");
                }
                else
                {
                    Logger.Warn("[SimulationEngineTabPage] Failed to load data");
                }
            }
            catch (Exception ex)
            {
                Logger.Error($"[SimulationEngineTabPage] ERROR in OnLoadClick: {ex.Message}", ex);
                _btnLoad.IsEnabled = true;
                _lblStatus.Text = $"Load failed: {ex.Message}";
            }
        }

        private void OnPlayClick(object sender, RoutedEventArgs e)
        {
            SimulationService.Instance.Start();
            UpdateButtonStates();
        }

        private void OnPauseClick(object sender, RoutedEventArgs e)
        {
            SimulationService.Instance.Pause();
            UpdateButtonStates();
        }

        private void OnStopClick(object sender, RoutedEventArgs e)
        {
            SimulationService.Instance.Stop();
            UpdateButtonStates();
        }

        private void OnSpeedChanged(object sender, SelectionChangedEventArgs e)
        {
            var speedText = _cboSpeed.SelectedItem?.ToString() ?? "1x";
            int speed = int.Parse(speedText.Replace("x", ""));
            SimulationService.Instance.SetSpeed(speed);
        }

        protected override string GetHeaderPart(string variable)
        {
            return "Simulation";
        }

        public override void Cleanup()
        {
            Logger.Info("[SimulationEngineTabPage] Cleanup");
            UnbindFromService();
            SimulationService.Instance.Reset();
            base.Cleanup();
        }

        protected override void Restore(XElement element)
        {
            // Nothing to restore for now
        }

        protected override void Save(XElement element)
        {
            // Nothing to save for now
        }
    }
}
