using System;
using System.ComponentModel;
using System.Globalization;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;
using System.Xml.Linq;
using NinjaTrader.Cbi;
using NinjaTrader.Gui.Tools;
using ZerodhaDatafeedAdapter.AddOns.OptionSignals.Controls;
using ZerodhaDatafeedAdapter.AddOns.OptionSignals.Services;
using ZerodhaDatafeedAdapter.Logging;

namespace ZerodhaDatafeedAdapter.AddOns.OptionSignals
{
    public class OptionSignalsWindow : NTWindow, IWorkspacePersistence
    {
        public OptionSignalsWindow()
        {
            Caption = "Option Signals";
            Width = 1100;
            Height = 700;

            TabControl tabControl = new TabControl();
            tabControl.Style = Application.Current.TryFindResource("TabControlStyle") as Style;

            // Create shared ViewModel
            var viewModel = new OptionSignalsViewModel();

            // Tab 1: Options in Play (existing functionality)
            var optionsTabItem = new TabItem { Header = "Options in Play" };
            optionsTabItem.Content = new OptionsInPlayTabPage(viewModel);
            tabControl.Items.Add(optionsTabItem);

            // Tab 2: Signals (new functionality)
            var signalsTabItem = new TabItem { Header = "Signals" };
            signalsTabItem.Content = new SignalsTabPage(viewModel);
            tabControl.Items.Add(signalsTabItem);

            // Tab 3: Terminal (NinjaScript Output-style logging)
            var terminalTabItem = new TabItem { Header = "Terminal" };
            terminalTabItem.Content = new TerminalTabPage();
            tabControl.Items.Add(terminalTabItem);

            tabControl.SelectedIndex = 0;

            Content = tabControl;
            Logger.Info("[OptionSignalsWindow] Window created with Options in Play and Signals tabs");
        }

        public void Restore(XDocument document, XElement element) { }
        public void Save(XDocument document, XElement element) { }
        public WorkspaceOptions WorkspaceOptions { get; set; }
    }

    /// <summary>
    /// Options in Play tab - displays option chain with VP metrics, momentum, and VWAP scores.
    /// </summary>
    public class OptionsInPlayTabPage : UserControl
    {
        private readonly OptionSignalsViewModel _viewModel;
        private OptionSignalsHeaderControl _header;
        private OptionSignalsStatusBar _status;
        private OptionSignalsListView _listView;

        public OptionsInPlayTabPage(OptionSignalsViewModel viewModel)
        {
            _viewModel = viewModel;
            _viewModel.PropertyChanged += OnViewModelPropertyChanged;

            BuildUI();

            Loaded += (s, e) => _viewModel.StartServices();
            Unloaded += (s, e) => _viewModel.StopServices();
        }

        private void BuildUI()
        {
            var dock = new DockPanel { Background = new SolidColorBrush(Color.FromRgb(27, 27, 28)) };
            Content = dock;

            _header = new OptionSignalsHeaderControl();
            DockPanel.SetDock(_header, Dock.Top);
            dock.Children.Add(_header);

            _status = new OptionSignalsStatusBar();
            DockPanel.SetDock(_status, Dock.Bottom);
            dock.Children.Add(_status);

            _listView = new OptionSignalsListView { ItemsSource = _viewModel.Rows };
            dock.Children.Add(_listView);
        }

        private void OnViewModelPropertyChanged(object sender, PropertyChangedEventArgs e)
        {
            Dispatcher.InvokeAsync(() =>
            {
                switch (e.PropertyName)
                {
                    case nameof(_viewModel.Underlying): _header.Underlying = _viewModel.Underlying; break;
                    case nameof(_viewModel.Expiry): _header.Expiry = _viewModel.Expiry; break;
                    case nameof(_viewModel.StatusText):
                        _header.StatusText = _viewModel.StatusText;
                        _status.StatusText = _viewModel.StatusText;
                        break;
                }
            });
        }

        public void Cleanup()
        {
            _viewModel.PropertyChanged -= OnViewModelPropertyChanged;
            _viewModel.Dispose();
        }
    }

    /// <summary>
    /// Signals tab - displays generated signals with P&L tracking.
    /// </summary>
    public class SignalsTabPage : UserControl
    {
        private readonly OptionSignalsViewModel _viewModel;
        private SignalsListView _signalsListView;
        private StackPanel _summaryPanel;
        private TextBlock _totalUnrealizedText;
        private TextBlock _totalRealizedText;
        private TextBlock _activeSignalsText;

        private static readonly SolidColorBrush _bgColor = new SolidColorBrush(Color.FromRgb(27, 27, 28));
        private static readonly SolidColorBrush _fgColor = new SolidColorBrush(Color.FromRgb(212, 212, 212));
        private static readonly SolidColorBrush _headerBg = new SolidColorBrush(Color.FromRgb(37, 37, 38));
        private static readonly SolidColorBrush _positiveColor = new SolidColorBrush(Color.FromRgb(38, 166, 91));
        private static readonly SolidColorBrush _negativeColor = new SolidColorBrush(Color.FromRgb(207, 70, 71));

        public SignalsTabPage(OptionSignalsViewModel viewModel)
        {
            _viewModel = viewModel;
            _viewModel.PropertyChanged += OnViewModelPropertyChanged;

            BuildUI();

            Unloaded += (s, e) => Cleanup();
        }

        private void BuildUI()
        {
            var dock = new DockPanel { Background = _bgColor };
            Content = dock;

            // Summary header panel
            _summaryPanel = CreateSummaryPanel();
            DockPanel.SetDock(_summaryPanel, Dock.Top);
            dock.Children.Add(_summaryPanel);

            // Signals list view
            _signalsListView = new SignalsListView { ItemsSource = _viewModel.Signals };
            dock.Children.Add(_signalsListView);
        }

        private StackPanel CreateSummaryPanel()
        {
            var panel = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Background = _headerBg,
                Height = 32
            };

            // Active signals count
            panel.Children.Add(new TextBlock
            {
                Text = "Active:",
                Foreground = _fgColor,
                FontSize = 11,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(10, 0, 5, 0)
            });

            _activeSignalsText = new TextBlock
            {
                Text = "0",
                Foreground = _fgColor,
                FontWeight = FontWeights.Bold,
                FontSize = 11,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 0, 20, 0)
            };
            panel.Children.Add(_activeSignalsText);

            // Total unrealized P&L
            panel.Children.Add(new TextBlock
            {
                Text = "Unrealized:",
                Foreground = _fgColor,
                FontSize = 11,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 0, 5, 0)
            });

            _totalUnrealizedText = new TextBlock
            {
                Text = "0.00",
                FontWeight = FontWeights.Bold,
                FontSize = 11,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 0, 20, 0)
            };
            panel.Children.Add(_totalUnrealizedText);

            // Total realized P&L
            panel.Children.Add(new TextBlock
            {
                Text = "Realized:",
                Foreground = _fgColor,
                FontSize = 11,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 0, 5, 0)
            });

            _totalRealizedText = new TextBlock
            {
                Text = "0.00",
                FontWeight = FontWeights.Bold,
                FontSize = 11,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 0, 20, 0)
            };
            panel.Children.Add(_totalRealizedText);

            return panel;
        }

        private void OnViewModelPropertyChanged(object sender, PropertyChangedEventArgs e)
        {
            Dispatcher.InvokeAsync(() =>
            {
                switch (e.PropertyName)
                {
                    case nameof(_viewModel.TotalUnrealizedPnL):
                        UpdatePnLDisplay(_totalUnrealizedText, _viewModel.TotalUnrealizedPnL);
                        break;
                    case nameof(_viewModel.TotalRealizedPnL):
                        UpdatePnLDisplay(_totalRealizedText, _viewModel.TotalRealizedPnL);
                        break;
                    case nameof(_viewModel.ActiveSignalCount):
                        _activeSignalsText.Text = _viewModel.ActiveSignalCount.ToString();
                        break;
                }
            });
        }

        private void UpdatePnLDisplay(TextBlock textBlock, double pnl)
        {
            textBlock.Text = pnl.ToString("F2");
            if (pnl > 0)
                textBlock.Foreground = _positiveColor;
            else if (pnl < 0)
                textBlock.Foreground = _negativeColor;
            else
                textBlock.Foreground = _fgColor;
        }

        public void Cleanup()
        {
            _viewModel.PropertyChanged -= OnViewModelPropertyChanged;
        }
    }

    /// <summary>
    /// Converts log level to foreground color.
    /// </summary>
    public class LogLevelToColorConverter : IValueConverter
    {
        private static readonly SolidColorBrush _infoColor = new SolidColorBrush(Color.FromRgb(212, 212, 212));   // Gray
        private static readonly SolidColorBrush _warnColor = new SolidColorBrush(Color.FromRgb(255, 193, 7));    // Amber
        private static readonly SolidColorBrush _errorColor = new SolidColorBrush(Color.FromRgb(207, 70, 71));   // Red
        private static readonly SolidColorBrush _debugColor = new SolidColorBrush(Color.FromRgb(100, 100, 100)); // Dark gray
        private static readonly SolidColorBrush _signalColor = new SolidColorBrush(Color.FromRgb(38, 166, 91));  // Green
        private static readonly SolidColorBrush _evalColor = new SolidColorBrush(Color.FromRgb(86, 156, 214));   // Blue
        private static readonly SolidColorBrush _indexColor = new SolidColorBrush(Color.FromRgb(206, 145, 120)); // Orange

        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            if (value is string level)
            {
                switch (level)
                {
                    case "INFO": return _infoColor;
                    case "WARN": return _warnColor;
                    case "ERROR": return _errorColor;
                    case "DEBUG": return _debugColor;
                    case "SIGNAL": return _signalColor;
                    case "EVAL": return _evalColor;
                    case "INDEX": return _indexColor;
                }
            }
            return _infoColor;
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
            => throw new NotImplementedException();
    }

    /// <summary>
    /// Terminal tab - NinjaScript Output-style logging for strategy evaluation.
    /// </summary>
    public class TerminalTabPage : UserControl
    {
        private ListBox _logListBox;
        private Button _clearButton;

        private static readonly SolidColorBrush _bgColor = new SolidColorBrush(Color.FromRgb(30, 30, 30));
        private static readonly SolidColorBrush _fgColor = new SolidColorBrush(Color.FromRgb(212, 212, 212));
        private static readonly SolidColorBrush _headerBg = new SolidColorBrush(Color.FromRgb(37, 37, 38));
        private static readonly LogLevelToColorConverter _colorConverter = new LogLevelToColorConverter();

        public TerminalTabPage()
        {
            // Initialize terminal service with dispatcher
            TerminalService.Instance.SetDispatcher(Dispatcher);

            BuildUI();

            Loaded += (s, e) =>
            {
                TerminalService.Instance.Info("Terminal initialized - Option Signals module ready");
            };
        }

        private void BuildUI()
        {
            var dock = new DockPanel { Background = _bgColor };
            Content = dock;

            // Header with clear button
            var header = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Background = _headerBg,
                Height = 28
            };
            DockPanel.SetDock(header, Dock.Top);
            dock.Children.Add(header);

            header.Children.Add(new TextBlock
            {
                Text = "Terminal Output",
                Foreground = _fgColor,
                FontWeight = FontWeights.Bold,
                FontSize = 11,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(10, 0, 20, 0)
            });

            _clearButton = new Button
            {
                Content = "Clear",
                Width = 60,
                Height = 20,
                FontSize = 10,
                Margin = new Thickness(0, 0, 10, 0),
                VerticalAlignment = VerticalAlignment.Center
            };
            _clearButton.Click += (s, e) => TerminalService.Instance.Clear();
            header.Children.Add(_clearButton);

            // Log list box
            _logListBox = new ListBox
            {
                Background = _bgColor,
                Foreground = _fgColor,
                BorderThickness = new Thickness(0),
                FontFamily = new FontFamily("Consolas"),
                FontSize = 11,
                ItemsSource = TerminalService.Instance.Logs
            };
            VirtualizingStackPanel.SetIsVirtualizing(_logListBox, true);
            VirtualizingStackPanel.SetVirtualizationMode(_logListBox, VirtualizationMode.Recycling);

            // Item template with color binding
            var itemTemplate = new DataTemplate();
            var factory = new FrameworkElementFactory(typeof(TextBlock));
            factory.SetBinding(TextBlock.TextProperty, new Binding("FormattedLine"));
            factory.SetBinding(TextBlock.ForegroundProperty, new Binding("Level") { Converter = _colorConverter });
            factory.SetValue(TextBlock.FontFamilyProperty, new FontFamily("Consolas"));
            factory.SetValue(TextBlock.FontSizeProperty, 11.0);
            factory.SetValue(TextBlock.MarginProperty, new Thickness(2, 1, 2, 1));
            itemTemplate.VisualTree = factory;
            _logListBox.ItemTemplate = itemTemplate;

            // Auto-scroll to bottom when new items added
            ((System.Collections.Specialized.INotifyCollectionChanged)TerminalService.Instance.Logs)
                .CollectionChanged += (s, e) =>
                {
                    if (e.Action == System.Collections.Specialized.NotifyCollectionChangedAction.Add &&
                        _logListBox.Items.Count > 0)
                    {
                        _logListBox.ScrollIntoView(_logListBox.Items[_logListBox.Items.Count - 1]);
                    }
                };

            dock.Children.Add(_logListBox);
        }
    }
}
