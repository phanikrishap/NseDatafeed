using ZerodhaAdapterAddOn.ViewModels;
using ZerodhaAPI.Common.Enums;
using ZerodhaAPI.Zerodha.Websockets;
using ZerodhaDatafeedAdapter.Classes;
using ZerodhaDatafeedAdapter.Controls;
using ZerodhaDatafeedAdapter.Helpers;
using ZerodhaDatafeedAdapter.Models;
using ZerodhaDatafeedAdapter.Models.MarketData;
using ZerodhaDatafeedAdapter.Services.MarketData;
using ZerodhaDatafeedAdapter.ViewModels;
using NinjaTrader.Adapter;
using NinjaTrader.Cbi;
using NinjaTrader.Core;
using NinjaTrader.Data;
using NinjaTrader.NinjaScript;
using NinjaTrader.NinjaScript.Adapters;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using NinjaTrader.CQG.ProtoBuf;
using System.Data.SQLite;
using ZerodhaDatafeedAdapter.SyntheticInstruments;

#nullable disable
namespace ZerodhaDatafeedAdapter
{
    /// <summary>
    /// ZerodhaAdapter - Core adapter for NinjaTrader integration with Zerodha data feed.
    /// Split into partial classes for maintainability:
    /// - ZerodhaAdapter.cs: Core adapter lifecycle, connection management
    /// - ZerodhaAdapter.MarketData.cs: Market data subscriptions (L1/L2)
    /// - ZerodhaAdapter.Historical.cs: Historical bars requests
    /// - ZerodhaAdapter.Synthetic.cs: Synthetic straddle handling
    /// </summary>
    public partial class ZerodhaAdapter : AdapterBase, IAdapter, IDisposable
    {
        private IConnection _zerodhaConncetion;
        private ZerodhaConnectorOptions _options;
        private readonly ConcurrentDictionary<string, L1Subscription> _l1Subscriptions = new ConcurrentDictionary<string, L1Subscription>();
        private readonly ConcurrentDictionary<string, L2Subscription> _l2Subscriptions = new ConcurrentDictionary<string, L2Subscription>();
        private readonly HashSet<string> _marketLiveDataSymbols = new HashSet<string>();
        private readonly HashSet<string> _marketDepthDataSymbols = new HashSet<string>();
        private static readonly object _lockLiveSymbol = new object();
        private static readonly object _lockDepthSymbol = new object();
        private readonly object _marketDataLock = new object(); // Added for synthetic straddle data synchronization

        // Instance tracking
        private static int _instanceCounter = 0;
        private readonly int _instanceId;

        // Cleanup timer for stale callback removal
        private Timer _cleanupTimer;
        private static readonly TimeSpan CleanupInterval = TimeSpan.FromMinutes(30);
        private static readonly TimeSpan MaxCallbackIdleTime = TimeSpan.FromMinutes(60);

        public ZerodhaAdapter()
        {
            _instanceId = Interlocked.Increment(ref _instanceCounter);
            Logger.Info($"[ZerodhaAdapter] INSTANCE CREATED: ID={_instanceId}");
        }

        private SyntheticInstruments.SyntheticStraddleService _syntheticStraddleService;

        /// <summary>
        /// Gets the SyntheticStraddleService for straddle price events
        /// </summary>
        public SyntheticInstruments.SyntheticStraddleService SyntheticStraddleService => _syntheticStraddleService;

        // Removed LogMe: Use Logger.Info or NinjaTrader native logging directly.

        public void Connect(IConnection connection)
        {
            Logger.Info("ZerodhaAdapter: Initializing and connecting adapter...");
            this._zerodhaConncetion = connection;
            this._options = (ZerodhaConnectorOptions)this._zerodhaConncetion.Options;
            
            // Set the adapter instance in the Connector class
            Connector.SetAdapter(this);
            
            // Initialize the SyntheticStraddleService
            _syntheticStraddleService = new SyntheticInstruments.SyntheticStraddleService(this);
            
            this._zerodhaConncetion.OrderTypes = new NinjaTrader.Cbi.OrderType[4]
            {
                NinjaTrader.Cbi.OrderType.Market,
                NinjaTrader.Cbi.OrderType.Limit,
                NinjaTrader.Cbi.OrderType.StopMarket,
                NinjaTrader.Cbi.OrderType.StopLimit
            };
            this._zerodhaConncetion.TimeInForces = new NinjaTrader.Cbi.TimeInForce[3]
            {
                NinjaTrader.Cbi.TimeInForce.Day,
                NinjaTrader.Cbi.TimeInForce.Gtc,
                NinjaTrader.Cbi.TimeInForce.Gtd
            };
            this._zerodhaConncetion.Features = new NinjaTrader.Cbi.Feature[10]
            {
                NinjaTrader.Cbi.Feature.Bars1Minute,
                NinjaTrader.Cbi.Feature.BarsDaily,
                NinjaTrader.Cbi.Feature.BarsTick,
                NinjaTrader.Cbi.Feature.BarsTickIntraday,
                NinjaTrader.Cbi.Feature.MarketData,
                NinjaTrader.Cbi.Feature.AtmStrategies,
                NinjaTrader.Cbi.Feature.Order,
                NinjaTrader.Cbi.Feature.OrderChange,
                NinjaTrader.Cbi.Feature.CustomOrders,
                NinjaTrader.Cbi.Feature.MarketDepth
            };
            this._zerodhaConncetion.InstrumentTypes = new InstrumentType[3]
            {
                InstrumentType.Stock,
                InstrumentType.Future,
                InstrumentType.Option
            };
            this._zerodhaConncetion.MarketDataTypes = new MarketDataType[1]
            {
                MarketDataType.Last
            };
            this.Connect();
        }

        private async void Connect()
        {
            if (this._zerodhaConncetion.Status == ConnectionStatus.Connecting)
            {
                if (Connector.Instance.CheckConnection())
                {
                    Logger.Info("ZerodhaAdapter: Connection to provider (Zerodha) successful.");
                    this.SetInstruments();
                    this._zerodhaConncetion.ConnectionStatusCallback(ConnectionStatus.Connected, ConnectionStatus.Connected, ErrorCode.NoError, "");

                    // Start the cleanup timer to remove stale callbacks periodically
                    StartCleanupTimer();

                    await Connector.Instance.RegisterInstruments();
                }
                else
                    this._zerodhaConncetion.ConnectionStatusCallback(ConnectionStatus.Disconnected, ConnectionStatus.Disconnected, ErrorCode.LogOnFailed, "Unable to connect to provider Zerodha.");
            }
            else
                this.Disconnect();
        }

        private void SetInstruments()
        {
            DataContext.Instance.SymbolNames.Clear();
            // Changed from CryptoCurrency to Stock for Zerodha
            foreach (MasterInstrument masterInstrument in MasterInstrument.All.Where<MasterInstrument>((Func<MasterInstrument, bool>)(x =>
                (x.InstrumentType == InstrumentType.Stock ||
                 x.InstrumentType == InstrumentType.Future ||
                 x.InstrumentType == InstrumentType.Option) &&
                !string.IsNullOrEmpty(((IEnumerable<string>)x.ProviderNames).ElementAtOrDefault<string>(Constants.ProviderId)))))
            {
                if (!DataContext.Instance.SymbolNames.ContainsKey(masterInstrument.Name))
                    DataContext.Instance.SymbolNames.Add(masterInstrument.Name, masterInstrument.ProviderNames[Constants.ProviderId]);
            }
        }

        protected override void OnStateChange()
        {
            if (this.State == State.SetDefaults)
            {
                this.Name = "Zerodha";
            }
            if (this.State != State.Configure)
                return;
        }

        public void Disconnect()
        {
            Logger.Info($"[ZerodhaAdapter] [Instance={_instanceId}] Disconnect called, cleaning up...");

            // Stop the cleanup timer
            StopCleanupTimer();

            // Clear all callbacks from L1 subscriptions to prevent memory leaks
            CleanupAllSubscriptions();

            lock (ZerodhaAdapter._lockLiveSymbol)
                this._marketLiveDataSymbols?.Clear();
            lock (ZerodhaAdapter._lockDepthSymbol)
                this._marketDepthDataSymbols?.Clear();
            if (this._zerodhaConncetion.Status == ConnectionStatus.Disconnected)
                return;
            this._zerodhaConncetion.ConnectionStatusCallback(ConnectionStatus.Disconnected, ConnectionStatus.Disconnected, ErrorCode.NoError, string.Empty);
        }

        public void ResolveInstrument(Instrument instrument, Action<Instrument, ErrorCode, string> callback) { }

        public void SubscribeFundamentalData(Instrument instrument, Action<FundamentalDataType, object> callback) { }

        public void UnsubscribeFundamentalData(Instrument instrument) { }

        // Market data methods moved to ZerodhaAdapter.MarketData.cs

        // Trading methods - not implemented (data feed adapter only)
        // These empty implementations satisfy the IAdapter interface requirements
        public void Cancel(NinjaTrader.Cbi.Order[] orders) { }

        public void Change(NinjaTrader.Cbi.Order[] orders) { }

        public void Submit(NinjaTrader.Cbi.Order[] orders) { }

        public void SubscribeAccount(NinjaTrader.Cbi.Account account) { }

        public void UnsubscribeAccount(NinjaTrader.Cbi.Account account) { }

        /// <summary>
        /// Checks if a symbol is an index (no volume traded, price updates only).
        /// </summary>
        private bool IsIndexSymbol(string symbolName) => SymbolHelper.IsIndexSymbol(symbolName);

        // Historical data methods moved to ZerodhaAdapter.Historical.cs

        public void OnMarketDepthReceived(Quote quote) { }

        public void RequestHotlistNames(Action<string[], ErrorCode, string> callback) { }

        public void SubscribeHotlist(Hotlist hotlist, Action callback) { }

        public void UnsubscribeHotlist(Hotlist hotlist) { }

        public void SubscribeNews() { }

        public void UnsubscribeNews() { }

        // Synthetic straddle methods moved to ZerodhaAdapter.Synthetic.cs

        #region Cleanup Timer Methods

        private void StartCleanupTimer()
        {
            StopCleanupTimer(); // Ensure no duplicate timers
            _cleanupTimer = new Timer(CleanupTimerCallback, null, CleanupInterval, CleanupInterval);
            Logger.Info($"[ZerodhaAdapter] [Instance={_instanceId}] Cleanup timer started (interval: {CleanupInterval.TotalMinutes} min, max idle: {MaxCallbackIdleTime.TotalMinutes} min)");
        }

        private void StopCleanupTimer()
        {
            if (_cleanupTimer != null)
            {
                _cleanupTimer.Dispose();
                _cleanupTimer = null;
                Logger.Debug($"[ZerodhaAdapter] [Instance={_instanceId}] Cleanup timer stopped");
            }
        }

        private void CleanupTimerCallback(object state)
        {
            try
            {
                int totalRemoved = 0;
                foreach (var kvp in _l1Subscriptions)
                {
                    totalRemoved += kvp.Value.CleanupStaleCallbacks(MaxCallbackIdleTime);
                }

                if (totalRemoved > 0)
                {
                    Logger.Info($"[ZerodhaAdapter] [Instance={_instanceId}] Cleanup timer: Removed {totalRemoved} stale callbacks across all subscriptions");
                }
            }
            catch (Exception ex)
            {
                Logger.Error($"[ZerodhaAdapter] [Instance={_instanceId}] Error in cleanup timer: {ex.Message}");
            }
        }

        private void CleanupAllSubscriptions()
        {
            int totalCallbacks = 0;
            foreach (var kvp in _l1Subscriptions)
            {
                totalCallbacks += kvp.Value.CallbackCount;
                kvp.Value.ClearAllCallbacks();
            }
            Logger.Info($"[ZerodhaAdapter] [Instance={_instanceId}] CleanupAllSubscriptions: Cleared {totalCallbacks} callbacks from {_l1Subscriptions.Count} subscriptions");
        }

        #endregion

        public void Dispose()
        {
            Logger.Info($"[ZerodhaAdapter] [Instance={_instanceId}] Dispose called");
            StopCleanupTimer();
            _syntheticStraddleService?.Dispose();
            this.Disconnect();
            this._l1Subscriptions.Clear();
            this._l2Subscriptions.Clear();
        }

        // PublishSyntheticTickData and ProcessSyntheticLegTick moved to ZerodhaAdapter.Synthetic.cs
    }
}
