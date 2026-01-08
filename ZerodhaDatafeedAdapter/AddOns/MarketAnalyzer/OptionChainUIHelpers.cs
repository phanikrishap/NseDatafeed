using System.Windows.Controls;
using System.Windows.Media;
using ZerodhaDatafeedAdapter.AddOns.MarketAnalyzer.Controls;
using ZerodhaDatafeedAdapter.AddOns.MarketAnalyzer.Models;
using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Input;

namespace ZerodhaDatafeedAdapter.AddOns.MarketAnalyzer
{
    public static class OptionChainUIHelpers
    {
        public static DockPanel CreateMainLayout()
        {
            return new DockPanel { Background = new SolidColorBrush(Color.FromRgb(27, 27, 28)) };
        }

        public static OptionChainHeaderControl CreateHeaderControl()
        {
            return new OptionChainHeaderControl();
        }

        public static OptionChainStatusBar CreateStatusBar()
        {
            return new OptionChainStatusBar();
        }

        public static OptionChainListView CreateListView(ObservableCollection<OptionChainRow> itemsSource, MouseButtonEventHandler clickHandler)
        {
            var listView = new OptionChainListView { ItemsSource = itemsSource };
            if (clickHandler != null)
            {
                listView.MouseLeftButtonUpEvent += clickHandler;
            }
            return listView;
        }
    }
}
