using System.Collections.Concurrent;
using PuTTY.Interop;

namespace PuTTY.Interop.Tests;

/// <summary>
/// Hosts the engine the way the app does: on one thread that owns it and runs its message
/// loop. The engine starts when the thread starts and shuts down when this is disposed.
/// Code that talks to the engine must run on that thread, through <see cref="Run{T}"/>.
/// </summary>
internal sealed class EngineThread : IDisposable
{
    private static readonly TimeSpan PumpInterval = TimeSpan.FromMilliseconds(10);

    private readonly Thread thread;
    private readonly BlockingCollection<Action> work = new();
    private readonly TaskCompletionSource started = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private volatile bool stopping;
    private Exception? shutdownFailure;

    public EngineThread()
    {
        thread = new Thread(Loop) { IsBackground = true, Name = "PuTTY engine" };
        thread.Start();
        started.Task.GetAwaiter().GetResult();   // rethrows an Initialize failure
    }

    /// <summary>Runs <paramref name="action"/> on the engine thread and returns its result.</summary>
    public T Run<T>(Func<T> action)
    {
        var done = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        work.Add(() =>
        {
            try
            {
                done.SetResult(action());
            }
            catch (Exception ex)
            {
                done.SetException(ex);
            }
        });
        return done.Task.GetAwaiter().GetResult();
    }

    public void Run(Action action) => Run(() =>
    {
        action();
        return 0;
    });

    /// <summary>Polls on the test thread until <paramref name="condition"/> holds or the time is up.</summary>
    public static bool WaitUntil(Func<bool> condition, TimeSpan timeout)
    {
        var clock = System.Diagnostics.Stopwatch.StartNew();
        while (clock.Elapsed < timeout)
        {
            if (condition())
            {
                return true;
            }

            Thread.Sleep(5);
        }

        return condition();
    }

    public void Dispose()
    {
        stopping = true;
        thread.Join();
        work.Dispose();
        if (shutdownFailure is not null)
        {
            throw new InvalidOperationException("The engine did not shut down cleanly.", shutdownFailure);
        }
    }

    private void Loop()
    {
        try
        {
            PuttyEngine.Initialize();
            started.SetResult();
        }
        catch (Exception ex)
        {
            started.SetException(ex);
            return;
        }

        while (!stopping)
        {
            while (work.TryTake(out Action? item))
            {
                item();
            }

            PuttyEngine.Pump(PumpInterval);
        }

        try
        {
            PuttyEngine.Shutdown();
        }
        catch (Exception ex)
        {
            shutdownFailure = ex;
        }
    }
}
