using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Newtonsoft.Json;
using ZerodhaDatafeedAdapter.Classes;
using ZerodhaDatafeedAdapter.Services.Analysis;

namespace ZerodhaDatafeedAdapter.SyntheticInstruments
{
    /// <summary>
    /// Core service for managing synthetic straddles. 
    /// Bridges the gap between raw leg ticks and injected synthetic ticks.
    /// </summary>
    public class SyntheticStraddleService : IDisposable
    {
        private readonly ZerodhaAdapter _adapter;
        private readonly List<StraddleDefinition> _definitions;
        private readonly ConcurrentDictionary<string, StraddleState> _activeStraddles;
        private readonly ConcurrentDictionary<string, List<string>> _legToSyntheticMapping;
        
        private readonly AsyncStraddleProcessor _asyncProcessor;
        private readonly StraddleCacheManager _cacheManager;
        
        private bool _isDisposed = false;
        private readonly object _lock = new object();

        /// <summary>
        /// Event fired when a straddle price is calculated (for UI updates like Option Chain)
        /// Parameters: straddleSymbol, price, cePrice, pePrice
        /// </summary>
        public event Action<string, double, double, double> StraddlePriceCalculated;

        /// <summary>
        /// Initialises a new instance of the SyntheticStraddleService.
        /// </summary>
        /// <param name="adapter">The parent adapter for publishing ticks.</param>
        public SyntheticStraddleService(ZerodhaAdapter adapter)
        {
            _adapter = adapter ?? throw new ArgumentNullException(nameof(adapter));
            _definitions = new List<StraddleDefinition>();
            _activeStraddles = new ConcurrentDictionary<string, StraddleState>();
            _legToSyntheticMapping = new ConcurrentDictionary<string, List<string>>();
            
            // Initialise helper components
            _cacheManager = new StraddleCacheManager();
            _asyncProcessor = new AsyncStraddleProcessor(adapter);
            
            // Load configuration
            LoadConfigurations();
            
            // Initialize processor with definitions
            _asyncProcessor.LoadStraddleConfigurations(_definitions);

            // Forward straddle price events to reactive hub (primary) and legacy subscribers
            _asyncProcessor.StraddlePriceCalculated += (symbol, price, cePrice, pePrice) =>
            {
                // Publish to reactive hub (primary - enables batching and streaming)
                MarketDataReactiveHub.Instance.PublishStraddlePrice(symbol, price, cePrice, pePrice);

                // Legacy event for backward compatibility (can be removed once all consumers use hub)
                StraddlePriceCalculated?.Invoke(symbol, price, cePrice, pePrice);
            };

            Logger.Info($"[SyntheticStraddleService] Initialised with {_definitions.Count} straddle definitions.");
        }

        /// <summary>
        /// Loads straddle configurations from a local JSON file.
        /// </summary>
        private void LoadConfigurations()
        {
            try
            {
                string documentsPath = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
                string configPath = Path.Combine(documentsPath, Constants.BaseDataFolder, "straddles_config.json");

                if (!File.Exists(configPath))
                {
                    Logger.Warn($"[SyntheticStraddleService] Configuration file not found at: {configPath}. Synthetic straddles will be disabled.");
                    return;
                }

                string json = File.ReadAllText(configPath);
                var config = JsonConvert.DeserializeObject<StraddleConfigRoot>(json);

                if (config?.Straddles != null)
                {
                    foreach (var def in config.Straddles)
                    {
                        RegisterStraddle(def);
                    }
                }
            }
            catch (Exception ex)
            {
                Logger.Error($"[SyntheticStraddleService] Error loading configurations: {ex.Message}");
            }
        }

        /// <summary>
        /// Reloads straddle configurations from the JSON file.
        /// Call this after regenerating straddles_config.json at runtime.
        /// </summary>
        public void ReloadConfigurations()
        {
            lock (_lock)
            {
                if (_isDisposed) return;

                try
                {
                    Logger.Info("[SyntheticStraddleService] Reloading straddle configurations...");

                    // Clear existing definitions and mappings
                    _definitions.Clear();
                    _legToSyntheticMapping.Clear();

                    // Reload from file
                    LoadConfigurations();

                    // Reload async processor with new definitions
                    _asyncProcessor.ReloadConfigurations(_definitions);

                    Logger.Info($"[SyntheticStraddleService] Reloaded with {_definitions.Count} straddle definitions.");
                }
                catch (Exception ex)
                {
                    Logger.Error($"[SyntheticStraddleService] Error reloading configurations: {ex.Message}");
                }
            }
        }

        /// <summary>
        /// Registers a single straddle definition and sets up leg mappings.
        /// </summary>
        private void RegisterStraddle(StraddleDefinition def)
        {
            _definitions.Add(def);

            // Map CE leg to this synthetic symbol
            _legToSyntheticMapping.AddOrUpdate(def.CESymbol,
                new List<string> { def.SyntheticSymbolNinjaTrader },
                (key, list) => { list.Add(def.SyntheticSymbolNinjaTrader); return list; });

            // Map PE leg to this synthetic symbol
            _legToSyntheticMapping.AddOrUpdate(def.PESymbol,
                new List<string> { def.SyntheticSymbolNinjaTrader },
                (key, list) => { list.Add(def.SyntheticSymbolNinjaTrader); return list; });

            // Use DEBUG level to avoid log flooding when registering many straddles
            Logger.Debug($"[SyntheticStraddleService] Registered straddle: {def.SyntheticSymbolNinjaTrader} (Legs: {def.CESymbol}, {def.PESymbol})");
        }

        /// <summary>
        /// Processes an incoming tick for an instrument that might be a leg of a straddle.
        /// </summary>
        public void ProcessLegTick(string instrumentSymbol, double price, long volume, DateTime timestamp, TickType tickType, double bid = 0, double ask = 0)
        {
            if (_isDisposed) return;
            
            // Optimization: Quick check if this instrument is even a leg
            if (!_legToSyntheticMapping.ContainsKey(instrumentSymbol))
                return;

            // Log the leg tick for debugging (if enabled)
            SyntheticDataLogger.LogLegTickProcessing(instrumentSymbol, price, volume, _legToSyntheticMapping[instrumentSymbol].Count);

            // Create standardized tick
            var tick = new Tick
            {
                InstrumentSymbol = instrumentSymbol,
                Price = price,
                Volume = volume,
                Timestamp = timestamp,
                Type = tickType,
                BidPrice = bid,
                AskPrice = ask
            };

            // Delegate to async processor for high performance
            _asyncProcessor.QueueTickForProcessing(instrumentSymbol, tick);
        }

        /// <summary>
        /// Checks if a NinjaTrader symbol name is a known synthetic straddle symbol.
        /// </summary>
        public bool IsSyntheticSymbol(string ntSymbolName)
        {
            return _definitions.Any(d => d.SyntheticSymbolNinjaTrader.Equals(ntSymbolName, StringComparison.OrdinalIgnoreCase));
        }

        /// <summary>
        /// Checks if an instrument symbol is a leg of any registered synthetic straddle.
        /// </summary>
        public bool IsLegInstrument(string instrumentSymbol)
        {
            return _legToSyntheticMapping.ContainsKey(instrumentSymbol);
        }

        /// <summary>
        /// Returns the straddle definition for a given synthetic symbol.
        /// </summary>
        public StraddleDefinition GetDefinition(string syntheticSymbol)
        {
            return _definitions.FirstOrDefault(d => d.SyntheticSymbolNinjaTrader.Equals(syntheticSymbol, StringComparison.OrdinalIgnoreCase));
        }
        
        /// <summary>
        /// Returns all registered straddle symbols.
        /// </summary>
        public IEnumerable<string> GetAllSyntheticSymbols()
        {
            return _definitions.Select(d => d.SyntheticSymbolNinjaTrader);
        }

        public void Dispose()
        {
            lock (_lock)
            {
                if (_isDisposed) return;
                _isDisposed = true;
                
                _asyncProcessor?.Dispose();
                _cacheManager?.Dispose();
                
                Logger.Info("[SyntheticStraddleService] Disposed.");
            }
        }

        // Helper class for JSON deserialization
        private class StraddleConfigRoot
        {
            public List<StraddleDefinition> Straddles { get; set; }
        }
    }
}
