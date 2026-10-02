namespace DxDark.Core;

/// <summary>
/// Runs device commands one at a time on a dedicated thread, so the UI never waits on USB.
/// Work posted with a key replaces queued work with the same key: dragging a slider sends only
/// the latest value instead of a backlog.
/// </summary>
internal sealed class CommandQueue : IDisposable
{
    private readonly LinkedList<(string? Key, Action Work)> _items = new();
    private readonly object _lock = new();
    private readonly SemaphoreSlim _signal = new(0);
    private readonly Thread _thread;
    private volatile bool _stopping;

    public CommandQueue()
    {
        _thread = new Thread(Run) { IsBackground = true, Name = "Strip commands" };
        _thread.Start();
    }

    public void Post(Action work, string? key = null)
    {
        lock (_lock)
        {
            if (key is not null)
            {
                for (LinkedListNode<(string? Key, Action Work)>? node = _items.First; node is not null; node = node.Next)
                {
                    if (node.Value.Key == key)
                    {
                        node.Value = (key, work);
                        return; // the queued item will run the latest work
                    }
                }
            }

            _items.AddLast((key, work));
        }

        _signal.Release();
    }

    /// <summary>Runs work after everything queued before it, and waits for it to finish.</summary>
    public bool Invoke(Action work, TimeSpan timeout)
    {
        using var done = new ManualResetEventSlim(false);
        Post(() =>
        {
            try
            {
                work();
            }
            finally
            {
                done.Set();
            }
        });
        return done.Wait(timeout);
    }

    private void Run()
    {
        while (!_stopping)
        {
            _signal.Wait();
            Action? work = null;
            lock (_lock)
            {
                if (_items.First is { } first)
                {
                    work = first.Value.Work;
                    _items.RemoveFirst();
                }
            }

            try
            {
                work?.Invoke();
            }
            catch (Exception ex)
            {
                Log.Error("Strip command failed", ex);
            }
        }
    }

    public void Dispose()
    {
        _stopping = true;
        _signal.Release();
        _thread.Join(2000);
        _signal.Dispose();
    }
}
