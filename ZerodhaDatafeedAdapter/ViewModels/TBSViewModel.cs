using System;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using ZerodhaDatafeedAdapter.Logging;
using ZerodhaDatafeedAdapter.Models;
using ZerodhaDatafeedAdapter.Services;

namespace ZerodhaDatafeedAdapter.ViewModels
{
    /// <summary>
    /// ViewModel for TBS Manager - bridges UI and TBSExecutionService.
    /// Provides data binding properties and commands for the TBS Manager tab pages.
    /// </summary>
    public class TBSViewModel : ViewModelBase
    {
        #region Fields

        private readonly TBSExecutionService _executionService;
        private string _selectedUnderlying = "All";
        private int? _selectedDTE;
        private string _statusMessage = "Ready";
        private bool _isExecutionStarted;
        private PropertyChangedEventHandler _servicePropertyChangedHandler;

        #endregion

        #region Config Tab Properties

        /// <summary>
        /// Collection of TBS configuration entries loaded from Excel
        /// </summary>
        public ObservableCollection<TBSConfigEntry> ConfigRows { get; }

        /// <summary>
        /// Available underlyings for filter dropdown
        /// </summary>
        public ObservableCollection<string> AvailableUnderlyings { get; }

        /// <summary>
        /// Available DTE values for filter dropdown
        /// </summary>
        public ObservableCollection<int> AvailableDTEs { get; }

        /// <summary>
        /// Selected underlying filter (null or "All" for no filter)
        /// </summary>
        public string SelectedUnderlying
        {
            get => _selectedUnderlying;
            set
            {
                if (_selectedUnderlying != value)
                {
                    _selectedUnderlying = value;
                    OnPropertyChanged();
                    ApplyFilter();
                    RefreshExecutionStates();
                }
            }
        }

        /// <summary>
        /// Selected DTE filter (null for no filter)
        /// </summary>
        public int? SelectedDTE
        {
            get => _selectedDTE;
            set
            {
                if (_selectedDTE != value)
                {
                    _selectedDTE = value;
                    OnPropertyChanged();
                    ApplyFilter();
                    RefreshExecutionStates();
                }
            }
        }

        #endregion

        #region Execution Tab Properties - Delegated to Service

        /// <summary>
        /// Collection of execution states for all tranches
        /// </summary>
        public ObservableCollection<TBSExecutionState> ExecutionStates
            => _executionService.ExecutionStates;

        /// <summary>
        /// Total P&L across all Live and SquaredOff tranches (excluding missed)
        /// </summary>
        public decimal TotalPnL => _executionService.TotalPnL;

        /// <summary>
        /// Count of tranches currently Live
        /// </summary>
        public int LiveCount => _executionService.LiveCount;

        /// <summary>
        /// Count of tranches currently in Monitoring state
        /// </summary>
        public int MonitoringCount => _executionService.MonitoringCount;

        /// <summary>
        /// Count of tranches that are Idle
        /// </summary>
        public int IdleCount => _executionService.ExecutionStates.Count(s => s.Status == TBSExecutionStatus.Idle && !s.IsMissed);

        /// <summary>
        /// Count of tranches that are SquaredOff
        /// </summary>
        public int SquaredOffCount => _executionService.ExecutionStates.Count(s => s.Status == TBSExecutionStatus.SquaredOff);

        /// <summary>
        /// Whether Option Chain has finished loading and delay has passed
        /// </summary>
        public bool IsOptionChainReady => _executionService.IsOptionChainReady;

        /// <summary>
        /// Countdown seconds remaining before Option Chain is considered ready
        /// </summary>
        public int DelayCountdown => _executionService.DelayCountdown;

        #endregion

        #region UI State Properties

        /// <summary>
        /// Status message displayed in the status bar
        /// </summary>
        public string StatusMessage
        {
            get => _statusMessage;
            set
            {
                if (_statusMessage != value)
                {
                    _statusMessage = value;
                    OnPropertyChanged();
                }
            }
        }

        /// <summary>
        /// Whether execution monitoring has been started
        /// </summary>
        public bool IsExecutionStarted
        {
            get => _isExecutionStarted;
            private set
            {
                if (_isExecutionStarted != value)
                {
                    _isExecutionStarted = value;
                    OnPropertyChanged();
                }
            }
        }

        /// <summary>
        /// Formatted delay countdown string
        /// </summary>
        public string DelayCountdownText => DelayCountdown > 0 ? $"Waiting for Option Chain ({DelayCountdown}s)" : "";

        /// <summary>
        /// Summary text for the execution tab header
        /// </summary>
        public string ExecutionSummaryText
        {
            get
            {
                var pnlSign = TotalPnL >= 0 ? "+" : "";
                return $"Live: {LiveCount} | Monitoring: {MonitoringCount} | P&L: {pnlSign}{TotalPnL:N2}";
            }
        }

        #endregion

        #region Events

        /// <summary>
        /// Raised when a tranche state changes (for UI updates)
        /// </summary>
        public event EventHandler<TBSExecutionState> TrancheStateChanged;

        #endregion

        #region Constructor

        public TBSViewModel()
        {
            ConfigRows = new ObservableCollection<TBSConfigEntry>();
            AvailableUnderlyings = new ObservableCollection<string> { "All" };
            AvailableDTEs = new ObservableCollection<int>();

            _executionService = TBSExecutionService.Instance;

            // Subscribe to service events with named handler for proper cleanup
            _servicePropertyChangedHandler = OnServicePropertyChanged;
            _executionService.PropertyChanged += _servicePropertyChangedHandler;
            _executionService.StateChanged += OnServiceStateChanged;
            _executionService.SummaryUpdated += OnServiceSummaryUpdated;

            TBSLogger.Info("[TBSViewModel] Initialized");
        }

        #endregion

        #region Config Tab Methods

        /// <summary>
        /// Load configurations from TBSConfigurationService
        /// </summary>
        /// <param name="forceReload">Force reload from Excel file</param>
        public void LoadConfigurations(bool forceReload = false)
        {
            IsBusy = true;
            StatusMessage = "Loading configurations...";

            try
            {
                var configs = TBSConfigurationService.Instance.LoadConfigurations(forceReload);
                ConfigRows.Clear();

                foreach (var config in configs)
                {
                    ConfigRows.Add(config);
                }

                // Update available filters
                UpdateAvailableFilters(configs);

                // Ensure "All" is selected by default after populating
                if (string.IsNullOrEmpty(_selectedUnderlying) || !AvailableUnderlyings.Contains(_selectedUnderlying))
                {
                    _selectedUnderlying = "All";
                    OnPropertyChanged(nameof(SelectedUnderlying));
                }

                // Apply filter to show configs based on current selection and initialize execution states
                ApplyFilter();
                RefreshExecutionStates();

                StatusMessage = $"Loaded {configs.Count} configurations";
                TBSLogger.Info($"[TBSViewModel] Loaded {configs.Count} configurations");
            }
            catch (Exception ex)
            {
                StatusMessage = $"Error: {ex.Message}";
                TBSLogger.Error($"[TBSViewModel] LoadConfigurations error: {ex.Message}");
            }
            finally
            {
                IsBusy = false;
            }
        }

        private void UpdateAvailableFilters(System.Collections.Generic.List<TBSConfigEntry> configs)
        {
            // Update Underlyings
            AvailableUnderlyings.Clear();
            AvailableUnderlyings.Add("All");

            var underlyings = configs.Select(c => c.Underlying).Distinct().OrderBy(u => u);
            foreach (var underlying in underlyings)
            {
                AvailableUnderlyings.Add(underlying);
            }

            // Update DTEs
            AvailableDTEs.Clear();
            var dtes = configs.Select(c => c.DTE).Distinct().OrderBy(d => d);
            foreach (var dte in dtes)
            {
                AvailableDTEs.Add(dte);
            }
        }

        /// <summary>
        /// Apply filter to config list
        /// </summary>
        public void ApplyFilter()
        {
            IsBusy = true;

            try
            {
                string underlying = _selectedUnderlying == "All" ? null : _selectedUnderlying;
                var filtered = TBSConfigurationService.Instance.GetConfigurations(underlying, _selectedDTE);

                ConfigRows.Clear();
                foreach (var config in filtered)
                {
                    ConfigRows.Add(config);
                }

                StatusMessage = $"Showing {filtered.Count} configurations";
            }
            catch (Exception ex)
            {
                StatusMessage = $"Filter error: {ex.Message}";
            }
            finally
            {
                IsBusy = false;
            }
        }

        #endregion

        #region Execution Tab Methods

        /// <summary>
        /// Initialize execution states from configurations with current filters
        /// </summary>
        public void RefreshExecutionStates()
        {
            string underlying = _selectedUnderlying == "All" ? null : _selectedUnderlying;
            _executionService.InitializeExecutionStates(underlying, _selectedDTE);

            // Notify UI of updates
            OnPropertyChanged(nameof(ExecutionStates));
            OnPropertyChanged(nameof(TotalPnL));
            OnPropertyChanged(nameof(LiveCount));
            OnPropertyChanged(nameof(MonitoringCount));
            OnPropertyChanged(nameof(IdleCount));
            OnPropertyChanged(nameof(SquaredOffCount));
            OnPropertyChanged(nameof(ExecutionSummaryText));
        }

        /// <summary>
        /// Start execution monitoring
        /// </summary>
        public void StartExecution()
        {
            if (IsExecutionStarted) return;

            RefreshExecutionStates();
            _executionService.StartMonitoring();
            IsExecutionStarted = true;
            StatusMessage = "Execution started";
            TBSLogger.Info("[TBSViewModel] Execution started");
        }

        /// <summary>
        /// Stop execution monitoring
        /// </summary>
        public void StopExecution()
        {
            if (!IsExecutionStarted) return;

            _executionService.StopMonitoring();
            IsExecutionStarted = false;
            StatusMessage = "Execution stopped";
            TBSLogger.Info("[TBSViewModel] Execution stopped");
        }

        /// <summary>
        /// Called when Option Chain has finished loading
        /// </summary>
        public void OnOptionChainReady()
        {
            _executionService.OnOptionChainReady();
            OnPropertyChanged(nameof(IsOptionChainReady));
            OnPropertyChanged(nameof(DelayCountdown));
            OnPropertyChanged(nameof(DelayCountdownText));
        }

        /// <summary>
        /// Called when Option Chain generates options for an underlying/expiry.
        /// Auto-sets the DTE filter based on the selected expiry.
        /// </summary>
        public void OnOptionsGenerated(string underlying, DateTime? expiry)
        {
            _executionService.OnOptionsGenerated(underlying, expiry);

            // Auto-set filter based on Option Chain selection
            if (!string.IsNullOrEmpty(underlying))
            {
                // Set underlying filter (or "All" to show all underlyings)
                _selectedUnderlying = underlying;
                OnPropertyChanged(nameof(SelectedUnderlying));
            }

            if (expiry.HasValue)
            {
                // Compute DTE from expiry
                int dte = (int)(expiry.Value.Date - DateTime.Today).TotalDays;
                _selectedDTE = dte;
                OnPropertyChanged(nameof(SelectedDTE));
                TBSLogger.Info($"[TBSViewModel] OnOptionsGenerated: Auto-set DTE={dte} from expiry={expiry.Value:dd-MMM-yyyy}");
            }

            // Apply filter and refresh execution states with new DTE
            ApplyFilter();
            RefreshExecutionStates();
        }

        #endregion

        #region Service Event Handlers

        private void OnServicePropertyChanged(object sender, PropertyChangedEventArgs e)
        {
            // Forward relevant property changes to UI
            switch (e.PropertyName)
            {
                case nameof(TBSExecutionService.IsOptionChainReady):
                    OnPropertyChanged(nameof(IsOptionChainReady));
                    OnPropertyChanged(nameof(DelayCountdownText));
                    break;

                case nameof(TBSExecutionService.DelayCountdown):
                    OnPropertyChanged(nameof(DelayCountdown));
                    OnPropertyChanged(nameof(DelayCountdownText));
                    break;

                case nameof(TBSExecutionService.TotalPnL):
                    OnPropertyChanged(nameof(TotalPnL));
                    OnPropertyChanged(nameof(ExecutionSummaryText));
                    break;

                case nameof(TBSExecutionService.LiveCount):
                    OnPropertyChanged(nameof(LiveCount));
                    OnPropertyChanged(nameof(ExecutionSummaryText));
                    break;

                case nameof(TBSExecutionService.MonitoringCount):
                    OnPropertyChanged(nameof(MonitoringCount));
                    OnPropertyChanged(nameof(ExecutionSummaryText));
                    break;
            }
        }

        private void OnServiceStateChanged(object sender, TBSExecutionState state)
        {
            // Forward state changes to UI
            TrancheStateChanged?.Invoke(this, state);

            // Update summary counts
            OnPropertyChanged(nameof(LiveCount));
            OnPropertyChanged(nameof(MonitoringCount));
            OnPropertyChanged(nameof(IdleCount));
            OnPropertyChanged(nameof(SquaredOffCount));
            OnPropertyChanged(nameof(ExecutionSummaryText));
        }

        private void OnServiceSummaryUpdated(object sender, EventArgs e)
        {
            // Update all summary properties
            OnPropertyChanged(nameof(TotalPnL));
            OnPropertyChanged(nameof(LiveCount));
            OnPropertyChanged(nameof(MonitoringCount));
            OnPropertyChanged(nameof(IdleCount));
            OnPropertyChanged(nameof(SquaredOffCount));
            OnPropertyChanged(nameof(ExecutionSummaryText));
        }

        #endregion

        #region Cleanup

        /// <summary>
        /// Cleanup ViewModel and unsubscribe from service events
        /// </summary>
        public void Cleanup()
        {
            if (_servicePropertyChangedHandler != null)
            {
                _executionService.PropertyChanged -= _servicePropertyChangedHandler;
                _servicePropertyChangedHandler = null;
            }

            _executionService.StateChanged -= OnServiceStateChanged;
            _executionService.SummaryUpdated -= OnServiceSummaryUpdated;

            StopExecution();
            ConfigRows.Clear();

            TBSLogger.Info("[TBSViewModel] Cleaned up");
        }

        #endregion
    }
}
