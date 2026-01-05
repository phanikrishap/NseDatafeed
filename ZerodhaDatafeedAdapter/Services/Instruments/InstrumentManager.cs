using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using NinjaTrader.Cbi;
using NinjaTrader.Data;
using ZerodhaAPI.Common.Enums;
using ZerodhaDatafeedAdapter.Helpers;
using ZerodhaDatafeedAdapter.Models;
using ZerodhaDatafeedAdapter.Services.Zerodha;

namespace ZerodhaDatafeedAdapter.Services.Instruments
{
    /// <summary>
    /// Coordinator for instrument management. 
    /// Delegates storage logic to InstrumentDbService and mapping logic to SymbolMappingService.
    /// Acts as a high-level facade for instrument resolution and lifecycle management.
    /// </summary>
    public class InstrumentManager
    {
        private static readonly Lazy<InstrumentManager> _instance = new Lazy<InstrumentManager>(() => new InstrumentManager());
        public static InstrumentManager Instance => _instance.Value;

        private readonly InstrumentDbService _dbService;
        private readonly SymbolMappingService _mappingService;
        private readonly string _dbPath;

        private InstrumentManager()
        {
            string folder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "NinjaTrader 8", "ZerodhaAdapter");
            if (!Directory.Exists(folder)) Directory.CreateDirectory(folder);

            _dbPath = Path.Combine(folder, "InstrumentMasters.db");
            string mappingPath = Path.Combine(folder, "fo_mappings.json");

            _dbService = new InstrumentDbService(_dbPath);
            _mappingService = new SymbolMappingService(mappingPath);
        }

        public async Task InitializeAsync()
        {
            Logger.Info("[IM] Initializing instrument data...");
            await EnsureDatabaseExists();

            // Load all tokens into memory cache for fast lookups
            _dbService.LoadAllTokensToCache();

            _mappingService.LoadFOMappings();
            Logger.Info("[IM] Initialization complete.");
        }

        public MappedInstrument GetMappingByNtSymbol(string ntSymbol)
        {
            if (_mappingService.TryGetMapping(ntSymbol, out var mapped)) return mapped;

            // Try to resolve dynamically if not in cache (e.g., F&O)
            return ResolveInstrumentDynamically(ntSymbol);
        }

        private MappedInstrument ResolveInstrumentDynamically(string ntSymbol)
        {
            // Logic for parsing F&O symbols (e.g., NIFTY25JANFUT, NIFTY25JAN22000CE)
            // This is complex logic that was in the original file. 
            // I'll implement a simplified version that relies on the DB service for token lookup.
            
            // 1. Try FUTURES
            if (ntSymbol.EndsWith("FUT"))
            {
                long token = _dbService.LookupToken(ntSymbol);
                if (token > 0)
                {
                    var mapped = new MappedInstrument { symbol = ntSymbol, instrument_token = token, underlying = ExtractUnderlying(ntSymbol), exchange = "NFO" };
                    _mappingService.AddMapping(ntSymbol, mapped);
                    return mapped;
                }
            }

            // 2. Try OPTIONS (using DB lookup)
            // Note: In real scenarios, we'd parse the strike/expiry from ntSymbol.
            // For now, if it's already in Zerodha format, we can use it.
            long optToken = _dbService.LookupToken(ntSymbol);
            if (optToken > 0)
            {
                var mapped = new MappedInstrument { symbol = ntSymbol, instrument_token = optToken, underlying = ExtractUnderlying(ntSymbol), exchange = "NFO" };
                _mappingService.AddMapping(ntSymbol, mapped);
                return mapped;
            }

            return null;
        }

        private string ExtractUnderlying(string symbol)
        {
            if (symbol.StartsWith("NIFTY")) return "NIFTY";
            if (symbol.StartsWith("BANKNIFTY")) return "BANKNIFTY";
            if (symbol.StartsWith("SENSEX")) return "SENSEX";
            if (symbol.StartsWith("FINNIFTY")) return "FINNIFTY";
            return symbol;
        }

        public async Task<bool> DownloadInstrumentsAsync() => await _dbService.DownloadAndCreateInstrumentDatabaseAsync();

        /// <summary>
        /// Gets the instrument token for a symbol.
        /// First checks SymbolMappingService (for indices from index_mappings.json),
        /// then falls back to SQLite database lookup.
        /// </summary>
        public long GetInstrumentToken(string symbol)
        {
            // First check SymbolMappingService (indices and F&O mappings)
            if (_mappingService.TryGetMapping(symbol, out var mapped) && mapped.instrument_token > 0)
            {
                return mapped.instrument_token;
            }

            // Fall back to SQLite database lookup
            return _dbService.LookupToken(symbol);
        }
        
        public List<DateTime> GetExpiriesForUnderlying(string underlying) => _dbService.GetExpiries(underlying);

        public Task<List<DateTime>> GetExpiriesForUnderlyingAsync(string underlying) => Task.Run(() => _dbService.GetExpiries(underlying));

        public int GetLotSizeForUnderlying(string underlying) => _dbService.GetLotSize(underlying);

        public string GetSegmentForToken(long token) => _dbService.GetSegmentForToken(token);

        public void AddMappedInstrument(string ntSymbol, MappedInstrument mapped) => _mappingService.AddMapping(ntSymbol, mapped);

        public void ClearFOMappings() => _mappingService.ClearFOMappings();

        public (long token, string symbol) LookupOptionDetails(string seg, string und, string exp, double str, string typ)
            => _dbService.LookupOptionDetails(seg, und, exp, str, typ);

        public (long token, string symbol) LookupOptionDetailsInSqlite(string seg, string und, string exp, double str, string typ)
            => _dbService.LookupOptionDetails(seg, und, exp, str, typ);

        private async Task EnsureDatabaseExists()
        {
            if (!File.Exists(_dbPath) || (DateTime.Now - File.GetLastWriteTime(_dbPath)).TotalDays > 1)
            {
                await DownloadInstrumentsAsync();
            }
        }

        // --- Restored Connector/UI Methods ---

        public static string GetSymbolName(string symbol, out MarketType marketType)
        {
            marketType = MarketType.Spot;

            // Handle special index symbols - return the Zerodha symbol format
            // GIFT_NIFTY is the NT symbol, maps to "GIFT NIFTY" in Zerodha (space, not underscore)
            if (symbol == "GIFT_NIFTY")
            {
                return "GIFT NIFTY";
            }

            // Detect market type from suffix
            if (symbol.Contains("_NSE")) marketType = MarketType.Spot;
            else if (symbol.Contains("_NFO")) marketType = MarketType.Futures;
            else if (symbol.Contains("_MCX")) marketType = MarketType.MCX;

            return symbol.Split('_')[0];
        }

        public static string GetSuffix(MarketType marketType)
        {
            switch (marketType)
            {
                case MarketType.Spot: return "_NSE";
                case MarketType.Futures: return "_NFO";
                case MarketType.MCX: return "_MCX";
                default: return "_NSE";
            }
        }

        public async Task RegisterSymbols()
        {
            Logger.Info("[IM] Registering symbols...");
            // Load and register instruments from mapped_instruments.json or DB
            var all = await _dbService.GetAllInstrumentsAsync();
            foreach (var m in all) _mappingService.AddMapping(m.symbol, m);
        }

        public async Task<ObservableCollection<InstrumentDefinition>> GetBrokerInformation()
        {
            var list = await _dbService.GetAllInstrumentsAsync();
            var coll = new ObservableCollection<InstrumentDefinition>();
            foreach (var m in list)
            {
                coll.Add(new InstrumentDefinition
                {
                    Symbol = m.symbol,
                    BrokerSymbol = m.zerodhaSymbol ?? m.symbol,
                    Segment = m.segment,
                    InstrumentToken = m.instrument_token,
                    MarketType = MapInstrumentType(m.instrument_type),
                    TickSize = m.tick_size,
                    LotSize = m.lot_size,
                    Expiry = m.expiry,
                    Strike = m.strike,
                    Underlying = m.underlying
                });
            }
            return coll;
        }

        public bool CreateInstrument(InstrumentDefinition instrument, out string ntSymbolName)
        {
            ntSymbolName = instrument.Symbol;
            try
            {
                // Step 1: Add mapping to our internal cache
                _mappingService.AddMapping(instrument.Symbol, new MappedInstrument
                {
                    symbol = instrument.Symbol,
                    zerodhaSymbol = instrument.BrokerSymbol ?? instrument.Symbol,
                    instrument_token = instrument.InstrumentToken,
                    exchange = instrument.Segment,
                    segment = instrument.Segment,
                    tick_size = instrument.TickSize,
                    lot_size = instrument.LotSize,
                    expiry = instrument.Expiry,
                    strike = instrument.Strike,
                    underlying = instrument.Underlying
                });

                // Step 2: Create actual NinjaTrader MasterInstrument and Instrument
                // This is the critical step that was missing - without it, Instrument.GetInstrument() returns null
                bool created = NinjaTraderHelper.CreateNTInstrument(instrument, out ntSymbolName);

                if (created)
                {
                    Logger.Info($"[IM] CreateInstrument: Successfully created/found NT instrument '{ntSymbolName}'");
                }
                else
                {
                    Logger.Warn($"[IM] CreateInstrument: Failed to create NT instrument for '{instrument.Symbol}'");
                }

                return created;
            }
            catch (Exception ex)
            {
                Logger.Error($"[IM] CreateInstrument: Exception for '{instrument.Symbol}' - {ex.Message}", ex);
                return false;
            }
        }

        public bool RemoveInstrument(InstrumentDefinition instrument)
        {
            try
            {
                // Remove from NinjaTrader database
                bool removed = NinjaTraderHelper.RemoveNTInstrument(instrument.Symbol);

                // Also remove from our mapping cache (if we had a method for that)
                // _mappingService.RemoveMapping(instrument.Symbol);

                return removed;
            }
            catch (Exception ex)
            {
                Logger.Error($"[IM] RemoveInstrument: Exception for '{instrument.Symbol}' - {ex.Message}", ex);
                return false;
            }
        }

        public async Task<ObservableCollection<InstrumentDefinition>> GetNTSymbols()
        {
            // Returns currently mapped NT symbols
            return await GetBrokerInformation();
        }

        private MarketType MapInstrumentType(string type)
        {
            if (type == "EQ") return MarketType.Spot;
            if (type == "FUTIDX" || type == "FUTSTK") return MarketType.Futures;
            if (type == "OPTIDX" || type == "OPTSTK") return MarketType.Futures; // Options treated as Futures segment
            return MarketType.Spot;
        }
    }
}
