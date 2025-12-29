using QABrokerAPI.Common.Enums;
using QABrokerAPI.Common.Utility;
using QABrokerAPI.Zerodha;
using QABrokerAPI.Zerodha.Websockets;
using QANinjaAdapter.Annotations;
using QANinjaAdapter.Classes;
using QANinjaAdapter.Models;
using QANinjaAdapter.Services.Configuration;
using QANinjaAdapter.Services.Instruments;
using QANinjaAdapter.Services.MarketData;
using QANinjaAdapter.Services.Zerodha;
using QANinjaAdapter.ViewModels;
using log4net;
using NinjaTrader.Adapter;
using NinjaTrader.Cbi;
using NinjaTrader.Data;
using NinjaTrader.Gui;
using NinjaTrader.Gui.Tools;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;
using System.Windows;
using System.Xml.Linq;

#nullable disable
namespace QANinjaAdapter
{
    /// <summary>
    /// Main connector class for the QA Ninja Adapter
    /// </summary>
    public class Connector : INotifyPropertyChanged
    {
        private bool _connected;
        private static BrokerClient _client;
        private static Connector _instance;

        private readonly ConfigurationManager _configManager;
        private readonly ZerodhaClient _zerodhaClient;
        private readonly InstrumentManager _instrumentManager;
        private readonly HistoricalDataService _historicalDataService;
        private readonly MarketDataService _marketDataService;

        // Task to track token validation completion
        private Task<bool> _tokenValidationTask;

        /// <summary>
        /// Gets the version of the adapter
        /// </summary>
        public string Version { get; } = "2.0.1";

        /// <summary>
        /// Gets whether the adapter is connected
        /// </summary>
        public bool IsConnected
        {
            get => this._connected;
            private set
            {
                if (this._connected == value)
                    return;
                this._connected = value;
                this.OnPropertyChanged(nameof(IsConnected));
            }
        }

        /// <summary>
        /// Gets the broker client
        /// </summary>
        public static BrokerClient Client
        {
            get
            {
                if (Connector._client == null)
                {
                    ILog logger = LogManager.GetLogger(typeof(Connector));
                    logger.Debug((object)"Connector Debug");

                    // Initialize Zerodha client with access token
                    var configManager = ConfigurationManager.Instance;
                    Connector._client = new BrokerClient(new ClientConfiguration()
                    {
                        ApiKey = configManager.ApiKey,
                        SecretKey = configManager.SecretKey,
                        AccessToken = configManager.AccessToken,
                        Logger = logger
                    });
                }
                return Connector._client;
            }
        }

        private static QAAdapter _qaAdapter;

        /// <summary>
        /// Sets the QAAdapter instance
        /// </summary>
        /// <param name="adapter">The QAAdapter instance</param>
        public static void SetAdapter(QAAdapter adapter)
        {
            _qaAdapter = adapter;
        }

        /// <summary>
        /// Gets the QAAdapter instance
        /// </summary>
        /// <returns>The QAAdapter instance</returns>
        public IAdapter GetAdapter()
        {
            return _qaAdapter;
        }

        /// <summary>
        /// Gets the singleton instance of the Connector
        /// </summary>
        public static Connector Instance
        {
            get
            {
                if (Connector._instance == null)
                    Connector._instance = new Connector();
                return Connector._instance;
            }
        }

        /// <summary>
        /// Constructor
        /// </summary>
        public Connector()
        {
            Logger.Initialize();
            Logger.Info($"QANinjaAdapter v{Version} initializing...");

            _configManager = ConfigurationManager.Instance;
            _zerodhaClient = ZerodhaClient.Instance;
            _instrumentManager = InstrumentManager.Instance;
            _historicalDataService = HistoricalDataService.Instance;
            _marketDataService = MarketDataService.Instance;

            // Load configuration
            if (!_configManager.LoadConfiguration())
            {
                // Handle configuration failure
                MessageBox.Show("Using default API keys. Please check your configuration file.",
                    "Configuration Warning", MessageBoxButton.OK, MessageBoxImage.Warning);
            }

            // Ensure valid access token - store the task so CheckConnection can wait for it
            _tokenValidationTask = Task.Run(async () =>
            {
                try
                {
                    Logger.Info("Checking access token validity...");
                    var tokenResult = await _configManager.EnsureValidTokenAsync();

                    if (tokenResult)
                    {
                        Logger.Info("Access token is valid.");
                        NinjaTrader.NinjaScript.NinjaScript.Log(
                            "[QAAdapter] Zerodha access token is valid.",
                            NinjaTrader.Cbi.LogLevel.Information);
                    }
                    else
                    {
                        Logger.Info("Failed to obtain valid access token. Manual login may be required.");
                        NinjaTrader.NinjaScript.NinjaScript.Log(
                            "[QAAdapter] WARNING: Failed to obtain valid Zerodha access token. Manual login may be required.",
                            NinjaTrader.Cbi.LogLevel.Warning);
                    }
                    return tokenResult;
                }
                catch (Exception ex)
                {
                    var innerMessage = ex.InnerException?.Message ?? ex.Message;
                    Logger.Error($"Token validation error: {innerMessage}");
                    NinjaTrader.NinjaScript.NinjaScript.Log(
                        $"[QAAdapter] Token validation error: {innerMessage}",
                        NinjaTrader.Cbi.LogLevel.Error);
                    return false;
                }
            });
        }

        /// <summary>
        /// Checks the connection to the Zerodha API.
        /// Waits for token validation to complete first if it's still running.
        /// </summary>
        /// <returns>True if the connection is valid, false otherwise</returns>
        public bool CheckConnection()
        {
            // Wait for token validation to complete before checking connection
            if (_tokenValidationTask != null && !_tokenValidationTask.IsCompleted)
            {
                Logger.Info("Waiting for token validation to complete before checking connection...");
                try
                {
                    // Wait with a timeout of 60 seconds for auto token generation
                    bool completed = _tokenValidationTask.Wait(TimeSpan.FromSeconds(60));
                    if (!completed)
                    {
                        Logger.Error("Token validation timed out after 60 seconds.");
                        return false;
                    }
                    Logger.Info($"Token validation completed with result: {_tokenValidationTask.Result}");
                }
                catch (Exception ex)
                {
                    Logger.Error($"Error waiting for token validation: {ex.Message}");
                    return false;
                }
            }

            if (!_zerodhaClient.CheckConnection())
                return false;

            this.IsConnected = true;
            return true;
        }

        /// <summary>
        /// Gets the symbol name with market type
        /// </summary>
        /// <param name="symbol">The symbol</param>
        /// <param name="marketType">The market type</param>
        /// <returns>The symbol name</returns>
        public static string GetSymbolName(string symbol, out MarketType marketType)
        {
            return InstrumentManager.GetSymbolName(symbol, out marketType);
        }

        /// <summary>
        /// Gets the suffix for a market type
        /// </summary>
        /// <param name="marketType">The market type</param>
        /// <returns>The suffix</returns>
        public static string GetSuffix(MarketType marketType)
        {
            return InstrumentManager.GetSuffix(marketType);
        }

        /// <summary>
        /// Registers instruments in NinjaTrader
        /// </summary>
        /// <returns>A task representing the asynchronous operation</returns>
        public async Task RegisterInstruments()
        {
            await _instrumentManager.RegisterSymbols();
        }

        /// <summary>
        /// Gets exchange information for all available instruments
        /// </summary>
        /// <returns>A collection of instrument definitions</returns>
        public async Task<ObservableCollection<InstrumentDefinition>> GetBrokerInformation()
        {
            return await _instrumentManager.GetBrokerInformation();
        }

        /// <summary>
        /// Creates an instrument in NinjaTrader
        /// </summary>
        /// <param name="instrument">The instrument definition to create</param>
        /// <param name="ntSymbolName">The NinjaTrader symbol name</param>
        /// <returns>True if the instrument was created successfully, false otherwise</returns>
        public bool CreateInstrument(InstrumentDefinition instrument, out string ntSymbolName)
        {
            return _instrumentManager.CreateInstrument(instrument, out ntSymbolName);
        }

        /// <summary>
        /// Removes an instrument from NinjaTrader
        /// </summary>
        /// <param name="instrument">The instrument definition to remove</param>
        /// <returns>True if the instrument was removed successfully, false otherwise</returns>
        public bool RemoveInstrument(InstrumentDefinition instrument)
        {
            return _instrumentManager.RemoveInstrument(instrument);
        }

        /// <summary>
        /// Gets all NinjaTrader symbols
        /// </summary>
        /// <returns>A collection of instrument definitions</returns>
        public async Task<ObservableCollection<InstrumentDefinition>> GetNTSymbols()
        {
            return await _instrumentManager.GetNTSymbols();
        }

        /// <summary>
        /// Gets historical trades for a symbol
        /// </summary>
        /// <param name="barsPeriodType">The bars period type</param>
        /// <param name="symbol">The symbol</param>
        /// <param name="fromDate">The start date</param>
        /// <param name="toDate">The end date</param>
        /// <param name="marketType">The market type</param>
        /// <param name="viewModelBase">The view model for progress updates</param>
        /// <returns>A list of historical records</returns>
        public async Task<List<Record>> GetHistoricalTrades(
            BarsPeriodType barsPeriodType,
            string symbol,
            DateTime fromDate,
            DateTime toDate,
            MarketType marketType,
            ViewModelBase viewModelBase)
        {
            return await _historicalDataService.GetHistoricalTrades(
                barsPeriodType,
                symbol,
                fromDate,
                toDate,
                marketType,
                viewModelBase);
        }

        /// <summary>
        /// Subscribes to real-time ticks for a symbol
        /// </summary>
        /// <param name="nativeSymbolName">The native symbol name</param>
        /// <param name="marketType">The market type</param>
        /// <param name="symbol">The symbol</param>
        /// <param name="l1Subscriptions">The L1 subscriptions dictionary</param>
        /// <param name="webSocketConnectionFunc">The WebSocket connection function</param>
        /// <returns>A task representing the asynchronous operation</returns>
        public async Task SubscribeToTicks(
            string nativeSymbolName,
            MarketType marketType,
            string symbol,
            ConcurrentDictionary<string, L1Subscription> l1Subscriptions,
            WebSocketConnectionFunc webSocketConnectionFunc)
        {
            // Use shared WebSocket for efficiency (single connection for all symbols)
            await _marketDataService.SubscribeToTicksShared(
                nativeSymbolName,
                marketType,
                symbol,
                l1Subscriptions,
                webSocketConnectionFunc);
        }

        /// <summary>
        /// Subscribes to market depth for a symbol
        /// </summary>
        /// <param name="nativeSymbolName">The native symbol name</param>
        /// <param name="marketType">The market type</param>
        /// <param name="symbol">The symbol</param>
        /// <param name="l2Subscriptions">The L2 subscriptions dictionary</param>
        /// <param name="webSocketConnectionFunc">The WebSocket connection function</param>
        /// <returns>A task representing the asynchronous operation</returns>
        public async Task SubscribeToDepth(
            string nativeSymbolName,
            MarketType marketType,
            string symbol,
            ConcurrentDictionary<string, L2Subscription> l2Subscriptions,
            WebSocketConnectionFunc webSocketConnectionFunc)
        {
            await _marketDataService.SubscribeToDepth(
                nativeSymbolName,
                marketType,
                symbol,
                l2Subscriptions,
                webSocketConnectionFunc);
        }

        // Removed ClearWrongStocks and FindCCControl as they were based on old logic
        // and may cause UI thread issues. Cleanup logic moved to specialized tools if needed.

        /// <summary>
        /// Clears stocks
        /// </summary>
        public void ClearStocks()
        {
            foreach (MasterInstrument masterInstrument in MasterInstrument.All.Where<MasterInstrument>((Func<MasterInstrument, bool>)(x =>
            {
                if (x.InstrumentType != InstrumentType.Stock)
                    return false;
                return x.Name.EndsWith("_NSE") || x.Name.EndsWith("_MCX") || x.Name.EndsWith("_NFO");
            })).ToArray<MasterInstrument>())
            {
                if (string.IsNullOrEmpty(masterInstrument.Description))
                    masterInstrument.DbRemove();
            }
        }

        /// <summary>
        /// Property changed event
        /// </summary>
        public event PropertyChangedEventHandler PropertyChanged;

        /// <summary>
        /// Raises the property changed event
        /// </summary>
        /// <param name="propertyName">The property name</param>
        [NotifyPropertyChangedInvocator]
        protected virtual void OnPropertyChanged([CallerMemberName] string propertyName = null)
        {
            PropertyChangedEventHandler propertyChanged = this.PropertyChanged;
            if (propertyChanged == null)
                return;
            propertyChanged((object)this, new PropertyChangedEventArgs(propertyName));
        }
    }
}
