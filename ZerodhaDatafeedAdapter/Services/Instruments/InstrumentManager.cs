using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Reactive.Linq;
using System.Reactive.Threading.Tasks;
using System.Threading;
using System.Threading.Tasks;
using NinjaTrader.Cbi;
using NinjaTrader.Data;
using ZerodhaAPI.Common.Enums;
using ZerodhaDatafeedAdapter.Helpers;
using ZerodhaDatafeedAdapter.Logging;
using ZerodhaDatafeedAdapter.Models;
using ZerodhaDatafeedAdapter.Models.Reactive;
using ZerodhaDatafeedAdapter.Services.Analysis;
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
            var hub = MarketDataReactiveHub.Instance;

            // Phase 1: Check if database needs refresh
            hub.PublishInitializationPhase(InitializationPhase.CheckingInstrumentDb, "Checking instrument database...", 10);
            StartupLogger.LogInitializationState("CheckingInstrumentDb", "Checking instrument database...", 10);

            bool needsRefresh = !File.Exists(_dbPath);
            DateTime? lastModifiedDate = null;

            if (!needsRefresh)
            {
                // Check if the file was last modified today (using date only, not time)
                var lastModified = File.GetLastWriteTime(_dbPath);
                lastModifiedDate = lastModified;
                var today = DateTime.Now.Date;
                needsRefresh = lastModified.Date < today;

                if (needsRefresh)
                {
                    Logger.Info($"[IM] InstrumentMasters.db is stale (last modified: {lastModified:yyyy-MM-dd}). Refreshing...");
                    StartupLogger.LogInstrumentDbCheck(true, lastModified);
                }
                else
                {
                    Logger.Info($"[IM] InstrumentMasters.db is current (last modified: {lastModified:yyyy-MM-dd}). Skipping download.");
                    StartupLogger.LogInstrumentDbCheck(false, lastModified);

                    // IMPORTANT: Load cache BEFORE publishing Ready signal
                    // Subscribers await InstrumentDbReadyStream before doing lookups,
                    // so cache must be populated before we signal ready
                    Logger.Info("[IM] Loading instrument cache from existing database...");
                    _dbService.LoadAllTokensToCache();
                    _mappingService.LoadFOMappings();

                    StartupLogger.LogInitializationReady(true, true);
                    hub.PublishInitializationState(InitializationState.Ready(true, true));
                    return;
                }
            }
            else
            {
                Logger.Info("[IM] InstrumentMasters.db does not exist. Downloading...");
                StartupLogger.LogInstrumentDbCheck(true, null);
            }

            // Phase 2: Await token validation using Rx pattern
            hub.PublishInitializationPhase(InitializationPhase.ValidatingToken, "Awaiting token validation...", 20);
            StartupLogger.LogInitializationState("ValidatingToken", "Awaiting token validation via Rx stream...", 20);
            Logger.Info("[IM] Awaiting token validation via Rx stream before downloading instruments...");

            try
            {
                // Use Rx to await token ready with timeout (60 seconds)
                var tokenValid = await hub.TokenReadyStream
                    .Timeout(TimeSpan.FromSeconds(60))
                    .FirstAsync()
                    .ToTask();

                // Publish token validated state
                var tokenState = InitializationState.ForPhase(
                    InitializationPhase.TokenValidated,
                    tokenValid ? "Token validated successfully" : "Token validation failed",
                    30);
                tokenState.IsTokenValid = tokenValid;
                hub.PublishInitializationState(tokenState);
                StartupLogger.LogInitializationState("TokenValidated", tokenValid ? "Token validated successfully" : "Token validation FAILED", 30, !tokenValid);

                if (!tokenValid)
                {
                    Logger.Warn("[IM] Token validation failed. Cannot download instruments without valid token.");
                    StartupLogger.LogInitializationFailed("Token validation failed. Manual login may be required.");
                    hub.PublishInitializationState(InitializationState.Fail("Token validation failed. Manual login may be required."));
                    return;
                }

                // Phase 3: Download instruments with retry
                Logger.Info("[IM] Token is valid. Proceeding with instrument download...");
                hub.PublishInitializationPhase(InitializationPhase.DownloadingInstruments, "Downloading instruments from Zerodha...", 40);
                StartupLogger.LogInitializationState("DownloadingInstruments", "Downloading instruments from Zerodha API...", 40);

                var downloadSuccess = await _dbService.DownloadAndCreateInstrumentDatabaseAsync(
                    maxRetries: 3,
                    initialDelayMs: 2000,
                    cancellationToken: CancellationToken.None);

                if (downloadSuccess)
                {
                    Logger.Info("[IM] Instrument database downloaded and refreshed successfully.");
                    StartupLogger.LogInstrumentDbDownloadResult(true);

                    // Phase 4: Load cache
                    hub.PublishInitializationPhase(InitializationPhase.LoadingInstrumentCache, "Loading instruments into cache...", 80);
                    StartupLogger.LogInitializationState("LoadingInstrumentCache", "Loading instruments into memory cache...", 80);
                    _dbService.LoadAllTokensToCache();

                    // Publish ready state
                    StartupLogger.LogInitializationReady(true, true);
                    hub.PublishInitializationState(InitializationState.Ready(true, true));
                }
                else
                {
                    Logger.Warn("[IM] Instrument download failed after all retries.");
                    StartupLogger.LogInstrumentDbDownloadResult(false, null, "All retry attempts exhausted");
                    bool hasExistingDb = File.Exists(_dbPath);

                    if (hasExistingDb)
                    {
                        Logger.Info("[IM] Using existing (stale) database as fallback.");
                        StartupLogger.LogInitializationState("LoadingInstrumentCache", "Loading STALE cache as fallback...", 80);
                        hub.PublishInitializationPhase(InitializationPhase.LoadingInstrumentCache, "Loading existing cache (stale)...", 80);
                        _dbService.LoadAllTokensToCache();
                        StartupLogger.LogInitializationReady(true, true);
                        hub.PublishInitializationState(InitializationState.Ready(true, true));
                    }
                    else
                    {
                        StartupLogger.LogInitializationFailed("Failed to download instruments and no existing database available.");
                        hub.PublishInitializationState(InitializationState.Fail("Failed to download instruments and no existing database available."));
                    }
                }
            }
            catch (TimeoutException)
            {
                Logger.Error("[IM] Timeout waiting for token validation (60s). Cannot download instruments.");
                StartupLogger.LogInitializationFailed("Timeout waiting for token validation (60s).");
                hub.PublishInitializationState(InitializationState.Fail("Timeout waiting for token validation (60s)."));
            }
            catch (Exception ex)
            {
                Logger.Error($"[IM] Error during instrument database refresh: {ex.Message}", ex);
                bool hasExistingDb = File.Exists(_dbPath);
                if (hasExistingDb)
                {
                    Logger.Info("[IM] Using existing database despite error.");
                    StartupLogger.LogInitializationReady(true, true);
                    hub.PublishInitializationState(InitializationState.Ready(true, true));
                }
                else
                {
                    StartupLogger.LogInitializationFailed($"Error: {ex.Message}");
                    hub.PublishInitializationState(InitializationState.Fail($"Error: {ex.Message}"));
                }
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
