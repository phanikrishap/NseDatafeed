using System;
using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace ZerodhaDatafeedAdapter.Models
{
    public class TickerData : INotifyPropertyChanged
    {
        private string _symbol;
        private double _currentPrice;
        private double _open;
        private double _high;
        private double _low;
        private double _close;
        private double _netChange;
        private double _netChangePercent;
        private double _projectedOpen;
        private DateTime _lastUpdate;

        public string Symbol { get => _symbol; set { _symbol = value; OnPropertyChanged(); } }
        public double CurrentPrice
        {
            get => _currentPrice;
            set
            {
                _currentPrice = value;
                _lastUpdate = DateTime.Now;
                OnPropertyChanged();
                OnPropertyChanged(nameof(LastPriceDisplay));
                OnPropertyChanged(nameof(LastUpdateTimeDisplay));
                OnPropertyChanged(nameof(IsPositive));
                // Recompute change if Close is available
                if (_close > 0)
                {
                    NetChange = _currentPrice - _close;
                    NetChangePercent = (NetChange / _close) * 100;
                }
            }
        }

        public double Open { get => _open; set { _open = value; OnPropertyChanged(); } }
        public double High { get => _high; set { _high = value; OnPropertyChanged(); } }
        public double Low { get => _low; set { _low = value; OnPropertyChanged(); } }
        public double Close
        {
            get => _close;
            set
            {
                _close = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(PriorCloseDisplay));
                // Recompute change when Close is set
                if (_currentPrice > 0 && _close > 0)
                {
                    NetChange = _currentPrice - _close;
                    NetChangePercent = (NetChange / _close) * 100;
                }
            }
        }

        // NetChange and NetChangePercent - can be set directly from API or computed
        public double NetChange
        {
            get => _netChange;
            set { _netChange = value; OnPropertyChanged(); OnPropertyChanged(nameof(NetChangeDisplay)); OnPropertyChanged(nameof(IsPositive)); }
        }

        public double NetChangePercent
        {
            get => _netChangePercent;
            set { _netChangePercent = value; OnPropertyChanged(); OnPropertyChanged(nameof(NetChangePercentDisplay)); }
        }

        public double ProjectedOpen
        {
            get => _projectedOpen;
            set
            {
                _projectedOpen = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(ProjectedOpenDisplay));
            }
        }

        // Alias properties for compatibility
        public double LastPrice { get => CurrentPrice; set => CurrentPrice = value; }
        public DateTime LastUpdateTime { get => _lastUpdate; set { _lastUpdate = value; OnPropertyChanged(); } }

        // Display properties
        public string LastPriceDisplay => CurrentPrice.ToString("F2");
        public string PriorCloseDisplay => Close > 0 ? Close.ToString("F2") : "-";
        public string NetChangeDisplay => NetChange != 0 ? $"{NetChange:+0.00;-0.00;0.00}" : "---";
        public string NetChangePercentDisplay => NetChangePercent != 0 ? $"{NetChangePercent:+0.00;-0.00;0.00}%" : "---";
        public string LastUpdateTimeDisplay => _lastUpdate.ToString("HH:mm:ss");
        public bool IsPositive => NetChange >= 0;
        public string ProjectedOpenDisplay => ProjectedOpen > 0 ? ProjectedOpen.ToString("F2") : "-";

        public void UpdatePrice(double price)
        {
            if (Open == 0) Open = price;
            if (price > High) High = price;
            if (Low == 0 || price < Low) Low = price;
            CurrentPrice = price;
        }

        public event PropertyChangedEventHandler PropertyChanged;
        protected void OnPropertyChanged([CallerMemberName] string name = null) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }
}
