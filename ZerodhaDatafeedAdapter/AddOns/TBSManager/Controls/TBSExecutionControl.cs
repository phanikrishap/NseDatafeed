using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
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
        private TextBlock _lblFilteredCount;
        private CheckBox _chkLive;
        private CheckBox _chkMonitoring;
        private CheckBox _chkIdle;
        private CheckBox _chkSquaredOff;
        private CheckBox _chkSkipped;
        private TBSViewModel _viewModel;
        private DispatcherTimer _rebuildDebounceTimer;
        private bool _rebuildPending;
        private bool _isRebuilding;

        public TBSExecutionControl(TBSViewModel viewModel)
        {
            _viewModel = viewModel;
            DataContext = _viewModel;
            Content = BuildUI();

            // Setup debounce timer for rebuild operations
            _rebuildDebounceTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(100) };
            _rebuildDebounceTimer.Tick += (s, e) =>
            {
                _rebuildDebounceTimer.Stop();
                if (_rebuildPending)
                {
                    _rebuildPending = false;
                    RebuildExecutionUI();
                }
            };

            // Subscribe to VM events for tranche updates
            if (_viewModel != null)
            {
                _viewModel.TrancheStateChanged += OnTrancheStateChanged;
                _viewModel.PropertyChanged += OnViewModelPropertyChanged;

                // Subscribe to collection changes - debounce to avoid multiple rapid rebuilds
                _viewModel.ExecutionStates.CollectionChanged += OnExecutionStatesCollectionChanged;
            }

            // Initial UI build after all subscriptions are set up
            Dispatcher.BeginInvoke(DispatcherPriority.Loaded, new Action(() =>
            {
                if (_viewModel.ExecutionStates.Count > 0)
                {
                    RebuildExecutionUI();
                }
            }));
        }

        private void OnExecutionStatesCollectionChanged(object sender, NotifyCollectionChangedEventArgs e)
        {
            // Debounce rebuild - multiple events fire during InitializeExecutionStates (Clear + multiple Adds)
            _rebuildPending = true;
            _rebuildDebounceTimer.Stop();
            _rebuildDebounceTimer.Start();
        }

        private Grid BuildUI()
        {
            var grid = new Grid();
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto }); // Summary row
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto }); // Filter row
            grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) }); // Scrollable content
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto }); // Status row

            // Summary panel
            var summaryPanel = new Border
            {
                Background = TBSStyles.HeaderBg,
                Margin = new Thickness(5, 5, 5, 0),
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

            // Filter bar with multi-select checkboxes (Excel-like filtering)
            var filterPanel = new Border
            {
                Background = new SolidColorBrush(Color.FromRgb(45, 45, 48)),
                Margin = new Thickness(5, 2, 5, 0),
                Padding = new Thickness(10, 5, 10, 5)
            };

            var filterStack = new StackPanel { Orientation = Orientation.Horizontal };

            filterStack.Children.Add(new TextBlock
            {
                Text = "Show:",
                Foreground = TBSStyles.FgColor,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 0, 10, 0),
                FontWeight = FontWeights.SemiBold
            });

            // Create checkboxes for each status (all checked by default = show all)
            _chkLive = CreateFilterCheckBox("Live", TBSStyles.LiveColor, true);
            filterStack.Children.Add(_chkLive);

            _chkMonitoring = CreateFilterCheckBox("Monitoring", TBSStyles.MonitoringColor, true);
            filterStack.Children.Add(_chkMonitoring);

            _chkIdle = CreateFilterCheckBox("Idle", TBSStyles.FgColor, true);
            filterStack.Children.Add(_chkIdle);

            _chkSquaredOff = CreateFilterCheckBox("Squared Off", new SolidColorBrush(Color.FromRgb(150, 150, 150)), true);
            filterStack.Children.Add(_chkSquaredOff);

            _chkSkipped = CreateFilterCheckBox("Skipped", TBSStyles.NegativeColor, true);
            filterStack.Children.Add(_chkSkipped);

            // Separator
            filterStack.Children.Add(new Border
            {
                Width = 1,
                Background = TBSStyles.BorderColor,
                Margin = new Thickness(10, 2, 10, 2)
            });

            _lblFilteredCount = new TextBlock
            {
                Foreground = new SolidColorBrush(Color.FromRgb(150, 150, 150)),
                VerticalAlignment = VerticalAlignment.Center,
                FontSize = 11
            };
            filterStack.Children.Add(_lblFilteredCount);

            filterPanel.Child = filterStack;
            Grid.SetRow(filterPanel, 1);
            grid.Children.Add(filterPanel);

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
            Grid.SetRow(_executionScrollViewer, 2);
            grid.Children.Add(_executionScrollViewer);

            // Status bar
            _lblExecutionStatus = new TextBlock
            {
                Foreground = TBSStyles.FgColor,
                Margin = new Thickness(5),
                Text = "Ready"
            };
            Grid.SetRow(_lblExecutionStatus, 3);
            grid.Children.Add(_lblExecutionStatus);

            return grid;
        }

        private CheckBox CreateFilterCheckBox(string label, Brush foreground, bool isChecked)
        {
            var checkBox = new CheckBox
            {
                Content = label,
                IsChecked = isChecked,
                Foreground = foreground,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 0, 12, 0),
                FontSize = 11
            };
            checkBox.Checked += OnFilterCheckBoxChanged;
            checkBox.Unchecked += OnFilterCheckBoxChanged;
            return checkBox;
        }

        private void OnFilterCheckBoxChanged(object sender, RoutedEventArgs e)
        {
            RebuildExecutionUI();
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
            Dispatcher.InvokeAsync(() =>
            {
                // If any filter is active, we need to check if this state change affects visibility
                if (IsAnyFilterActive())
                {
                    // Check if the tranche is currently in the UI
                    bool isCurrentlyVisible = false;
                    foreach (var child in _executionPanel.Children)
                    {
                        if (child is TBSTrancheRowControl row && row.Tag == state)
                        {
                            isCurrentlyVisible = true;
                            break;
                        }
                    }

                    // Check if it should be visible based on current filters
                    bool shouldBeVisible = ShouldShowState(state);

                    // If visibility changed, rebuild the whole UI
                    if (isCurrentlyVisible != shouldBeVisible)
                    {
                        RebuildExecutionUI();
                        return;
                    }
                }

                // Otherwise just update the existing row
                UpdateTrancheUIForState(state);
            });
        }

        private bool ShouldShowState(TBSExecutionState state)
        {
            bool showLive = _chkLive?.IsChecked ?? true;
            bool showMonitoring = _chkMonitoring?.IsChecked ?? true;
            bool showIdle = _chkIdle?.IsChecked ?? true;
            bool showSquaredOff = _chkSquaredOff?.IsChecked ?? true;
            bool showSkipped = _chkSkipped?.IsChecked ?? true;

            switch (state.Status)
            {
                case TBSExecutionStatus.Live:
                    return showLive;
                case TBSExecutionStatus.Monitoring:
                    return showMonitoring;
                case TBSExecutionStatus.Idle:
                    return state.IsMissed ? showSkipped : showIdle;
                case TBSExecutionStatus.SquaredOff:
                    return showSquaredOff;
                case TBSExecutionStatus.Skipped:
                    return showSkipped;
                default:
                    return true;
            }
        }

        public void RebuildExecutionUI()
        {
            // Guard against concurrent rebuilds
            if (_isRebuilding) return;
            _isRebuilding = true;

            try
            {
                _executionPanel.Children.Clear();

                bool isAlt = false;
                // Take a snapshot to avoid collection modification during iteration
                var allStates = _viewModel.ExecutionStates.ToList();

                // Apply status filter
                var filteredStates = ApplyStatusFilter(allStates);

                foreach (var state in filteredStates.OrderBy(s => s.Config?.EntryTime ?? TimeSpan.Zero))
                {
                    var trancheUI = new TBSTrancheRowControl(state, isAlt);
                    trancheUI.Tag = state; // Set Tag on UserControl for UpdateTrancheUIForState
                    _executionPanel.Children.Add(trancheUI);
                    isAlt = !isAlt;
                }

                // Update status bar and filter count
                _lblExecutionStatus.Text = $"Loaded {allStates.Count} tranches";

                if (IsAnyFilterActive())
                {
                    _lblFilteredCount.Text = $"Showing {filteredStates.Count} of {allStates.Count}";
                }
                else
                {
                    _lblFilteredCount.Text = "";
                }
            }
            finally
            {
                _isRebuilding = false;
            }
        }

        private List<TBSExecutionState> ApplyStatusFilter(List<TBSExecutionState> states)
        {
            // Check if all checkboxes are checked (show all) or all unchecked (show none)
            bool showLive = _chkLive?.IsChecked ?? true;
            bool showMonitoring = _chkMonitoring?.IsChecked ?? true;
            bool showIdle = _chkIdle?.IsChecked ?? true;
            bool showSquaredOff = _chkSquaredOff?.IsChecked ?? true;
            bool showSkipped = _chkSkipped?.IsChecked ?? true;

            // If all are checked, return all states (no filter)
            if (showLive && showMonitoring && showIdle && showSquaredOff && showSkipped)
                return states;

            return states.Where(s =>
            {
                switch (s.Status)
                {
                    case TBSExecutionStatus.Live:
                        return showLive;
                    case TBSExecutionStatus.Monitoring:
                        return showMonitoring;
                    case TBSExecutionStatus.Idle:
                        // Idle but missed goes to Skipped filter
                        return s.IsMissed ? showSkipped : showIdle;
                    case TBSExecutionStatus.SquaredOff:
                        return showSquaredOff;
                    case TBSExecutionStatus.Skipped:
                        return showSkipped;
                    default:
                        return true;
                }
            }).ToList();
        }

        private bool IsAnyFilterActive()
        {
            bool showLive = _chkLive?.IsChecked ?? true;
            bool showMonitoring = _chkMonitoring?.IsChecked ?? true;
            bool showIdle = _chkIdle?.IsChecked ?? true;
            bool showSquaredOff = _chkSquaredOff?.IsChecked ?? true;
            bool showSkipped = _chkSkipped?.IsChecked ?? true;

            // Returns true if any checkbox is unchecked (i.e., filtering is active)
            return !(showLive && showMonitoring && showIdle && showSquaredOff && showSkipped);
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
