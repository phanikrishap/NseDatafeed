using System;
using System.Threading;
using System.Threading.Tasks;
using ZerodhaDatafeedAdapter.Helpers;

namespace ZerodhaDatafeedAdapter.Services.WebSocket
{
    public enum WebSocketState
    {
        Disconnected,
        Connecting,
        Connected,
        BackingOff,
        Disposed
    }

    /// <summary>
    /// Manages the state machine and backoff logic for a WebSocket connection.
    /// Ensures atomic transitions and reliable event-driven waiting.
    /// </summary>
    public class WebSocketStateController
    {
        private int _state = (int)WebSocketState.Disconnected;
        private int _backoffCount = 0;
        private TaskCompletionSource<bool> _backoffTcs;
        private readonly object _stateLock = new object();

        public WebSocketState CurrentState => (WebSocketState)_state;
        public int BackoffCount => _backoffCount;

        public bool TryTransition(WebSocketState from, WebSocketState to)
        {
            return Interlocked.CompareExchange(ref _state, (int)to, (int)from) == (int)from;
        }

        public void ForceState(WebSocketState state)
        {
            Interlocked.Exchange(ref _state, (int)state);
            if (state == WebSocketState.Disconnected || state == WebSocketState.Disposed)
            {
                _backoffTcs?.TrySetResult(false);
            }
        }

        public async Task<bool> EnterBackoffAndWaitAsync(CancellationToken token)
        {
            if (!TryTransition(WebSocketState.Disconnected, WebSocketState.BackingOff)) return false;

            _backoffCount++;
            int delayMs = GetNextBackoffDelay();
            Logger.Warn($"[WSC] Connection failed. Backoff #{_backoffCount} for {delayMs}ms");

            _backoffTcs = new TaskCompletionSource<bool>();
            using (token.Register(() => _backoffTcs.TrySetResult(false)))
            {
                await Task.WhenAny(_backoffTcs.Task, Task.Delay(delayMs, token));
            }

            TryTransition(WebSocketState.BackingOff, WebSocketState.Disconnected);
            return !token.IsCancellationRequested;
        }

        public void ResetBackoff()
        {
            _backoffCount = 0;
            _backoffTcs?.TrySetResult(true);
        }

        private int GetNextBackoffDelay()
        {
            // Exponential: 1s, 2s, 4s, 8s, 16s, capped at 30s
            double delay = Math.Pow(2, _backoffCount - 1) * 1000;
            return (int)Math.Min(delay, 30000);
        }
    }
}
