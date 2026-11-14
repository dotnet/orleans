using System.Collections.Concurrent;
using System.Diagnostics;
using System.Diagnostics.Tracing;
using org.apache.utils;
using org.apache.zookeeper;

namespace UnitTests.MembershipTests;

internal sealed class NativeSocketDiagnostics(Action<string> write) : EventListener
{
    private const int MaxLoggedEvents = 256;
    private readonly Action<string> _write = write;
    private int _eventCount;

    protected override void OnEventSourceCreated(EventSource eventSource)
    {
        if (eventSource.Name == "Private.InternalDiagnostics.System.Net.Sockets")
        {
            // Capture native completion errors before the SDK assigns its own SocketError.
            EnableEvents(eventSource, EventLevel.Error, (EventKeywords)1);
            _write($"{DateTime.UtcNow:O} [NativeSockets#{GetHashCode()}] Error listener enabled");
        }
    }

    protected override void OnEventWritten(EventWrittenEventArgs eventData)
    {
        if (eventData.EventName is "ErrorMessage" or "EventSourceMessage")
        {
            var count = Interlocked.Increment(ref _eventCount);
            if (count <= MaxLoggedEvents)
            {
                _write($"{DateTime.UtcNow:O} [NativeSockets#{GetHashCode()}] {eventData.EventName}: {string.Join("; ", eventData.Payload!)}");
            }
            else if (count == MaxLoggedEvents + 1)
            {
                _write($"{DateTime.UtcNow:O} [NativeSockets#{GetHashCode()}] Event limit reached; subsequent event details are omitted");
            }
        }
    }

    public override void Dispose()
    {
        base.Dispose();
        _write($"{DateTime.UtcNow:O} [NativeSockets#{GetHashCode()}] Listener disposed; observed-events={Volatile.Read(ref _eventCount)}; detail-limit={MaxLoggedEvents}");
    }
}

internal sealed class ZooKeeperNativeDiagnostics : ILogConsumer, IDisposable
{
    private const int MaxSdkMessages = 256;
    private readonly TraceLevel _previousLevel = ZooKeeper.LogLevel;
    private readonly bool _previousTrace = ZooKeeper.LogToTrace;
    private readonly ILogConsumer? _previousConsumer = ZooKeeper.CustomLogConsumer;
    private readonly ConcurrentQueue<string> _messages = new();
    private readonly NativeSocketDiagnostics _sockets;
    private int _sdkMessageCount;

    internal ZooKeeperNativeDiagnostics()
    {
        _sockets = new NativeSocketDiagnostics(_messages.Enqueue);
        ZooKeeper.LogLevel = TraceLevel.Info;
        ZooKeeper.LogToTrace = false;
        ZooKeeper.CustomLogConsumer = this;
    }

    public void Log(TraceLevel severity, string className, string message, Exception exception)
    {
        if (exception is not null || severity <= TraceLevel.Warning)
        {
            var record = $"{DateTime.UtcNow:O} [{className}] {severity}: {message}{Environment.NewLine}{exception}";
            Console.Error.WriteLine(record);
            var count = Interlocked.Increment(ref _sdkMessageCount);
            if (count <= MaxSdkMessages)
            {
                _messages.Enqueue(record);
            }
            else if (count == MaxSdkMessages + 1)
            {
                _messages.Enqueue($"{DateTime.UtcNow:O} SDK message limit reached; subsequent message details are omitted");
            }
        }
    }

    public void Dispose()
    {
        ZooKeeper.CustomLogConsumer = _previousConsumer;
        ZooKeeper.LogToTrace = _previousTrace;
        ZooKeeper.LogLevel = _previousLevel;
        _sockets.Dispose();
        _messages.Enqueue($"SDK messages observed={Volatile.Read(ref _sdkMessageCount)}; detail-limit={MaxSdkMessages}");
    }

    internal async Task WriteAsync(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await File.WriteAllLinesAsync(path, _messages);
    }
}
