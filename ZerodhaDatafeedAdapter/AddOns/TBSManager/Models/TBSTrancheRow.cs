using System;
using System.ComponentModel;
using System.Linq;
using System.Runtime.CompilerServices;
using ZerodhaDatafeedAdapter.Models;

namespace ZerodhaDatafeedAdapter.AddOns.TBSManager.Models
{
    /// <summary>
    /// Flattened row model for FilterDataGrid display of TBS tranches.
    /// Wraps TBSExecutionState and provides flat properties for column binding.
    /// </summary>
    public class TBSTrancheRow : INotifyPropertyChanged
    {
        public event PropertyChangedEventHandler PropertyChanged;

        private readonly TBSExecutionState _state;

        public TBSTrancheRow(TBSExecutionState state)
        {
            _state = state ?? throw new ArgumentNullException(nameof(state));
            _state.PropertyChanged += (s, e) => RefreshFromState();

            // Subscribe to leg changes
            foreach (var leg in _state.Legs)
            {
                leg.PropertyChanged += (s, e) => RefreshFromState();
            }
        }

        /// <summary>
        /// Reference to underlying execution state (for detail view)
        /// </summary>
        public TBSExecutionState State => _state;

        #region Tranche Properties

        public int TrancheId => _state.TrancheId;
        public string EntryTime => _state.EntryTimeDisplay;
        public int Quantity => _state.Quantity;
        public string Strike => _state.StrikeDisplay;
        public string EntryPrice => _state.EntryPriceDisplay;
        public string ExitPrice => _state.ExitPriceDisplay;
        public string ExitTime => _state.ExitTimeDisplay;

        public string Status => _state.StatusText;
        public TBSExecutionStatus StatusEnum => _state.Status;

        public decimal PnL => _state.CombinedPnL;
        public string PnLDisplay => _state.CombinedPnL != 0 ? _state.CombinedPnL.ToString("F2") : "-";

        public string StoxxoName => _state.StoxxoPortfolioNameDisplay;
        public string StoxxoStatus => _state.StoxxoStatusDisplay;
        public decimal StoxxoPnL => _state.StoxxoPnL;
        public string StoxxoPnLDisplay => _state.StoxxoPnLDisplay;

        public string Message => _state.Message;

        #endregion

        #region CE Leg Properties

        private TBSLegState CELeg => _state.Legs?.FirstOrDefault(l => l.OptionType == "CE");

        public string CESymbol => CELeg?.SymbolDisplay ?? "-";
        public string CEEntry => CELeg?.EntryPriceDisplay ?? "-";
        public string CELTP => CELeg?.CurrentPriceDisplay ?? "-";
        public string CESL => CELeg?.SLPriceDisplay ?? "-";
        public string CEExit => CELeg?.ExitPriceDisplay ?? "-";
        public string CEExitTime => CELeg?.ExitTimeDisplay ?? "-";
        public string CEPnL => CELeg?.PnLDisplay ?? "-";
        public decimal CEPnLValue => CELeg?.PnL ?? 0;
        public string CEStatus => CELeg?.StatusText ?? "-";
        public string CESLStatus => CELeg?.SLStatus ?? "-";
        public string CEStoxxoEntry => CELeg?.StoxxoEntryPriceDisplay ?? "-";
        public string CEStoxxoExit => CELeg?.StoxxoExitPriceDisplay ?? "-";
        public string CEStoxxoStatus => CELeg?.StoxxoStatusDisplay ?? "-";

        #endregion

        #region PE Leg Properties

        private TBSLegState PELeg => _state.Legs?.FirstOrDefault(l => l.OptionType == "PE");

        public string PESymbol => PELeg?.SymbolDisplay ?? "-";
        public string PEEntry => PELeg?.EntryPriceDisplay ?? "-";
        public string PELTP => PELeg?.CurrentPriceDisplay ?? "-";
        public string PESL => PELeg?.SLPriceDisplay ?? "-";
        public string PEExit => PELeg?.ExitPriceDisplay ?? "-";
        public string PEExitTime => PELeg?.ExitTimeDisplay ?? "-";
        public string PEPnL => PELeg?.PnLDisplay ?? "-";
        public decimal PEPnLValue => PELeg?.PnL ?? 0;
        public string PEStatus => PELeg?.StatusText ?? "-";
        public string PESLStatus => PELeg?.SLStatus ?? "-";
        public string PEStoxxoEntry => PELeg?.StoxxoEntryPriceDisplay ?? "-";
        public string PEStoxxoExit => PELeg?.StoxxoExitPriceDisplay ?? "-";
        public string PEStoxxoStatus => PELeg?.StoxxoStatusDisplay ?? "-";

        #endregion

        /// <summary>
        /// Refresh all properties from underlying state
        /// </summary>
        public void RefreshFromState()
        {
            OnPropertyChanged(string.Empty); // Refresh all
        }

        protected void OnPropertyChanged([CallerMemberName] string propertyName = null)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }
    }
}
