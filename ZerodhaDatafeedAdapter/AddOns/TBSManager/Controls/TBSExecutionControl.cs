using System;
using System.Collections.ObjectModel;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using ZerodhaDatafeedAdapter.ViewModels;
using ZerodhaDatafeedAdapter.Models;

namespace ZerodhaDatafeedAdapter.AddOns.TBSManager.Controls
{
    public class TBSExecutionControl : UserControl
    {
        private StackPanel _executionPanel;
        private ScrollViewer _executionScrollViewer;
        private TextBlock _lblTotalPnL;
        private TextBlock _lblLiveCount;
        private TextBlock _lblMonitoringCount;
        private TextBlock _lblExecutionStatus;
        private TBSViewModel _viewModel;

        public TBSExecutionControl(TBSViewModel viewModel)
        {
            _viewModel = viewModel;
            DataContext = _viewModel;
            Content = BuildUI();
            
            // Subscribe to VM events for tranche updates
            if (_viewModel != null)
            {
                _viewModel.TrancheStateChanged += OnTrancheStateChanged;
                _viewModel.PropertyChanged += OnViewModelPropertyChanged;
            }
        }

        private Grid BuildUI()
        {
            var grid = new Grid();
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto }); // Summary row
            grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) }); // Scrollable content
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto }); // Status row

            // Summary panel
            var summaryPanel = new Border
            {
                Background = TBSStyles.HeaderBg,
                Margin = new Thickness(5),
                Padding = new Thickness(10, 8, 10, 8)
            };

            var summaryStack = new StackPanel { Orientation = Orientation.Horizontal };

            summaryStack.Children.Add(new TextBlock
            {
                Text = "Total P&L:",
                Foreground = TBSStyles.FgColor,
                VerticalAlignment = VerticalAlignment.Center,
                FontWeight = FontWeights.Bold,
                Margin = new Thickness(0, 0, 5, 0)
            });

            _lblTotalPnL = new TextBlock
            {
                Foreground = TBSStyles.FgColor,
                VerticalAlignment = VerticalAlignment.Center,
                FontWeight = FontWeights.Bold,
                MinWidth = 80
            };
            // Binding for TotalPnL
            var pnlBinding = new System.Windows.Data.Binding("TotalPnL") { StringFormat = "{0:N2}" };
            _lblTotalPnL.SetBinding(TextBlock.TextProperty, pnlBinding);
            summaryStack.Children.Add(_lblTotalPnL);

            summaryStack.Children.Add(new TextBlock
            {
                Text = "Live:",
                Foreground = TBSStyles.LiveColor,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(30, 0, 5, 0)
            });

            _lblLiveCount = new TextBlock
            {
                Foreground = TBSStyles.LiveColor,
                VerticalAlignment = VerticalAlignment.Center,
                FontWeight = FontWeights.Bold,
                MinWidth = 30
            };
            _lblLiveCount.SetBinding(TextBlock.TextProperty, new System.Windows.Data.Binding("LiveCount"));
            summaryStack.Children.Add(_lblLiveCount);

            summaryStack.Children.Add(new TextBlock
            {
                Text = "Monitoring:",
                Foreground = TBSStyles.MonitoringColor,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(20, 0, 5, 0)
            });

            _lblMonitoringCount = new TextBlock
            {
                Foreground = TBSStyles.MonitoringColor,
                VerticalAlignment = VerticalAlignment.Center,
                FontWeight = FontWeights.Bold,
                MinWidth = 30
            };
            _lblMonitoringCount.SetBinding(TextBlock.TextProperty, new System.Windows.Data.Binding("MonitoringCount"));
            summaryStack.Children.Add(_lblMonitoringCount);

            summaryPanel.Child = summaryStack;
            Grid.SetRow(summaryPanel, 0);
            grid.Children.Add(summaryPanel);

            // Scrollable execution panel
            _executionScrollViewer = new ScrollViewer
            {
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
                Margin = new Thickness(5),
                CanContentScroll = false
            };

            _executionPanel = new StackPanel
            {
                Background = TBSStyles.BgColor
            };

            _executionScrollViewer.Content = _executionPanel;
            Grid.SetRow(_executionScrollViewer, 1);
            grid.Children.Add(_executionScrollViewer);

            // Status bar
            _lblExecutionStatus = new TextBlock
            {
                Foreground = TBSStyles.FgColor,
                Margin = new Thickness(5),
                Text = "Ready"
            };
            Grid.SetRow(_lblExecutionStatus, 2);
            grid.Children.Add(_lblExecutionStatus);

            return grid;
        }

        private void OnViewModelPropertyChanged(object sender, System.ComponentModel.PropertyChangedEventArgs e)
        {
            if (e.PropertyName == nameof(TBSViewModel.ExecutionStates))
            {
                Dispatcher.InvokeAsync(RebuildExecutionUI);
            }
            else if (e.PropertyName == nameof(TBSViewModel.StatusMessage))
            {
                Dispatcher.InvokeAsync(() => _lblExecutionStatus.Text = _viewModel.StatusMessage);
            }
            else if (e.PropertyName == nameof(TBSViewModel.TotalPnL))
            {
                Dispatcher.InvokeAsync(() => {
                    var pnl = _viewModel.TotalPnL;
                    _lblTotalPnL.Foreground = pnl >= 0 ? TBSStyles.LiveColor : TBSStyles.NegativeColor;
                });
            }
        }

        private void OnTrancheStateChanged(object sender, TBSExecutionState state)
        {
            Dispatcher.InvokeAsync(() => UpdateTrancheUIForState(state));
        }

        public void RebuildExecutionUI()
        {
            _executionPanel.Children.Clear();

            bool isAlt = false;
            foreach (var state in _viewModel.ExecutionStates.OrderBy(s => s.Config?.EntryTime ?? TimeSpan.Zero))
            {
                var trancheUI = new TBSTrancheRowControl(state, isAlt);
                _executionPanel.Children.Add(trancheUI);
                isAlt = !isAlt;
            }
            
            _lblExecutionStatus.Text = $"Loaded {_viewModel.ExecutionStates.Count} tranches";
        }

        private void UpdateTrancheUIForState(TBSExecutionState state)
        {
            foreach (var child in _executionPanel.Children)
            {
                if (child is TBSTrancheRowControl row && row.Tag == state)
                {
                    row.Refresh();
                    break;
                }
            }
        }

        public void ShowWaitingMessage(string message)
        {
            _executionPanel.Children.Clear();
            _executionPanel.Children.Add(new TextBlock
            {
                Text = message,
                Foreground = TBSStyles.MonitoringColor,
                FontSize = 14,
                FontWeight = FontWeights.SemiBold,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(20)
            });
            _lblExecutionStatus.Text = "Waiting...";
        }
    }
}
