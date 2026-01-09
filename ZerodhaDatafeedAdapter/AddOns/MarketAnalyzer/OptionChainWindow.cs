using System;
using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Xml.Linq;
using NinjaTrader.Cbi;
using NinjaTrader.Gui.Tools;
using ZerodhaDatafeedAdapter.AddOns.MarketAnalyzer.Controls;
using ZerodhaDatafeedAdapter.Logging;

namespace ZerodhaDatafeedAdapter.AddOns.MarketAnalyzer
{
    /// <summary>
    /// Option Chain Window - hosts the OptionChainTabPage
    /// </summary>
    public class OptionChainWindow : NTWindow, IWorkspacePersistence
    {
        public OptionChainWindow()
        {
            Logger.Info("[OptionChainWindow] Constructor: Creating window");

            Caption = "Option Chain";
            Width = 900;
            Height = 600;

            TabControl tabControl = new TabControl();
            tabControl.Style = Application.Current.TryFindResource("TabControlStyle") as Style;

            OptionChainTabPage tabPage = new OptionChainTabPage();
            tabControl.Items.Add(tabPage);
            tabControl.SelectedIndex = 0;

            Content = tabControl;

            Logger.Info("[OptionChainWindow] Constructor: Window created with modular components");
        }

        public void Restore(XDocument document, XElement element) { }
        public void Save(XDocument document, XElement element) { }
        public WorkspaceOptions WorkspaceOptions { get; set; }
    }

    /// <summary>
    /// Modularized Option Chain Tab Page using MVVM pattern.
    /// UI logic is delegated to OptionChainUIHelpers and business logic to OptionChainViewModel.
    /// </summary>
    public class OptionChainTabPage : NTTabPage, IInstrumentProvider
    {
        private OptionChainViewModel _viewModel;
        private OptionChainHeaderControl _headerControl;
        private OptionChainStatusBar _statusBarControl;
        private OptionChainListView _listViewControl;

        public OptionChainTabPage()
        {
            Logger.Info("[OptionChainTabPage] Initializing MVVM UI");

            _viewModel = new OptionChainViewModel();
            _viewModel.PropertyChanged += OnViewModelPropertyChanged;

            BuildUI();

            Loaded += OnTabPageLoaded;
            Unloaded += OnTabPageUnloaded;
        }

        private void BuildUI()
        {
            var dockPanel = OptionChainUIHelpers.CreateMainLayout();
            Content = dockPanel;

            // Header
            _headerControl = OptionChainUIHelpers.CreateHeaderControl();
            DockPanel.SetDock(_headerControl, Dock.Top);
            dockPanel.Children.Add(_headerControl);

            // Status Bar
            _statusBarControl = OptionChainUIHelpers.CreateStatusBar();
            DockPanel.SetDock(_statusBarControl, Dock.Bottom);
            dockPanel.Children.Add(_statusBarControl);

            // ListView
            _listViewControl = OptionChainUIHelpers.CreateListView(_viewModel.Rows, OnListViewClick);
            dockPanel.Children.Add(_listViewControl);
        }

        private void OnTabPageLoaded(object sender, RoutedEventArgs e)
        {
            Logger.Info("[OptionChainTabPage] OnTabPageLoaded: Starting ViewModel services");
            _viewModel.StartServices();
        }

        private void OnTabPageUnloaded(object sender, RoutedEventArgs e)
        {
            Logger.Info("[OptionChainTabPage] OnTabPageUnloaded: Stopping ViewModel services");
            _viewModel.StopServices();
        }

        private void OnViewModelPropertyChanged(object sender, PropertyChangedEventArgs e)
        {
            // Bindings for manual controls (Header/Status)
            Dispatcher.InvokeAsync(() =>
            {
                switch (e.PropertyName)
                {
                    case nameof(OptionChainViewModel.Underlying):
                        _headerControl.Underlying = _viewModel.Underlying;
                        UpdateHeader(); // Update tab header if needed
                        break;
                    case nameof(OptionChainViewModel.Expiry):
                        _headerControl.Expiry = _viewModel.Expiry;
                        break;
                    case nameof(OptionChainViewModel.ATMStrike):
                        _headerControl.ATMStrike = _viewModel.ATMStrike;
                        break;
                    case nameof(OptionChainViewModel.StatusText):
                        _statusBarControl.StatusText = _viewModel.StatusText;
                        break;
                    case nameof(OptionChainViewModel.StrikePositionText):
                        _headerControl.StrikePositionText = _viewModel.StrikePositionText;
                        break;
                    
                    case nameof(OptionChainViewModel.SelectedInstrumentMessage):
                         if (_viewModel.IsSelectedInstrumentError)
                             _headerControl.SetSelectedInstrumentError(_viewModel.SelectedInstrumentMessage);
                         else
                             _headerControl.SelectedInstrument = _viewModel.SelectedInstrumentMessage;
                         break;

                    case nameof(OptionChainViewModel.Instrument):
                        UpdateHeader();
                        break;
                }
            });
        }
        
        // This handles the VM's calculated Integers for the header UI
        // Since I made StrikePositionText a string in VM, I have a mismatch.
        // Let's Re-Check VM. I made `StrikePositionText`.
        // I will assume I can modify HeaderControl to accept a string or access its textblock.
        // Or I can modify the VM to expose the counts.
        // I'll stick to VM exposing string and fix HeaderControl in next step to accept string property if needed.
        // Actually, looking at `OptionChainHeaderControl`, `private TextBlock _lblStrikePosition;`.
        // I have to modify `OptionChainHeaderControl` to expose a public property `StrikePositionInfo` { set => _lblStrikePosition.Text = value; }
        
        private void OnListViewClick(object sender, MouseButtonEventArgs e)
        {
            var row = _listViewControl.SelectedItem;
            // Determine if CE or PE was clicked based on mouse position
            // The ListViewControl doesn't expose the mouse position easily in the event args directly from VM standpoint.
            // But the UIHelpers connected the event to this method.
            // `sender` is the DataGrid or ListView. `e` gives position.
            
            if (row == null) return;
            
            // Logic reused from original:
            var pos = e.GetPosition((UIElement)sender); 
            // ListViewControl is the sender? `OptionChainListView` constructor assigns: `_dataGrid.MouseLeftButtonUp += ...`
            // So sender is DataGrid-like.
            
            bool isCall = (pos.X < 235); // Crude coordinate check from original code
            
            _viewModel.HandleRowClick(row, isCall);
        }

        public Instrument Instrument
        {
            get => _viewModel.Instrument;
            set => _viewModel.Instrument = value;
        }

        public override void Cleanup()
        {
            _viewModel.PropertyChanged -= OnViewModelPropertyChanged;
            _viewModel.Dispose();
            base.Cleanup();
        }

        protected override string GetHeaderPart(string variable)
        {
            return _viewModel.Instrument?.FullName ?? 
                   (_viewModel.Underlying != "NIFTY" ? $"{_viewModel.Underlying} Options" : "Option Chain");
        }

        protected override void Restore(XElement element) 
        { 
            var attr = element.Attribute("LastInstrument"); 
            if (attr != null) Instrument = Instrument.GetInstrument(attr.Value); 
        }

        protected override void Save(XElement element) 
        { 
            if (Instrument != null) element.SetAttributeValue("LastInstrument", Instrument.FullName); 
        }

        private void UpdateHeader() 
        { 
            Dispatcher.InvokeAsync(() => { try { base.RefreshHeader(); } catch { } }); 
        }
    }
}
