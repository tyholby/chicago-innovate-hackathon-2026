using System.Collections.Concurrent;
using Autodesk.Revit.UI;
using Autodesk.Revit.UI.Events;

namespace FamilyStudio.Revit.Native;

/// <summary>
/// Runs work on Revit's API thread through an ExternalEvent, from any thread, as awaitable tasks.
/// Queued work can be cancelled until it starts; work that has started always runs to completion,
/// so a cancelled operation can never leave a transaction half-applied.
/// </summary>
internal sealed class RevitDispatcher : IExternalEventHandler, IDisposable
{
    private static readonly TimeSpan QueueTimeout = TimeSpan.FromSeconds(90);

    private interface IWork
    {
        void Execute(UIApplication app);
        void Abandon(Exception reason);
    }

    private sealed class Work<T>(Func<UIApplication, T> action) : IWork
    {
        private int _state; // 0 queued, 1 running, 2 finished
        public readonly TaskCompletionSource<T> Completion = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public void Execute(UIApplication app)
        {
            if (Interlocked.CompareExchange(ref _state, 1, 0) != 0) return;
            try { Completion.TrySetResult(action(app)); }
            catch (Exception ex) { Completion.TrySetException(ex); }
            finally { Volatile.Write(ref _state, 2); }
        }

        public void Abandon(Exception reason)
        {
            if (Interlocked.CompareExchange(ref _state, 2, 0) == 0) Completion.TrySetException(reason);
        }
    }

    private readonly ExternalEvent _event;
    private readonly UIApplication _application;
    private readonly ConcurrentQueue<IWork> _queue = new();
    private readonly object _gate = new();
    private TaskCompletionSource<bool> _idle = Completed();
    private int _outstanding;
    private Exception? _stopped;

    /// <summary>Must be constructed in a Revit API context, such as a command's Execute.</summary>
    public RevitDispatcher(UIApplication application)
    {
        _application = application;
        _event = ExternalEvent.Create(this);
        _application.Idling += OnIdling;
    }

    public Task WhenIdle { get { lock (_gate) return _idle.Task; } }

    public async Task<T> RunAsync<T>(Func<UIApplication, T> action, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var work = new Work<T>(action);
        lock (_gate)
        {
            if (_stopped is not null) throw _stopped;
            if (_outstanding++ == 0) _idle = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            _queue.Enqueue(work);
        }
        using var deadline = new CancellationTokenSource(QueueTimeout);
        using var onCancel = cancellationToken.Register(() => work.Abandon(new OperationCanceledException(cancellationToken)));
        using var onTimeout = deadline.Token.Register(() => work.Abandon(new TimeoutException(
            "Revit is busy (a dialog may be open), so Family Studio could not run. Close any open Revit dialog and try again.")));
        try
        {
            if (_event.Raise() == ExternalEventRequest.Denied)
                work.Abandon(new InvalidOperationException("Revit declined the request. Close any open Revit dialog and try again."));
            return await work.Completion.Task.ConfigureAwait(false);
        }
        finally
        {
            lock (_gate) if (--_outstanding == 0) _idle.TrySetResult(true);
        }
    }

    public void Execute(UIApplication app)
    {
        while (_queue.TryDequeue(out var work)) work.Execute(app);
    }

    public string GetName() => "Family Studio";

    // Covers work queued while the event was already executing, which Raise() can miss.
    private void OnIdling(object? sender, IdlingEventArgs args)
    {
        if (!_queue.IsEmpty) _event.Raise();
    }

    public void Dispose()
    {
        lock (_gate) _stopped ??= new ObjectDisposedException(nameof(RevitDispatcher), "Family Studio closed.");
        while (_queue.TryDequeue(out var work)) work.Abandon(_stopped);
        _application.Idling -= OnIdling;
        _event.Dispose();
    }

    private static TaskCompletionSource<bool> Completed()
    {
        var source = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        source.SetResult(true);
        return source;
    }
}
