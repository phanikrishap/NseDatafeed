// Decompiled with JetBrains decompiler
// Type: ZerodhaDatafeedAdapter.Annotations.CollectionAccessAttribute
// Assembly: ZerodhaDatafeedAdapter, Version=1.0.0.0, Culture=neutral, PublicKeyToken=null
// MVID: C3950ED3-7884-49E5-9F57-41CBA3235764
// Assembly location: D:\NTConnector References\Binance Adapter\BinanceAdapterInstaller\ZerodhaDatafeedAdapter.dll

using System;

#nullable disable
namespace ZerodhaDatafeedAdapter.Annotations;

[AttributeUsage(AttributeTargets.Constructor | AttributeTargets.Method | AttributeTargets.Property)]
public sealed class CollectionAccessAttribute : Attribute
{
  public CollectionAccessAttribute(CollectionAccessType collectionAccessType)
  {
    this.CollectionAccessType = collectionAccessType;
  }

  public CollectionAccessType CollectionAccessType { get; }
}
