using System;
using System.Threading;

namespace Headroom.App.Startup;

/// <summary>
/// Keeps exactly one Headroom running, and turns a second launch into "show me
/// the deck" rather than a second tray icon.
/// </summary>
public sealed class SingleInstance : IDisposable
{
    private const string MutexName = @"Local\Headroom.SingleInstance";
    private const string SignalName = @"Local\Headroom.ShowDeck";

    private readonly Mutex _mutex;
    private readonly EventWaitHandle _signal;
    private readonly CancellationTokenSource _cancellation = new();
    private Thread? _listener;

    private SingleInstance(Mutex mutex, EventWaitHandle signal, bool isFirst)
    {
        _mutex = mutex;
        _signal = signal;
        IsFirstInstance = isFirst;
    }

    public bool IsFirstInstance { get; }

    /// <summary>Raised on a background thread when another launch asks for the deck.</summary>
    public event EventHandler? ShowRequested;

    public static SingleInstance Acquire()
    {
        var mutex = new Mutex(initiallyOwned: true, MutexName, out var createdNew);
        var signal = new EventWaitHandle(false, EventResetMode.AutoReset, SignalName);
        return new SingleInstance(mutex, signal, createdNew);
    }

    /// <summary>Asks the already-running instance to open its deck, then exits.</summary>
    public void SignalExistingInstance() => _signal.Set();

    /// <summary>Starts listening for other launches. Only the first instance should call this.</summary>
    public void StartListening()
    {
        var thread = new Thread(() =>
        {
            var handles = new WaitHandle[] { _signal, _cancellation.Token.WaitHandle };
            while (!_cancellation.IsCancellationRequested)
            {
                var index = WaitHandle.WaitAny(handles);
                if (index != 0) return;
                ShowRequested?.Invoke(this, EventArgs.Empty);
            }
        })
        {
            IsBackground = true,
            Name = "Headroom single-instance listener",
        };

        _listener = thread;
        thread.Start();
    }

    public void Dispose()
    {
        _cancellation.Cancel();

        // The listener may still be parked inside WaitAny holding both handles;
        // disposing them out from under it throws on a background thread.
        _listener?.Join(TimeSpan.FromMilliseconds(250));

        try
        {
            if (IsFirstInstance) _mutex.ReleaseMutex();
        }
        catch (ApplicationException)
        {
            // Not owned any more; nothing to release.
        }

        _mutex.Dispose();
        _signal.Dispose();
        _cancellation.Dispose();
    }
}
