// Decompiled with JetBrains decompiler
// Type: ZerodhaAPI.Common.Models.Response.Error.BrokerError
// Assembly: BinanceAPI, Version=1.0.0.0, Culture=neutral, PublicKeyToken=null
// MVID: D349CB21-077C-4B48-99EA-7AB6C64F9B14
// Assembly location: D:\NTConnector References\Binance Adapter\BinanceAdapterInstaller\BinanceAPI.dll

using Newtonsoft.Json;

#nullable disable
namespace ZerodhaAPI.Common.Models.Response.Error;

public class BrokerError
{
  public int Code { get; set; }

  [JsonProperty(PropertyName = "msg")]
  public string Message { get; set; }

  public string RequestMessage { get; set; }

  public override string ToString() => $"{this.Code}: {this.Message}";
}
