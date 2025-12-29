// Decompiled with JetBrains decompiler
// Type: ZerodhaAPI.Common.Models.Response.SystemStatusResponse
// Assembly: BinanceAPI, Version=1.0.0.0, Culture=neutral, PublicKeyToken=null
// MVID: D349CB21-077C-4B48-99EA-7AB6C64F9B14
// Assembly location: D:\NTConnector References\Binance Adapter\BinanceAdapterInstaller\BinanceAPI.dll

using ZerodhaAPI.Common.Enums;
using ZerodhaAPI.Common.Models.Response.Interfaces;
using Newtonsoft.Json;
using Newtonsoft.Json.Converters;
using System.Runtime.Serialization;

#nullable disable
namespace ZerodhaAPI.Common.Models.Response;

[DataContract]
public class SystemStatusResponse : IResponse
{
  [DataMember(Order = 1)]
  [JsonConverter(typeof (StringEnumConverter))]
  public SystemStatus Status { get; set; }

  [DataMember(Order = 2)]
  [JsonProperty(PropertyName = "msg")]
  public string Message { get; set; }
}
