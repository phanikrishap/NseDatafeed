namespace ZerodhaDatafeedAdapter.Models
{
    /// <summary>
    /// Configuration settings for Telegram bot integration.
    /// Loaded from config.json TelegramSettings section.
    /// </summary>
    public class TelegramSettings
    {
        /// <summary>
        /// Whether Telegram integration is enabled
        /// </summary>
        public bool Enabled { get; set; } = false;

        /// <summary>
        /// Telegram Bot API token (from @BotFather)
        /// </summary>
        public string Token { get; set; } = string.Empty;

        /// <summary>
        /// Chat ID to send messages to (can be user or group chat)
        /// </summary>
        public string ChatId { get; set; } = string.Empty;

        /// <summary>
        /// Send alerts on NinjaTrader startup, token validation, etc.
        /// </summary>
        public bool SendStartupAlerts { get; set; } = true;

        /// <summary>
        /// Send alerts when tranches are entered
        /// </summary>
        public bool SendTrancheAlerts { get; set; } = true;

        /// <summary>
        /// Send alerts when stoplosses are triggered
        /// </summary>
        public bool SendStoplossAlerts { get; set; } = true;

        /// <summary>
        /// Enable two-way bot commands for TBS control
        /// </summary>
        public bool EnableBotCommands { get; set; } = true;

        /// <summary>
        /// Validates that the settings are properly configured
        /// </summary>
        public bool IsValid => Enabled &&
                               !string.IsNullOrWhiteSpace(Token) &&
                               !string.IsNullOrWhiteSpace(ChatId);
    }
}
