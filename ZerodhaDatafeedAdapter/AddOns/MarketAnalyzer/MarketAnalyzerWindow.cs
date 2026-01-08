using System;
using System.Windows;
using System.Windows.Controls;
using System.Xml.Linq;
using NinjaTrader.Gui.Tools;
using ZerodhaDatafeedAdapter.Logging;
using ZerodhaDatafeedAdapter.Services.Analysis;
// using ZerodhaDatafeedAdapter.Services.MarketData;

namespace ZerodhaDatafeedAdapter.AddOns.MarketAnalyzer
{
    /// <summary>
    /// Dashboard window for Index Watch (formerly GIFT NIFTY Market Analyzer).
    /// Displays real-time prices in a NinjaTrader-style ListView with resizable columns.
    /// Refactored to use MVVM pattern.
    /// </summary>
    public class MarketAnalyzerWindow : NTWindow, IWorkspacePersistence
    {
        private MarketAnalyzerViewModel _viewModel;
        
        // NinjaTrader-style colors (kept for Window background init if needed, though Helpers handle most)
        private static readonly System.Windows.Media.SolidColorBrush _bgColor = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(27, 27, 28));

        public MarketAnalyzerWindow()
        {
            Logger.Info("[MarketAnalyzerWindow] Constructor: Creating window");

            Caption = "Index Watch";
            Width = 720;
            Height = 400;

            try
            {
                _viewModel = new MarketAnalyzerViewModel();
                DataContext = _viewModel;

                var dockPanel = new DockPanel { Background = _bgColor };
                Content = dockPanel;

                // Toolbar
                var toolbar = MarketAnalyzerUIHelpers.CreateToolbar((s, e) => 
                {
                    Logger.Info("[MarketAnalyzerWindow] Refresh button clicked");
                    // Refresh action: restart service via ViewModel or directly
                    // ViewModel.StartServices triggers subscriptions, maybe we just want to restart logic?
                    // Original code: MarketAnalyzerService.Instance.Start();
                    MarketAnalyzerService.Instance.Start(); 
                });
                DockPanel.SetDock(toolbar, Dock.Top);
                dockPanel.Children.Add(toolbar);

                // Status bar
                var statusBar = MarketAnalyzerUIHelpers.CreateStatusBar();
                DockPanel.SetDock(statusBar, Dock.Bottom);
                dockPanel.Children.Add(statusBar);

                // Main ListView
                var listView = MarketAnalyzerUIHelpers.CreateListView();
                dockPanel.Children.Add(listView);

                // Nifty Futures Metrics Panel (below ListView)
                var vpMetricsPanel = MarketAnalyzerUIHelpers.CreateNiftyFuturesMetricsPanel();
                DockPanel.SetDock(vpMetricsPanel, Dock.Bottom);
                dockPanel.Children.Insert(dockPanel.Children.Count - 1, vpMetricsPanel);

                // Events
                Loaded += OnWindowLoaded;
                Unloaded += OnWindowUnloaded;

                Logger.Info("[MarketAnalyzerWindow] Constructor: Window created successfully");
            }
            catch (Exception ex)
            {
                Logger.Error($"[MarketAnalyzerWindow] Constructor: Exception - {ex.Message}", ex);
                throw;
            }
        }

        private void OnWindowLoaded(object sender, RoutedEventArgs e)
        {
            Logger.Info("[MarketAnalyzerWindow] OnWindowLoaded: Window loaded, starting ViewModel services");
            _viewModel.StartServices();
        }

        private void OnWindowUnloaded(object sender, RoutedEventArgs e)
        {
            Logger.Info("[MarketAnalyzerWindow] OnWindowUnloaded: Stopping ViewModel services");
            _viewModel.StopServices();
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
