// Decompiled with JetBrains decompiler
// Type: ZerodhaDatafeedAdapter.Annotations.AspMvcActionAttribute
// Assembly: ZerodhaDatafeedAdapter, Version=1.0.0.0, Culture=neutral, PublicKeyToken=null
// MVID: C3950ED3-7884-49E5-9F57-41CBA3235764
// Assembly location: D:\NTConnector References\Binance Adapter\BinanceAdapterInstaller\ZerodhaDatafeedAdapter.dll

using System;

#nullable disable
namespace ZerodhaDatafeedAdapter.Annotations;

[AttributeUsage(AttributeTargets.Method | AttributeTargets.Property | AttributeTargets.Field | AttributeTargets.Parameter)]
public sealed class AspMvcActionAttribute : Attribute
{
  public AspMvcActionAttribute()
  {
  }

  public AspMvcActionAttribute([NotNull] string anonymousProperty)
  {
    this.AnonymousProperty = anonymousProperty;
  }

  [CanBeNull]
  public string AnonymousProperty { get; }
}
