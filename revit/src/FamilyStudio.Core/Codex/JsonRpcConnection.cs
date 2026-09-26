using System.Collections.Concurrent;
using System.Text.Json;
using FamilyStudio.Core.Json;

namespace FamilyStudio.Core.Codex;

/// <summary>A JSON-RPC error returned by the Codex app-server.</summary>
public sealed class CodexRpcException(int code, string message, JsonElement? details) : Exception(message)
{
    public int Code { get; } = code;
    public JsonElement? Details { get; } = details;
}

/// <summary>
/// Newline-delimited JSON-RPC 2.0 over a pair of text streams, as spoken by <c>codex app-server</c>
/// on stdio. Requests are correlated by ID; notifications are delivered in arrival order on the
/// reader thread; server-initiated requests are answered by <see cref="RequestHandler"/>.
/// </summary>
public sealed class JsonRpcConnection : IDisposable
{
    private readonly TextReader _reader;
    private readonly TextWriter _writer;
    private readonly SemaphoreSlim _writeLock = new(1, 1);
    private readonly ConcurrentDictionary<long, TaskCompletionSource<JsonElement>> _pending = new();
    private readonly CancellationTokenSource _stop = new();
    private readonly Task _readLoop;
    private long _nextId;
    private int _closed;

    /// <summary>Raised for every notification (a message with a method and no ID).</summary>
    public event Action<string, JsonElement>? Notification;

    /// <summary>Raised once when the stream ends or fails.</summary>
    public event Action<Exception>? Closed;

    /// <summary>Answers server-initiated requests. Return the result object, or throw to send an error.</summary>
    public Func<string, JsonElement, CancellationToken, Task<object?>>? RequestHandler { get; set; }

    public bool IsOpen => Volatile.Read(ref _closed) == 0;

    public JsonRpcConnection(TextReader reader, TextWriter writer)
    {
        _reader = reader;
        _writer = writer;
        // Read off the caller's thread: Revit's UI thread must never parse protocol traffic.
        _readLoop = Task.Run(ReadLoopAsync);
    }

    public async Task<JsonElement> CallAsync(string method, object? parameters, CancellationToken cancellationToken)
    {
        var id = Interlocked.Increment(ref _nextId);
        var completion = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pending[id] = completion;
        try
        {
            if (!IsOpen) throw new IOException("The Codex connection is closed.");
            await WriteAsync(new { id, method, @params = parameters ?? new { } }, cancellationToken).ConfigureAwait(false);
            return await completion.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        finally { _pending.TryRemove(id, out _); }
    }

    public Task NotifyAsync(string method, object? parameters, CancellationToken cancellationToken) =>
        WriteAsync(new { method, @params = parameters ?? new { } }, cancellationToken);

    private async Task WriteAsync(object message, CancellationToken cancellationToken)
    {
        var line = JsonSerializer.Serialize(message, StudioJson.Lenient);
        await _writeLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (!IsOpen) throw new IOException("The Codex connection is closed.");
            await _writer.WriteLineAsync(line.AsMemory(), cancellationToken).ConfigureAwait(false);
            await _writer.FlushAsync(cancellationToken).ConfigureAwait(false);
        }
        finally { _writeLock.Release(); }
    }

    private async Task ReadLoopAsync()
    {
        Exception reason = new EndOfStreamException("Codex closed its protocol stream.");
        try
        {
            while (!_stop.IsCancellationRequested)
            {
                var line = await _reader.ReadLineAsync(_stop.Token).ConfigureAwait(false);
                if (line is null) break;
                if (line.Length == 0 || line[0] != '{') continue; // tolerate stray non-protocol output
                Dispatch(line);
            }
        }
        catch (OperationCanceledException) when (_stop.IsCancellationRequested) { reason = new ObjectDisposedException(nameof(JsonRpcConnection)); }
        catch (Exception ex) { reason = ex; }
        Close(reason);
    }

    private void Dispatch(string line)
    {
        JsonElement root;
        try
        {
            using var document = JsonDocument.Parse(line);
            root = document.RootElement.Clone();
        }
        catch (JsonException) { return; }

        if (root.Str("method") is string method)
        {
            var parameters = root.Opt("params") ?? JsonSerializer.SerializeToElement(new { });
            if (root.TryGetProperty("id", out var requestId))
                _ = AnswerAsync(requestId.Clone(), method, parameters);
            else
            {
                try { Notification?.Invoke(method, parameters); }
                catch (Exception) { /* a faulty listener must not stop the protocol */ }
            }
            return;
        }

        if (!root.TryGetProperty("id", out var idElement) || !idElement.TryGetInt64(out var id) || !_pending.TryRemove(id, out var pending))
            return;
        if (root.Opt("error") is JsonElement error)
            pending.TrySetException(new CodexRpcException(
                error.Opt("code") is { ValueKind: JsonValueKind.Number } c && c.TryGetInt32(out var code) ? code : -32603,
                error.Str("message") ?? "Codex rejected the request.",
                error.Opt("data")));
        else
            pending.TrySetResult(root.Opt("result") ?? JsonSerializer.SerializeToElement(new { }));
    }

    private async Task AnswerAsync(JsonElement id, string method, JsonElement parameters)
    {
        try
        {
            var handler = RequestHandler ?? throw new NotSupportedException($"Family Studio does not handle {method}.");
            var result = await handler(method, parameters, _stop.Token).ConfigureAwait(false);
            await WriteAsync(new { id, result = result ?? new { } }, _stop.Token).ConfigureAwait(false);
        }
        catch (Exception ex) when (IsOpen)
        {
            try { await WriteAsync(new { id, error = new { code = -32000, message = ex.Message } }, _stop.Token).ConfigureAwait(false); }
            catch (Exception) { /* the connection is going away */ }
        }
        catch (Exception) { }
    }

    private void Close(Exception reason)
    {
        if (Interlocked.Exchange(ref _closed, 1) != 0) return;
        foreach (var pending in _pending.Values) pending.TrySetException(new IOException("The Codex connection closed.", reason));
        try { Closed?.Invoke(reason); } catch (Exception) { }
    }

    public void Dispose()
    {
        _stop.Cancel();
        Close(new ObjectDisposedException(nameof(JsonRpcConnection)));
    }
}
