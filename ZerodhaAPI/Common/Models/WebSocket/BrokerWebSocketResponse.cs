// Decompiled with JetBrains decompiler
// Type: ZerodhaAPI.Common.Models.WebSocket.BinanceWebSocketResponse
// Assembly: BinanceAPI, Version=1.0.0.0, Culture=neutral, PublicKeyToken=null
// MVID: D349CB21-077C-4B48-99EA-7AB6C64F9B14
// Assembly location: D:\NTConnector References\Binance Adapter\BinanceAdapterInstaller\BinanceAPI.dll

using ZerodhaAPI.Common.Converter;
using ZerodhaAPI.Common.Models.WebSocket.Interfaces;
using Newtonsoft.Json;
using System;
using System.Runtime.Serialization;

#nullable disable
namespace ZerodhaAPI.Common.Models.WebSocket;

[DataContract]
public class BrokerWebSocketResponse : IWebSocketResponse
{
  [DataMember(Order = 1)]
  [JsonProperty(PropertyName = "e")]
  public string EventType { get; set; }

  [DataMember(Order = 2)]
  [JsonProperty(PropertyName = "E")]
  [JsonConverter(typeof (EpochTimeConverter))]
  public DateTime EventTime { get; set; }
}
