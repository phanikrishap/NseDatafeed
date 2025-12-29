using ZerodhaAPI.Common.Utility;
using System;

#nullable disable
namespace ZerodhaAPI.Zerodha.Websockets;

//public class WebSocketConnectionFunc
//{
//  public Func<bool> ExitFunction;

//  public int Timeout { get; }

//  public bool IsTimeout => this.Timeout > 0;

//  public WebSocketConnectionFunc(int timeout = 5000) => this.Timeout = timeout;

//  public WebSocketConnectionFunc(Func<bool> exitFunction) => this.ExitFunction = exitFunction;
//}


public class WebSocketConnectionFunc
{
    public Func<bool> ExitFunction { get; }
    public bool IsTimeout { get; }
    public TimeSpan Timeout { get; }

    public WebSocketConnectionFunc(Func<bool> exitFunction, bool isTimeout = false, TimeSpan? timeout = null)
    {
        Guard.AgainstNull((object)exitFunction, nameof(exitFunction));
        this.ExitFunction = exitFunction;
        this.IsTimeout = isTimeout;
        this.Timeout = timeout ?? TimeSpan.Zero;
    }
}
