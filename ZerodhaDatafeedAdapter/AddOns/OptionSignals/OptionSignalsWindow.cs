using System;
using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Xml.Linq;
using NinjaTrader.Cbi;
using NinjaTrader.Gui.Tools;
using ZerodhaDatafeedAdapter.AddOns.OptionSignals.Controls;
using ZerodhaDatafeedAdapter.Logging;

namespace ZerodhaDatafeedAdapter.AddOns.OptionSignals
{
    public class OptionSignalsWindow : NTWindow, IWorkspacePersistence
    {
        public OptionSignalsWindow()
        {
            Caption = "Option Signals";
            Width = 950;
            Height = 650;

            TabControl tabControl = new TabControl();
            tabControl.Style = Application.Current.TryFindResource("TabControlStyle") as Style;

            OptionSignalsTabPage tabPage = new OptionSignalsTabPage();
            tabControl.Items.Add(tabPage);
            tabControl.SelectedIndex = 0;

            Content = tabControl;
            Logger.Info("[OptionSignalsWindow] Window created");
        }

        public void Restore(XDocument document, XElement element) { }
        public void Save(XDocument document, XElement element) { }
        public WorkspaceOptions WorkspaceOptions { get; set; }
    }

    public class OptionSignalsTabPage : NTTabPage
    {
        private OptionSignalsViewModel _viewModel;
        private OptionSignalsHeaderControl _header;
        private OptionSignalsStatusBar _status;
        private OptionSignalsListView _listView;

        public OptionSignalsTabPage()
        {
            _viewModel = new OptionSignalsViewModel();
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

        public override void Cleanup()
        {
            _viewModel.PropertyChanged -= OnViewModelPropertyChanged;
            _viewModel.Dispose();
            base.Cleanup();
        }

        protected override string GetHeaderPart(string variable)
        {
            return "Option Signals";
        }

        protected override void Restore(XElement element) { }

        protected override void Save(XElement element) { }
    }
}
