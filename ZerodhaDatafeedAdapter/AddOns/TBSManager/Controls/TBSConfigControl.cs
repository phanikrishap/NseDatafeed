using System;
using System.Collections.ObjectModel;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;
using ZerodhaDatafeedAdapter.Logging;
using ZerodhaDatafeedAdapter.Models;
using ZerodhaDatafeedAdapter.Services;
using ZerodhaDatafeedAdapter.ViewModels;

namespace ZerodhaDatafeedAdapter.AddOns.TBSManager.Controls
{
    public class TBSConfigControl : UserControl
    {
        private ListView _configListView;
        private ComboBox _cboUnderlying;
        private TextBox _txtDTE;
        private Button _btnRefresh;
        private TextBlock _lblConfigStatus;
        private TBSViewModel _viewModel;

        public TBSConfigControl(TBSViewModel viewModel)
        {
            _viewModel = viewModel;
            Content = BuildUI();
            
            // Subscribe to VM properties if needed
            if (_viewModel != null)
            {
                _viewModel.PropertyChanged += (s, e) =>
                {
                    if (e.PropertyName == nameof(TBSViewModel.StatusMessage))
                    {
                        Dispatcher.InvokeAsync(() => _lblConfigStatus.Text = _viewModel.StatusMessage);
                    }
                };
            }
        }

        private Grid BuildUI()
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
                Background = TBSStyles.HeaderBg
            };

            filterPanel.Children.Add(new TextBlock
            {
                Text = "Underlying:",
                Foreground = TBSStyles.FgColor,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(5)
            });

            _cboUnderlying = new ComboBox
            {
                Width = 120,
                Margin = new Thickness(5),
                IsEditable = false,
                ItemsSource = _viewModel.AvailableUnderlyings
            };
            _cboUnderlying.SetBinding(ComboBox.SelectedItemProperty, new Binding("SelectedUnderlying") { Source = _viewModel, Mode = BindingMode.TwoWay });
            filterPanel.Children.Add(_cboUnderlying);

            filterPanel.Children.Add(new TextBlock
            {
                Text = "DTE:",
                Foreground = TBSStyles.FgColor,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(15, 5, 5, 5)
            });

            _txtDTE = new TextBox
            {
                Width = 60,
                Margin = new Thickness(5)
            };
            _txtDTE.SetBinding(TextBox.TextProperty, new Binding("SelectedDTE") { Source = _viewModel, Mode = BindingMode.TwoWay, UpdateSourceTrigger = UpdateSourceTrigger.PropertyChanged });
            filterPanel.Children.Add(_txtDTE);

            _btnRefresh = new Button
            {
                Content = "Refresh",
                Margin = new Thickness(15, 5, 5, 5),
                Padding = new Thickness(10, 3, 10, 3)
            };
            _btnRefresh.Click += (s, e) => _viewModel.LoadConfigurations(forceReload: true);
            filterPanel.Children.Add(_btnRefresh);

            Grid.SetRow(filterPanel, 0);
            grid.Children.Add(filterPanel);

            // Config ListView
            _configListView = new ListView
            {
                Background = TBSStyles.BgColor,
                Foreground = TBSStyles.FgColor,
                BorderBrush = TBSStyles.BorderColor,
                BorderThickness = new Thickness(1),
                Margin = new Thickness(5),
                ItemsSource = _viewModel.ConfigRows
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
                Foreground = TBSStyles.FgColor,
                Margin = new Thickness(5),
                Text = _viewModel.StatusMessage
            };
            Grid.SetRow(_lblConfigStatus, 2);
            grid.Children.Add(_lblConfigStatus);

            return grid;
        }

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
    }
}
