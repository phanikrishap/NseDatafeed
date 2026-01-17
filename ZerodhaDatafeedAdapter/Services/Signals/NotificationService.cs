using System;
using System.Collections.Generic;
using ZerodhaDatafeedAdapter.AddOns.OptionSignals.Services;
using ZerodhaDatafeedAdapter.Services.Telegram;

namespace ZerodhaDatafeedAdapter.Services.Signals
{
    /// <summary>
    /// Interface for sending notifications about signal events.
    /// </summary>
    public interface INotificationService
    {
        void NotifySignal(string message);
        void NotifyStoploss(string signalId, string symbol, double exitPrice);
        void NotifyError(string message);
        void NotifyInfo(string message);
    }

    /// <summary>
    /// Notification service using Terminal output.
    /// </summary>
    public class TerminalNotificationService : INotificationService
    {
        private readonly TerminalService _terminal;

        public TerminalNotificationService(TerminalService terminal)
        {
            _terminal = terminal ?? throw new ArgumentNullException(nameof(terminal));
        }

        public void NotifySignal(string message)
        {
            _terminal.Signal(message);
        }

        public void NotifyStoploss(string signalId, string symbol, double exitPrice)
        {
            _terminal.Signal($"[STOPLOSS] {symbol} closed at {exitPrice:F2}");
        }

        public void NotifyError(string message)
        {
            _terminal.Error(message);
        }

        public void NotifyInfo(string message)
        {
            _terminal.Info(message);
        }
    }

    /// <summary>
    /// Notification service using Telegram alerts.
    /// </summary>
    public class TelegramNotificationService : INotificationService
    {
        private readonly TelegramAlertService _telegram;

        public TelegramNotificationService(TelegramAlertService telegram)
        {
            _telegram = telegram ?? throw new ArgumentNullException(nameof(telegram));
        }

        public void NotifySignal(string message)
        {
            // Telegram doesn't have a generic signal method, so we use Info
        }

        public void NotifyStoploss(string signalId, string symbol, double exitPrice)
        {
            // Could extend TelegramAlertService to support signal stoploss alerts
        }

        public void NotifyError(string message)
        {
            // Could extend TelegramAlertService to support error alerts
        }

        public void NotifyInfo(string message)
        {
            // Could extend TelegramAlertService to support info messages
        }
    }

    /// <summary>
    /// Composite notification service that sends notifications to multiple channels.
    /// Extracted from SignalsOrchestrator for separation of concerns.
    /// </summary>
    public class CompositeNotificationService : INotificationService
    {
        private readonly List<INotificationService> _notifiers;

        public CompositeNotificationService()
        {
            _notifiers = new List<INotificationService>
            {
                new TerminalNotificationService(TerminalService.Instance),
                new TelegramNotificationService(TelegramAlertService.Instance)
            };
        }

        public CompositeNotificationService(params INotificationService[] notifiers)
        {
            _notifiers = new List<INotificationService>(notifiers);
        }

        public void NotifySignal(string message)
        {
            foreach (var notifier in _notifiers)
            {
                try
                {
                    notifier.NotifySignal(message);
                }
                catch
                {
                    // Ignore individual notifier failures
                }
            }
        }

        public void NotifyStoploss(string signalId, string symbol, double exitPrice)
        {
            foreach (var notifier in _notifiers)
            {
                try
                {
                    notifier.NotifyStoploss(signalId, symbol, exitPrice);
                }
                catch
                {
                    // Ignore individual notifier failures
                }
            }
        }

        public void NotifyError(string message)
        {
            foreach (var notifier in _notifiers)
            {
                try
                {
                    notifier.NotifyError(message);
                }
                catch
                {
                    // Ignore individual notifier failures
                }
            }
        }

        public void NotifyInfo(string message)
        {
            foreach (var notifier in _notifiers)
            {
                try
                {
                    notifier.NotifyInfo(message);
                }
                catch
                {
                    // Ignore individual notifier failures
                }
            }
        }
    }
}
