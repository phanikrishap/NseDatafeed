// Decompiled with JetBrains decompiler
// Type: ZerodhaDatafeedAdapter.L2Subscription
// Assembly: ZerodhaDatafeedAdapter, Version=1.0.0.0, Culture=neutral, PublicKeyToken=null
// MVID: C3950ED3-7884-49E5-9F57-41CBA3235764
// Assembly location: D:\NTConnector References\Binance Adapter\BinanceAdapterInstaller\ZerodhaDatafeedAdapter.dll

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
