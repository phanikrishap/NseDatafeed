using NinjaTrader.Cbi;
using NinjaTrader.Data;
using System;
using System.Collections.Generic;

#nullable disable
namespace ZerodhaDatafeedAdapter;

public class L2Subscription
{
  public SortedList<Instrument, Action<int, string, Operation, MarketDataType, double, long, DateTime>> L2Callbacks = new SortedList<Instrument, Action<int, string, Operation, MarketDataType, double, long, DateTime>>();

  public Instrument Instrument { get; set; }
}
