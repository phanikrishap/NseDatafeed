using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Newtonsoft.Json;
using ZerodhaDatafeedAdapter.Helpers;
using ZerodhaDatafeedAdapter.Models;

namespace ZerodhaDatafeedAdapter.Services.Instruments
{
    /// <summary>
    /// Service for managing symbol mappings between Zerodha and NinjaTrader.
    /// Handles index mappings and dynamic F&O mappings with persistence.
    /// NOTE: fo_mappings.json is saved ONLY via explicit SaveFOMappingsOnce() call, not on every AddMapping.
    /// </summary>
    public class SymbolMappingService
    {
        private readonly string _foMappingsPath;
        private readonly string _indexMappingsPath;
        private readonly ConcurrentDictionary<string, MappedInstrument> _mappedInstruments = new ConcurrentDictionary<string, MappedInstrument>(StringComparer.OrdinalIgnoreCase);
        private readonly object _saveLock = new object();
        private bool _foMappingsSaved = false; // Flag to ensure we only save once per session

        public SymbolMappingService(string foMappingsPath)
        {
            _foMappingsPath = foMappingsPath;
            // Index mappings file is in the same folder as fo_mappings.json
            string folder = Path.GetDirectoryName(foMappingsPath);
            _indexMappingsPath = Path.Combine(folder, "index_mappings.json");
            LoadIndexMappings();
        }

        /// <summary>
        /// Loads index mappings from index_mappings.json file.
        /// These are static mappings for indices like NIFTY, SENSEX, BANKNIFTY, etc.
        /// </summary>
        private void LoadIndexMappings()
        {
            if (!File.Exists(_indexMappingsPath))
            {
                Logger.Warn($"[SMS] No index mapping file found at {_indexMappingsPath}");
                return;
            }

            try
            {
                string jsonContent = File.ReadAllText(_indexMappingsPath);
                var instruments = JsonConvert.DeserializeObject<List<MappedInstrument>>(jsonContent);
                if (instruments != null)
                {
                    foreach (var instrument in instruments)
                    {
                        if (string.IsNullOrEmpty(instrument.symbol) || instrument.instrument_token <= 0) continue;

                        instrument.is_index = true;

                        // Map by symbol (e.g., "NIFTY", "SENSEX", "GIFT_NIFTY")
                        _mappedInstruments[instrument.symbol] = instrument;

                        // Also map by zerodhaSymbol if different (e.g., "NIFTY 50" -> same mapping)
                        if (!string.IsNullOrEmpty(instrument.zerodhaSymbol) &&
                            !instrument.zerodhaSymbol.Equals(instrument.symbol, StringComparison.OrdinalIgnoreCase))
                        {
                            _mappedInstruments[instrument.zerodhaSymbol] = instrument;
                        }

                        // Also map by underlying if different
                        if (!string.IsNullOrEmpty(instrument.underlying) &&
                            !instrument.underlying.Equals(instrument.symbol, StringComparison.OrdinalIgnoreCase))
                        {
                            _mappedInstruments[instrument.underlying] = instrument;
                        }
                    }
                    Logger.Info($"[SMS] Loaded {instruments.Count} index mappings from file.");
                }
            }
            catch (Exception ex)
            {
                Logger.Error($"[SMS] Error loading index mappings: {ex.Message}");
            }
        }

        public void AddMapping(string ntSymbol, MappedInstrument mapped)
        {
            if (string.IsNullOrEmpty(ntSymbol) || mapped == null) return;
            _mappedInstruments[ntSymbol] = mapped;
            // NOTE: Do NOT auto-save on every AddMapping - this was causing writes every tick
            // Use SaveFOMappingsOnce() explicitly after batch additions (e.g., after option generation)
        }

        /// <summary>
        /// Explicitly saves F&O mappings to disk. Should be called ONCE after options/futures are generated.
        /// Will not save if already saved in this session (use forceSave=true to override).
        /// </summary>
        public void SaveFOMappingsOnce(bool forceSave = false)
        {
            if (_foMappingsSaved && !forceSave)
            {
                Logger.Debug("[SMS] fo_mappings.json already saved this session, skipping");
                return;
            }
            SaveFOMappings();
            _foMappingsSaved = true;
            Logger.Info("[SMS] fo_mappings.json saved (once per session)");
        }

        public bool TryGetMapping(string ntSymbol, out MappedInstrument mapped)
        {
            return _mappedInstruments.TryGetValue(ntSymbol, out mapped);
        }

        public List<MappedInstrument> GetAllMappings() => _mappedInstruments.Values.ToList();

        public void ClearFOMappings()
        {
            try
            {
                if (File.Exists(_foMappingsPath)) File.Delete(_foMappingsPath);
                // Keep only index mappings
                var indexKeys = _mappedInstruments.Where(kvp => kvp.Value.is_index).Select(kvp => kvp.Key).ToList();
                var indexMappings = indexKeys.ToDictionary(k => k, k => _mappedInstruments[k]);
                _mappedInstruments.Clear();
                foreach (var kvp in indexMappings) _mappedInstruments[kvp.Key] = kvp.Value;
            }
            catch (Exception ex) { Logger.Error("[SMS] Failed to clear FO mappings:", ex); }
        }

        private void SaveFOMappings()
        {
            lock (_saveLock)
            {
                try
                {
                    var foMappings = _mappedInstruments.Where(kvp => !kvp.Value.is_index).ToDictionary(kvp => kvp.Key, kvp => kvp.Value);
                    string json = JsonConvert.SerializeObject(foMappings, Formatting.Indented);
                    File.WriteAllText(_foMappingsPath, json);
                }
                catch (Exception ex) { Logger.Error("[SMS] Failed to save FO mappings:", ex); }
            }
        }

        public void LoadFOMappings()
        {
            try
            {
                if (File.Exists(_foMappingsPath))
                {
                    string json = File.ReadAllText(_foMappingsPath);
                    var foMappings = JsonConvert.DeserializeObject<Dictionary<string, MappedInstrument>>(json);
                    if (foMappings != null)
                    {
                        foreach (var kvp in foMappings) _mappedInstruments[kvp.Key] = kvp.Value;
                    }
                }
            }
            catch (Exception ex) { Logger.Error("[SMS] Failed to load FO mappings:", ex); }
        }
    }
}
