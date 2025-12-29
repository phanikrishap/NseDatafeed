// Decompiled with JetBrains decompiler
// Type: ZerodhaAPI.Common.Models.Response.BrokerRateLimit
// Assembly: BinanceAPI, Version=1.0.0.0, Culture=neutral, PublicKeyToken=null
// MVID: D349CB21-077C-4B48-99EA-7AB6C64F9B14
// Assembly location: D:\NTConnector References\Binance Adapter\BinanceAdapterInstaller\BinanceAPI.dll

using System.Runtime.Serialization;

#nullable disable
namespace ZerodhaAPI.Common.Models.Response;

[DataContract]
public class BrokerRateLimit
{
  [DataMember(Order = 1)]
  public string RateLimitType { get; set; }

  [DataMember(Order = 2)]
  public string Interval { get; set; }

  [DataMember(Order = 3)]
  public int Limit { get; set; }
}
