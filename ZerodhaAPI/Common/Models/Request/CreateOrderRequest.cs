// Decompiled with JetBrains decompiler
// Type: ZerodhaAPI.Common.Models.Request.CreateOrderRequest
// Assembly: BinanceAPI, Version=1.0.0.0, Culture=neutral, PublicKeyToken=null
// MVID: D349CB21-077C-4B48-99EA-7AB6C64F9B14
// Assembly location: D:\NTConnector References\Binance Adapter\BinanceAdapterInstaller\BinanceAPI.dll

using ZerodhaAPI.Common.Converter;
using ZerodhaAPI.Common.Enums;
using ZerodhaAPI.Common.Models.Request.Interfaces;
using Newtonsoft.Json;
using Newtonsoft.Json.Converters;
using System;
using System.Runtime.Serialization;

#nullable disable
namespace ZerodhaAPI.Common.Models.Request;

[DataContract]
public class CreateOrderRequest : IRequest
{
  [DataMember(Order = 1)]
  public string Symbol { get; set; }

  [DataMember(Order = 2)]
  [JsonConverter(typeof (StringEnumConverter))]
  public OrderSide Side { get; set; }

  [DataMember(Order = 3)]
  [JsonConverter(typeof (StringEnumConverter))]
  public OrderType Type { get; set; }

  [DataMember(Order = 4)]
  [JsonConverter(typeof (StringEnumConverter))]
  public ZerodhaAPI.Common.Enums.TimeInForce? TimeInForce { get; set; }

  [DataMember(Order = 5)]
  [JsonConverter(typeof (StringDecimalConverter))]
  public Decimal Quantity { get; set; }

  [DataMember(Order = 6)]
  [JsonConverter(typeof (StringDecimalConverter))]
  public Decimal? Price { get; set; }

  [DataMember(Order = 7)]
  public string NewClientOrderId { get; set; }

  [DataMember(Order = 8)]
  [JsonConverter(typeof (StringDecimalConverter))]
  public Decimal? StopPrice { get; set; }

  [DataMember(Order = 9)]
  [JsonProperty("icebergQty")]
  [JsonConverter(typeof (StringDecimalConverter))]
  public Decimal? IcebergQuantity { get; set; }

  [DataMember(Order = 10)]
  [JsonProperty("newOrderRespType")]
  [JsonConverter(typeof (StringEnumConverter))]
  public NewOrderResponseType NewOrderResponseType { get; set; }
}
