using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
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
using ZerodhaDatafeedAdapter.ViewModels;
using ZerodhaDatafeedAdapter.AddOns.TBSManager.Controls;
using SimService = ZerodhaDatafeedAdapter.Services.SimulationService;

namespace ZerodhaDatafeedAdapter.AddOns.TBSManager
{
    /// <summary>
    /// TBS Manager Window - hosts the TBSManagerTabPage
    /// </summary>
    public class TBSManagerWindow : NTWindow, IWorkspacePersistence
    {
        public TBSManagerWindow()
        {
            Caption = "TBS Manager";
            Width = 900;
            Height = 700;

            TabControl tabControl = new TabControl();
            tabControl.Style = Application.Current.TryFindResource("TabControlStyle") as Style;

            TBSManagerTabPage tabPage = new TBSManagerTabPage();
            tabControl.Items.Add(tabPage);

            Content = tabControl;
        }

        public void Restore(XDocument document, XElement element) { }
        public void Save(XDocument document, XElement element) { }
        public WorkspaceOptions WorkspaceOptions { get; set; }
    }

    /// <summary>
    /// Simplified TBS Manager Tab Page using modular controls and ViewModel.
    /// </summary>
    public class TBSManagerTabPage : NTTabPage, IInstrumentProvider
    {
        private TabControl _innerTabControl;
        private TBSConfigControl _configControl;
        private TBSExecutionControl _executionControl;
        private TBSViewModel _viewModel;
        private Instrument _instrument;

        public TBSManagerTabPage()
        {
            _viewModel = new TBSViewModel();
            BuildUI();
            SubscribeToEvents();

            // Defer heavy initialization to avoid UI freeze on startup
            // Use BeginInvoke with Background priority so UI renders first
            Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() =>
            {
                // Initial load
                _viewModel.LoadConfigurations();

                // Start monitoring timers (same as original monolithic implementation)
                // This ensures the status timer runs and processes tranche state transitions
                _viewModel.StartExecution();

                TBSLogger.Info("[TBSManagerTabPage] Initialized (Modular)");
            }));
        }

        private void BuildUI()
        {
            var mainGrid = new Grid { Background = TBSStyles.BgColor };

            _innerTabControl = new TabControl
            {
                Background = TBSStyles.BgColor,
                Foreground = TBSStyles.FgColor,
                FontFamily = TBSStyles.NtFont,
                FontSize = 12,
                Margin = new Thickness(5)
            };

            // Config Tab
            _configControl = new TBSConfigControl(_viewModel);
            _innerTabControl.Items.Add(new TabItem { Header = "Configuration", Content = _configControl, Foreground = TBSStyles.FgColor });

            // Execution Tab
            _executionControl = new TBSExecutionControl(_viewModel);
            _innerTabControl.Items.Add(new TabItem { Header = "Execution", Content = _executionControl, Foreground = TBSStyles.FgColor });

            mainGrid.Children.Add(_innerTabControl);
            Content = mainGrid;
        }

        private void SubscribeToEvents()
        {
            MarketAnalyzerLogic.Instance.PriceSyncReady += OnPriceSyncReady;
            MarketAnalyzerLogic.Instance.OptionsGenerated += OnOptionsGenerated;
            MarketAnalyzerLogic.Instance.PriceUpdated += OnPriceHubUpdated;
        }

        private void UnsubscribeFromEvents()
        {
            MarketAnalyzerLogic.Instance.PriceSyncReady -= OnPriceSyncReady;
            MarketAnalyzerLogic.Instance.OptionsGenerated -= OnOptionsGenerated;
            MarketAnalyzerLogic.Instance.PriceUpdated -= OnPriceHubUpdated;
        }

        private void OnPriceSyncReady()
        {
            Dispatcher.InvokeAsync(() => _viewModel.OnOptionChainReady());
        }

        private void OnOptionsGenerated(List<MappedInstrument> options)
        {
            if (options == null || options.Count == 0) return;
            var first = options.First();
            Dispatcher.InvokeAsync(() => _viewModel.OnOptionsGenerated(first.underlying, first.expiry));
        }

        private void OnPriceHubUpdated(string symbol, double price)
        {
            Dispatcher.InvokeAsync(() =>
            {
                foreach (var state in _viewModel.ExecutionStates)
                {
                    foreach (var leg in state.Legs)
                    {
                        if (leg.Symbol == symbol) leg.CurrentPrice = (decimal)price;
                    }
                }
            });
        }

        #region IInstrumentProvider
        public Instrument Instrument
        {
            get => _instrument;
            set
            {
                if (_instrument != value)
                {
                    _instrument = value;
                    if (_instrument != null) _viewModel.SelectedUnderlying = _instrument.FullName;
                }
            }
        }
        #endregion

        protected override string GetHeaderPart(string variable) => "TBS Manager";

        public override void Cleanup()
        {
            UnsubscribeFromEvents();
            _viewModel?.Cleanup();
            base.Cleanup();
        }

        protected override void Restore(XElement element) { }
        protected override void Save(XElement element) { }
    }
}
