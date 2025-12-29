// Decompiled with JetBrains decompiler
// Type: ZerodhaDatafeedAdapter.Annotations.NotifyPropertyChangedInvocatorAttribute
// Assembly: ZerodhaDatafeedAdapter, Version=1.0.0.0, Culture=neutral, PublicKeyToken=null
// MVID: C3950ED3-7884-49E5-9F57-41CBA3235764
// Assembly location: D:\NTConnector References\Binance Adapter\BinanceAdapterInstaller\ZerodhaDatafeedAdapter.dll

using System;

#nullable disable
namespace ZerodhaDatafeedAdapter.Annotations;

[AttributeUsage(AttributeTargets.Method)]
public sealed class NotifyPropertyChangedInvocatorAttribute : Attribute
{
  public NotifyPropertyChangedInvocatorAttribute()
  {
  }

  public NotifyPropertyChangedInvocatorAttribute([NotNull] string parameterName)
  {
    this.ParameterName = parameterName;
  }

  [CanBeNull]
  public string ParameterName { get; }
}
