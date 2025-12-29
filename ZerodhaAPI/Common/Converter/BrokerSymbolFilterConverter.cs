// Decompiled with JetBrains decompiler
// Type: ZerodhaAPI.Common.Converter.BrokerSymbolFilterConverter
// Assembly: BinanceAPI, Version=1.0.0.0, Culture=neutral, PublicKeyToken=null
// MVID: D349CB21-077C-4B48-99EA-7AB6C64F9B14
// Assembly location: D:\NTConnector References\Binance Adapter\BinanceAdapterInstaller\BinanceAPI.dll

using ZerodhaAPI.Common.Enums;
using ZerodhaAPI.Common.Models.Response;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using System;

#nullable disable
namespace ZerodhaAPI.Common.Converter;

public class BrokerSymbolFilterConverter : JsonConverter
{
  public override bool CanWrite => false;

  public override bool CanConvert(Type objectType) => false;

  public override object ReadJson(
    JsonReader reader,
    Type objectType,
    object existingValue,
    JsonSerializer serializer)
  {
    JObject jobject = JObject.Load(reader);
    BrokerSymbolFilter infoSymbolFilter = jobject.ToObject<BrokerSymbolFilter>();
    BrokerSymbolFilter target = (BrokerSymbolFilter) null;
    switch (infoSymbolFilter.FilterType)
    {
      case BrokerSymbolFilterType.PriceFilter:
        target = (BrokerSymbolFilter) new BrokerSymbolFilterPrice();
        break;
      case BrokerSymbolFilterType.PercentPrice:
        target = (BrokerSymbolFilter) new BrokerSymbolFilterPercentPrice();
        break;
      case BrokerSymbolFilterType.LotSize:
        target = (BrokerSymbolFilter) new BrokerSymbolFilterLotSize();
        break;
      case BrokerSymbolFilterType.MinNotional:
        target = (BrokerSymbolFilter) new BrokerSymbolFilterMinNotional();
        break;
      case BrokerSymbolFilterType.IcebergParts:
        target = (BrokerSymbolFilter) new BrokerSymbolFilterIcebergParts();
        break;
      case BrokerSymbolFilterType.MarketLotSize:
        target = (BrokerSymbolFilter) new BrokerSymbolFilterMarketLotSize();
        break;
      case BrokerSymbolFilterType.MaxNumOrders:
        target = (BrokerSymbolFilter) new BrokerSymbolFilterMaxNumOrders();
        break;
      case BrokerSymbolFilterType.MaxNumAlgoOrders:
        target = (BrokerSymbolFilter) new BrokerSymbolFilterMaxNumAlgoOrders();
        break;
      case BrokerSymbolFilterType.MaxNumIcebergOrders:
        target = (BrokerSymbolFilter) new BrokerSymbolFilterMaxNumIcebergOrders();
        break;
      case BrokerSymbolFilterType.MaxPosition:
        target = (BrokerSymbolFilter) new BrokerSymbolFilterMaxPosition();
        break;
      case BrokerSymbolFilterType.ExchangeMaxNumOrders:
        target = (BrokerSymbolFilter) new BrokerSymbolFilterExchangeMaxNumOrders();
        break;
      case BrokerSymbolFilterType.ExchangeMaxNumAlgoOrders:
        target = (BrokerSymbolFilter) new BrokerSymbolFilterExchangeMaxNumAlgoOrders();
        break;
      case BrokerSymbolFilterType.PercentagePrice:
        target = (BrokerSymbolFilter) new BrokerSymbolFilterPercentagePrice();
        break;
    }
    serializer.Populate(jobject.CreateReader(), (object) target);
    return (object) target;
  }

  public override void WriteJson(JsonWriter writer, object value, JsonSerializer serializer)
  {
    throw new NotImplementedException();
  }
}
