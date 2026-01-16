namespace ZerodhaDatafeedAdapter.Core
{
    public static class FeatureFlags
    {
        public static bool UseRefactoredHistoricalServices { get; set; } = false;
        public static bool UseRefactoredTBSExecution { get; set; } = false;
        public static bool UseRefactoredSignalExecution { get; set; } = false;
        public static bool UseRefactoredTickProcessor { get; set; } = false;

        public static bool CanaryMode { get; set; } = false;
    }
}
