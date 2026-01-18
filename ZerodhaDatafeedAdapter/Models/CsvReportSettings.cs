namespace ZerodhaDatafeedAdapter.Models
{
    /// <summary>
    /// Configuration settings for CSV report generation.
    /// Loaded from config.json CsvReportSettings section.
    /// </summary>
    public class CsvReportSettings
    {
        /// <summary>
        /// Whether CSV report writing is enabled.
        /// When true, writes Signals.csv and OptionsSignals.csv to the CSVReports folder.
        /// </summary>
        public bool WriteToCSV { get; set; } = false;

        /// <summary>
        /// Whether to write individual strike CSV files.
        /// When true, writes a separate CSV file for each strike that qualifies as ATM, ITM1, or OTM1.
        /// Once a strike qualifies, its metrics are tracked continuously regardless of later qualification status.
        /// Files are named: {InstrumentName}.csv (e.g., NIFTY25JAN23500CE.csv)
        /// </summary>
        public bool WriteIndividualStrikes { get; set; } = false;

        /// <summary>
        /// Validates that the settings are properly configured.
        /// </summary>
        public bool IsValid => WriteToCSV || WriteIndividualStrikes;
    }
}
