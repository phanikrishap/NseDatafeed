using System;
using System.Collections.Generic;
using System.Data.SQLite;
using System.IO;
using System.Net.Http;
using System.Threading.Tasks;
using Newtonsoft.Json;

namespace TokenGeneratorTest
{
    /// <summary>
    /// Downloads instruments from Zerodha API and saves them to both JSON and SQLite database
    /// </summary>
    public class InstrumentDownloader
    {
        private readonly string _apiKey;
        private readonly string _accessToken;
        private readonly string _jsonOutputPath;
        private readonly string _dbOutputPath;

        public InstrumentDownloader(string apiKey, string accessToken, string jsonOutputPath, string dbOutputPath = null)
        {
            _apiKey = apiKey;
            _accessToken = accessToken;
            _jsonOutputPath = jsonOutputPath;

            // If dbOutputPath not specified, use the same directory as JSON with InstrumentMasters.db
            if (string.IsNullOrEmpty(dbOutputPath))
            {
                string directory = Path.GetDirectoryName(jsonOutputPath);
                _dbOutputPath = Path.Combine(directory, "InstrumentMasters.db");
            }
            else
            {
                _dbOutputPath = dbOutputPath;
            }
        }

        /// <summary>
        /// Downloads instruments from Zerodha API and saves to both JSON and SQLite
        /// </summary>
        /// <param name="exchanges">Optional list of exchanges to filter (e.g., "NFO", "NSE", "MCX"). If null, downloads all.</param>
        /// <param name="ninjaSymbolMappings">Optional dictionary of NinjaTrader symbol -> Zerodha symbol mappings</param>
        /// <returns>Number of instruments downloaded</returns>
        public async Task<int> DownloadAndSaveInstrumentsAsync(string[] exchanges = null, Dictionary<string, string> ninjaSymbolMappings = null)
        {
            Console.WriteLine("Starting instrument download from Zerodha API...");

            var instruments = new List<MappedInstrument>();

            // Track the current month NIFTY futures for NIFTY_I mapping (declared outside using block)
            MappedInstrument currentMonthNiftyFut = null;
            DateTime nearestExpiry = DateTime.MaxValue;

            using (var client = new HttpClient())
            {
                // Set authorization header
                client.DefaultRequestHeaders.Add("X-Kite-Version", "3");
                client.DefaultRequestHeaders.Add("Authorization", $"token {_apiKey}:{_accessToken}");

                // Download instruments CSV
                string url = "https://api.kite.trade/instruments";
                Console.WriteLine($"Fetching from: {url}");

                HttpResponseMessage response = await client.GetAsync(url);

                if (!response.IsSuccessStatusCode)
                {
                    string error = await response.Content.ReadAsStringAsync();
                    throw new Exception($"Failed to download instruments. Status: {response.StatusCode}, Error: {error}");
                }

                string csvContent = await response.Content.ReadAsStringAsync();
                Console.WriteLine($"Downloaded {csvContent.Length} bytes of CSV data");

                // Parse CSV
                string[] lines = csvContent.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);

                if (lines.Length <= 1)
                {
                    Console.WriteLine("No instruments found in CSV");
                    return 0;
                }

                // Get column indices from header
                string[] headers = lines[0].Split(',');
                var columnIndex = new Dictionary<string, int>();
                for (int i = 0; i < headers.Length; i++)
                {
                    columnIndex[headers[i].Trim()] = i;
                }

                Console.WriteLine($"CSV Headers: {string.Join(", ", headers)}");
                Console.WriteLine($"Total rows: {lines.Length - 1}");

                // Required columns
                int tradingSymbolIdx = GetColumnIndex(columnIndex, "tradingsymbol");
                int instrumentTokenIdx = GetColumnIndex(columnIndex, "instrument_token");
                int exchangeTokenIdx = GetColumnIndex(columnIndex, "exchange_token");
                int exchangeIdx = GetColumnIndex(columnIndex, "exchange");
                int nameIdx = GetColumnIndex(columnIndex, "name");
                int expiryIdx = GetColumnIndex(columnIndex, "expiry");
                int strikeIdx = GetColumnIndex(columnIndex, "strike");
                int tickSizeIdx = GetColumnIndex(columnIndex, "tick_size");
                int lotSizeIdx = GetColumnIndex(columnIndex, "lot_size");
                int instrumentTypeIdx = GetColumnIndex(columnIndex, "instrument_type");
                int segmentIdx = GetColumnIndex(columnIndex, "segment");

                // Parse data rows
                int processed = 0;
                int skipped = 0;

                for (int i = 1; i < lines.Length; i++)
                {
                    try
                    {
                        string[] fields = ParseCsvLine(lines[i]);

                        if (fields.Length < headers.Length)
                        {
                            skipped++;
                            continue;
                        }

                        string exchange = GetField(fields, exchangeIdx);

                        // Filter by exchange if specified
                        if (exchanges != null && exchanges.Length > 0)
                        {
                            bool matchFound = false;
                            foreach (var ex in exchanges)
                            {
                                if (exchange.Equals(ex, StringComparison.OrdinalIgnoreCase))
                                {
                                    matchFound = true;
                                    break;
                                }
                            }
                            if (!matchFound)
                            {
                                skipped++;
                                continue;
                            }
                        }

                        string tradingSymbol = GetField(fields, tradingSymbolIdx);
                        string segment = GetField(fields, segmentIdx);
                        string instrumentType = GetField(fields, instrumentTypeIdx);

                        // Parse instrument token
                        if (!long.TryParse(GetField(fields, instrumentTokenIdx), out long instrumentToken))
                        {
                            skipped++;
                            continue;
                        }

                        // Parse other fields
                        int.TryParse(GetField(fields, exchangeTokenIdx), out int exchangeToken);
                        double.TryParse(GetField(fields, tickSizeIdx), out double tickSize);
                        int.TryParse(GetField(fields, lotSizeIdx), out int lotSize);
                        double.TryParse(GetField(fields, strikeIdx), out double strike);

                        // Parse expiry
                        DateTime? expiry = null;
                        string expiryStr = GetField(fields, expiryIdx);
                        if (!string.IsNullOrEmpty(expiryStr) && DateTime.TryParse(expiryStr, out DateTime expiryDate))
                        {
                            expiry = expiryDate;
                        }

                        // Determine option type from instrument type
                        string optionType = null;
                        if (instrumentType.Contains("CE"))
                            optionType = "CE";
                        else if (instrumentType.Contains("PE"))
                            optionType = "PE";

                        // Determine underlying
                        string underlying = DetermineUnderlying(tradingSymbol, instrumentType, exchange);

                        var instrument = new MappedInstrument
                        {
                            symbol = tradingSymbol,
                            zerodhaSymbol = tradingSymbol,
                            underlying = underlying,
                            expiry = expiry,
                            strike = strike > 0 ? strike : (double?)null,
                            option_type = optionType,
                            segment = segment,
                            instrument_token = instrumentToken,
                            exchange_token = exchangeToken,
                            tick_size = tickSize > 0 ? tickSize : 0.05,
                            lot_size = lotSize > 0 ? lotSize : 1,
                            name = GetField(fields, nameIdx),
                            exchange = exchange,
                            instrument_type = instrumentType
                        };

                        instruments.Add(instrument);
                        processed++;

                        // Track the nearest expiry NIFTY futures for NIFTY_I mapping
                        if (underlying == "NIFTY" && instrumentType == "FUT" && exchange == "NFO" && expiry.HasValue)
                        {
                            if (expiry.Value > DateTime.Now && expiry.Value < nearestExpiry)
                            {
                                nearestExpiry = expiry.Value;
                                currentMonthNiftyFut = instrument;
                            }
                        }

                        // Log progress every 10000 instruments
                        if (processed % 10000 == 0)
                        {
                            Console.WriteLine($"Processed {processed} instruments...");
                        }
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine($"Error parsing line {i}: {ex.Message}");
                        skipped++;
                    }
                }

                Console.WriteLine($"Processed {processed} instruments, skipped {skipped}");

                if (currentMonthNiftyFut != null)
                {
                    Console.WriteLine($"Found nearest NIFTY futures: {currentMonthNiftyFut.symbol} (expiry: {currentMonthNiftyFut.expiry:yyyy-MM-dd})");
                }

                // Add any custom NinjaTrader symbol mappings
                if (ninjaSymbolMappings != null)
                {
                    foreach (var mapping in ninjaSymbolMappings)
                    {
                        string ninjaSymbol = mapping.Key;
                        string zerodhaSymbol = mapping.Value;

                        // Find the Zerodha instrument
                        var zerodhaInstrument = instruments.Find(i =>
                            i.zerodhaSymbol.Equals(zerodhaSymbol, StringComparison.OrdinalIgnoreCase));

                        if (zerodhaInstrument != null)
                        {
                            Console.WriteLine($"Creating mapping: {ninjaSymbol} -> {zerodhaSymbol}");

                            var customMapping = new MappedInstrument
                            {
                                symbol = ninjaSymbol,
                                zerodhaSymbol = zerodhaInstrument.zerodhaSymbol,
                                underlying = zerodhaInstrument.underlying,
                                expiry = zerodhaInstrument.expiry,
                                strike = zerodhaInstrument.strike,
                                option_type = zerodhaInstrument.option_type,
                                segment = zerodhaInstrument.segment,
                                instrument_token = zerodhaInstrument.instrument_token,
                                exchange_token = zerodhaInstrument.exchange_token,
                                tick_size = zerodhaInstrument.tick_size,
                                lot_size = zerodhaInstrument.lot_size,
                                name = zerodhaInstrument.name,
                                exchange = zerodhaInstrument.exchange,
                                instrument_type = zerodhaInstrument.instrument_type
                            };

                            instruments.Insert(0, customMapping);
                        }
                        else
                        {
                            Console.WriteLine($"WARNING: Could not find Zerodha instrument for mapping: {ninjaSymbol} -> {zerodhaSymbol}");
                        }
                    }
                }
            }

            // Ensure directory exists
            string directory = Path.GetDirectoryName(_jsonOutputPath);
            if (!string.IsNullOrEmpty(directory) && !Directory.Exists(directory))
            {
                Directory.CreateDirectory(directory);
            }

            // Save to SQLite database (all instruments)
            Console.WriteLine($"Saving {instruments.Count} instruments to SQLite: {_dbOutputPath}");
            SaveToSqlite(instruments);
            Console.WriteLine($"Successfully saved {instruments.Count} instruments to SQLite");

            // Save only custom mappings to JSON file (NIFTY_I, etc.)
            var customMappings = new List<MappedInstrument>();

            // Add NIFTY_I mapping if found
            if (currentMonthNiftyFut != null)
            {
                var niftyIMapping = new MappedInstrument
                {
                    symbol = "NIFTY_I",
                    zerodhaSymbol = currentMonthNiftyFut.zerodhaSymbol,
                    underlying = "NIFTY",
                    expiry = currentMonthNiftyFut.expiry,
                    strike = null,
                    option_type = null,
                    segment = currentMonthNiftyFut.segment,
                    instrument_token = currentMonthNiftyFut.instrument_token,
                    exchange_token = currentMonthNiftyFut.exchange_token,
                    tick_size = currentMonthNiftyFut.tick_size,
                    lot_size = currentMonthNiftyFut.lot_size,
                    name = "NIFTY 50 Continuous Futures",
                    exchange = currentMonthNiftyFut.exchange,
                    instrument_type = "FUT"
                };
                customMappings.Add(niftyIMapping);
                Console.WriteLine($"Added NIFTY_I mapping to {currentMonthNiftyFut.zerodhaSymbol} (expiry: {currentMonthNiftyFut.expiry:yyyy-MM-dd})");
            }

            // Save custom mappings to JSON
            Console.WriteLine($"Saving {customMappings.Count} custom mappings to JSON: {_jsonOutputPath}");
            string json = JsonConvert.SerializeObject(customMappings, Formatting.Indented);
            File.WriteAllText(_jsonOutputPath, json);
            Console.WriteLine($"Successfully saved {customMappings.Count} custom mappings to JSON");

            return instruments.Count;
        }

        /// <summary>
        /// Saves instruments to SQLite database
        /// </summary>
        private void SaveToSqlite(List<MappedInstrument> instruments)
        {
            // Delete existing database if it exists
            if (File.Exists(_dbOutputPath))
            {
                File.Delete(_dbOutputPath);
            }

            string connectionString = $"Data Source={_dbOutputPath};Version=3;";

            using (var connection = new SQLiteConnection(connectionString))
            {
                connection.Open();

                // Create table
                string createTableSql = @"
                    CREATE TABLE IF NOT EXISTS instruments (
                        id INTEGER PRIMARY KEY AUTOINCREMENT,
                        symbol TEXT NOT NULL,
                        zerodha_symbol TEXT NOT NULL,
                        underlying TEXT,
                        expiry TEXT,
                        strike REAL,
                        option_type TEXT,
                        segment TEXT,
                        instrument_token INTEGER NOT NULL,
                        exchange_token INTEGER,
                        tick_size REAL,
                        lot_size INTEGER,
                        name TEXT,
                        exchange TEXT,
                        instrument_type TEXT,
                        created_at TEXT DEFAULT CURRENT_TIMESTAMP
                    );

                    CREATE INDEX IF NOT EXISTS idx_symbol ON instruments(symbol);
                    CREATE INDEX IF NOT EXISTS idx_zerodha_symbol ON instruments(zerodha_symbol);
                    CREATE INDEX IF NOT EXISTS idx_instrument_token ON instruments(instrument_token);
                    CREATE INDEX IF NOT EXISTS idx_underlying ON instruments(underlying);
                    CREATE INDEX IF NOT EXISTS idx_exchange ON instruments(exchange);
                ";

                using (var command = new SQLiteCommand(createTableSql, connection))
                {
                    command.ExecuteNonQuery();
                }

                // Insert instruments using a transaction for better performance
                using (var transaction = connection.BeginTransaction())
                {
                    string insertSql = @"
                        INSERT INTO instruments (
                            symbol, zerodha_symbol, underlying, expiry, strike,
                            option_type, segment, instrument_token, exchange_token,
                            tick_size, lot_size, name, exchange, instrument_type
                        ) VALUES (
                            @symbol, @zerodha_symbol, @underlying, @expiry, @strike,
                            @option_type, @segment, @instrument_token, @exchange_token,
                            @tick_size, @lot_size, @name, @exchange, @instrument_type
                        )";

                    using (var command = new SQLiteCommand(insertSql, connection))
                    {
                        command.Parameters.Add("@symbol", System.Data.DbType.String);
                        command.Parameters.Add("@zerodha_symbol", System.Data.DbType.String);
                        command.Parameters.Add("@underlying", System.Data.DbType.String);
                        command.Parameters.Add("@expiry", System.Data.DbType.String);
                        command.Parameters.Add("@strike", System.Data.DbType.Double);
                        command.Parameters.Add("@option_type", System.Data.DbType.String);
                        command.Parameters.Add("@segment", System.Data.DbType.String);
                        command.Parameters.Add("@instrument_token", System.Data.DbType.Int64);
                        command.Parameters.Add("@exchange_token", System.Data.DbType.Int32);
                        command.Parameters.Add("@tick_size", System.Data.DbType.Double);
                        command.Parameters.Add("@lot_size", System.Data.DbType.Int32);
                        command.Parameters.Add("@name", System.Data.DbType.String);
                        command.Parameters.Add("@exchange", System.Data.DbType.String);
                        command.Parameters.Add("@instrument_type", System.Data.DbType.String);

                        int count = 0;
                        foreach (var instrument in instruments)
                        {
                            command.Parameters["@symbol"].Value = instrument.symbol ?? "";
                            command.Parameters["@zerodha_symbol"].Value = instrument.zerodhaSymbol ?? "";
                            command.Parameters["@underlying"].Value = instrument.underlying ?? (object)DBNull.Value;
                            command.Parameters["@expiry"].Value = instrument.expiry?.ToString("yyyy-MM-dd") ?? (object)DBNull.Value;
                            command.Parameters["@strike"].Value = instrument.strike ?? (object)DBNull.Value;
                            command.Parameters["@option_type"].Value = instrument.option_type ?? (object)DBNull.Value;
                            command.Parameters["@segment"].Value = instrument.segment ?? (object)DBNull.Value;
                            command.Parameters["@instrument_token"].Value = instrument.instrument_token;
                            command.Parameters["@exchange_token"].Value = instrument.exchange_token ?? (object)DBNull.Value;
                            command.Parameters["@tick_size"].Value = instrument.tick_size;
                            command.Parameters["@lot_size"].Value = instrument.lot_size;
                            command.Parameters["@name"].Value = instrument.name ?? (object)DBNull.Value;
                            command.Parameters["@exchange"].Value = instrument.exchange ?? (object)DBNull.Value;
                            command.Parameters["@instrument_type"].Value = instrument.instrument_type ?? (object)DBNull.Value;

                            command.ExecuteNonQuery();
                            count++;

                            if (count % 10000 == 0)
                            {
                                Console.WriteLine($"Inserted {count} instruments into SQLite...");
                            }
                        }
                    }

                    transaction.Commit();
                }

                // Create metadata table with download timestamp
                string createMetadataSql = @"
                    CREATE TABLE IF NOT EXISTS metadata (
                        key TEXT PRIMARY KEY,
                        value TEXT
                    );
                    INSERT OR REPLACE INTO metadata (key, value) VALUES ('last_updated', @timestamp);
                    INSERT OR REPLACE INTO metadata (key, value) VALUES ('instrument_count', @count);
                ";

                using (var command = new SQLiteCommand(createMetadataSql, connection))
                {
                    command.Parameters.AddWithValue("@timestamp", DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"));
                    command.Parameters.AddWithValue("@count", instruments.Count.ToString());
                    command.ExecuteNonQuery();
                }
            }
        }

        private int GetColumnIndex(Dictionary<string, int> columns, string name)
        {
            if (columns.TryGetValue(name, out int index))
                return index;
            return -1;
        }

        private string GetField(string[] fields, int index)
        {
            if (index < 0 || index >= fields.Length)
                return string.Empty;
            return fields[index]?.Trim() ?? string.Empty;
        }

        private string[] ParseCsvLine(string line)
        {
            // Simple CSV parsing - handles basic cases
            var fields = new List<string>();
            bool inQuotes = false;
            string currentField = "";

            foreach (char c in line)
            {
                if (c == '"')
                {
                    inQuotes = !inQuotes;
                }
                else if (c == ',' && !inQuotes)
                {
                    fields.Add(currentField);
                    currentField = "";
                }
                else
                {
                    currentField += c;
                }
            }
            fields.Add(currentField);

            return fields.ToArray();
        }

        private string DetermineUnderlying(string tradingSymbol, string instrumentType, string exchange)
        {
            // For index futures/options
            if (tradingSymbol.StartsWith("NIFTY") && !tradingSymbol.StartsWith("NIFTYBEES"))
                return "NIFTY";
            if (tradingSymbol.StartsWith("BANKNIFTY"))
                return "BANKNIFTY";
            if (tradingSymbol.StartsWith("FINNIFTY"))
                return "FINNIFTY";
            if (tradingSymbol.StartsWith("MIDCPNIFTY"))
                return "MIDCPNIFTY";
            if (tradingSymbol.StartsWith("SENSEX"))
                return "SENSEX";
            if (tradingSymbol.StartsWith("BANKEX"))
                return "BANKEX";

            // For equity derivatives, the underlying is typically the first part before the expiry
            if (exchange == "NFO" || exchange == "BFO")
            {
                // Try to extract underlying from symbol
                foreach (var month in new[] { "JAN", "FEB", "MAR", "APR", "MAY", "JUN", "JUL", "AUG", "SEP", "OCT", "NOV", "DEC" })
                {
                    int idx = tradingSymbol.IndexOf(month);
                    if (idx > 0)
                    {
                        // Check for 2-digit year before month (e.g., "25JAN")
                        if (idx >= 2 && char.IsDigit(tradingSymbol[idx - 1]) && char.IsDigit(tradingSymbol[idx - 2]))
                        {
                            return tradingSymbol.Substring(0, idx - 2);
                        }
                    }
                }
            }

            // For equities, the symbol itself is the underlying
            return tradingSymbol;
        }
    }

    /// <summary>
    /// Mapped instrument data structure
    /// </summary>
    public class MappedInstrument
    {
        public string symbol { get; set; }
        public string underlying { get; set; }
        public DateTime? expiry { get; set; }
        public double? strike { get; set; }
        public string option_type { get; set; }
        public string segment { get; set; }
        public long instrument_token { get; set; }
        public int? exchange_token { get; set; }
        public string zerodhaSymbol { get; set; }
        public double tick_size { get; set; }
        public int lot_size { get; set; }
        public string name { get; set; }
        public string exchange { get; set; }
        public string instrument_type { get; set; }
    }
}
