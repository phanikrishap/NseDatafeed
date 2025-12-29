// Decompiled with JetBrains decompiler
// Type: ZerodhaDatafeedAdapter.Annotations.ContractAnnotationAttribute
// Assembly: ZerodhaDatafeedAdapter, Version=1.0.0.0, Culture=neutral, PublicKeyToken=null
// MVID: C3950ED3-7884-49E5-9F57-41CBA3235764
// Assembly location: D:\NTConnector References\Binance Adapter\BinanceAdapterInstaller\ZerodhaDatafeedAdapter.dll

using System;

#nullable disable
namespace ZerodhaDatafeedAdapter.Annotations;

[AttributeUsage(AttributeTargets.Method, AllowMultiple = true)]
public sealed class ContractAnnotationAttribute : Attribute
{
  public ContractAnnotationAttribute([NotNull] string contract)
    : this(contract, false)
  {
  }

  public ContractAnnotationAttribute([NotNull] string contract, bool forceFullStates)
  {
    this.Contract = contract;
    this.ForceFullStates = forceFullStates;
  }

  [NotNull]
  public string Contract { get; }

  public bool ForceFullStates { get; }
}
