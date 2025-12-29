// Decompiled with JetBrains decompiler
// Type: ZerodhaDatafeedAdapter.Annotations.HtmlElementAttributesAttribute
// Assembly: ZerodhaDatafeedAdapter, Version=1.0.0.0, Culture=neutral, PublicKeyToken=null
// MVID: C3950ED3-7884-49E5-9F57-41CBA3235764
// Assembly location: D:\NTConnector References\Binance Adapter\BinanceAdapterInstaller\ZerodhaDatafeedAdapter.dll

using System;

#nullable disable
namespace ZerodhaDatafeedAdapter.Annotations;

[AttributeUsage(AttributeTargets.Property | AttributeTargets.Field | AttributeTargets.Parameter)]
public sealed class HtmlElementAttributesAttribute : Attribute
{
  public HtmlElementAttributesAttribute()
  {
  }

  public HtmlElementAttributesAttribute([NotNull] string name) => this.Name = name;

  [CanBeNull]
  public string Name { get; }
}
