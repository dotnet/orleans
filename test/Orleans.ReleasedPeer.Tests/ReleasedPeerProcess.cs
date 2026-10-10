using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.Json;
using ReleasedPeer;

namespace Orleans.ReleasedPeer.Tests;

internal sealed class ReleasedPeerProcess : IAsyncDisposable
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(45);
    private readonly Process _process;
    private readonly SemaphoreSlim _input = new(1, 1);
    private readonly Task _reader;
    private readonly Task<string> _stderr;
    private readonly ConcurrentDictionary<int, TaskCompletionSource<JsonElement>> _replies = new();
    private readonly ConcurrentDictionary<string, TaskCompletionSource<MessageSnapshot>> _requests = new();
    private readonly TaskCompletionSource<JsonElement> _ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource _deactivating = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource _running = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private int _commandId;

    public string Silo { get; private set; } = "";
    public JsonElement Identity { get; private set; }
    public ConcurrentQueue<MessageSnapshot> Messages { get; } = new();
    public Task Deactivating => _deactivating.Task.WaitAsync(Timeout);
    public Task Running => _running.Task.WaitAsync(Timeout);

    private ReleasedPeerProcess(Process process)
    {
        _process = process;
        _stderr = process.StandardError.ReadToEndAsync();
        _reader = ReadAsync();
    }

    public static async Task<ReleasedPeerProcess> StartAsync(string version, int silo, int gateway, int primary, string cluster, bool clientOnly = false)
    {
        var filename = $"ReleasedPeer.V{(version == "10.3.1" ? "103" : "104")}.dll";
        var start = new ProcessStartInfo("dotnet")
        {
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };
        start.ArgumentList.Add(Path.Combine(AppContext.BaseDirectory, "released", version, filename));
        start.ArgumentList.Add(silo.ToString(System.Globalization.CultureInfo.InvariantCulture));
        start.ArgumentList.Add(gateway.ToString(System.Globalization.CultureInfo.InvariantCulture));
        start.ArgumentList.Add(primary.ToString(System.Globalization.CultureInfo.InvariantCulture));
        start.ArgumentList.Add(cluster);
        if (clientOnly)
        {
            start.ArgumentList.Add("client");
        }
        var result = new ReleasedPeerProcess(Process.Start(start)!);
        try
        {
            result.Identity = await result._ready.Task.WaitAsync(Timeout);
            result.Silo = result.Identity.GetProperty("Silo").GetString()!;
            return result;
        }
        catch
        {
            await result.DisposeAsync();
            throw;
        }
    }

    public Task<MessageSnapshot> RequestAsync(string context)
        => _requests.GetOrAdd(context, _ => new(TaskCreationOptions.RunContinuationsAsynchronously)).Task.WaitAsync(Timeout);

    public async Task<T> CommandAsync<T>(
        string kind, Guid grain = default, Guid operation = default,
        int argument = 0, string context = "", string target = "")
    {
        var id = Interlocked.Increment(ref _commandId);
        var completion = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
        _replies[id] = completion;
        await _input.WaitAsync();
        try
        {
            await _process.StandardInput.WriteLineAsync(JsonSerializer.Serialize(
                new { Kind = kind, Id = id, Grain = grain, Operation = operation, Argument = argument, Context = context, Target = target }));
            await _process.StandardInput.FlushAsync();
        }
        finally
        {
            _input.Release();
        }
        try
        {
            var reply = await completion.Task.WaitAsync(Timeout);
            if (reply.TryGetProperty("Error", out var error))
            {
                throw new InvalidOperationException($"Released peer {Silo}, command {kind}: {error.GetString()}");
            }

            return reply.GetProperty("Value").Deserialize<T>()!;
        }
        finally
        {
            _replies.TryRemove(id, out _);
        }
    }

    private async Task ReadAsync()
    {
        try
        {
            while (await _process.StandardOutput.ReadLineAsync() is { } line)
            {
                using var document = JsonDocument.Parse(line);
                var value = document.RootElement;
                switch (value.GetProperty("Kind").GetString())
                {
                    case "ready":
                        _ready.TrySetResult(value.Clone());
                        break;
                    case "reply":
                        if (_replies.TryGetValue(value.GetProperty("Id").GetInt32(), out var completion))
                        {
                            completion.TrySetResult(value.Clone());
                        }
                        break;
                    case "deactivating":
                        _deactivating.TrySetResult();
                        break;
                    case "running":
                        _running.TrySetResult();
                        break;
                    case "message":
                        var message = value.GetProperty("Message").Deserialize<MessageSnapshot>()!;
                        Messages.Enqueue(message);
                        if (message.Direction == "Request" && message.Stage == "admitted")
                        {
                            _requests.GetOrAdd(message.Context, _ => new(TaskCreationOptions.RunContinuationsAsynchronously)).TrySetResult(message);
                        }
                        break;
                }
            }

            await _process.WaitForExitAsync();
            throw new InvalidOperationException($"Released peer exited {_process.ExitCode}: {await _stderr}");
        }
        catch (Exception error)
        {
            _ready.TrySetException(error);
            _deactivating.TrySetException(error);
            _running.TrySetException(error);
            foreach (var completion in _replies.Values)
            {
                completion.TrySetException(error);
            }
            foreach (var completion in _requests.Values)
            {
                completion.TrySetException(error);
            }
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (!_process.HasExited)
        {
            try
            {
                await _process.StandardInput.WriteLineAsync("{\"Kind\":\"exit\",\"Id\":0}");
                await _process.StandardInput.FlushAsync();
                await _process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(20));
            }
            catch
            {
                if (!_process.HasExited)
                {
                    _process.Kill(entireProcessTree: true);
                    await _process.WaitForExitAsync();
                }
            }
        }

        await _reader;
        _process.Dispose();
    }
}
