using System.Collections.Concurrent;
using System.Diagnostics;
using Xunit;

namespace UnitTests.ActivationsLifeCycleTests;

/// <summary>
/// Records assertion failures raised by activation code, which the activation's operation loop otherwise swallows, so that tests can fail on them.
/// </summary>
internal static class ActivationAssertionRecorder
{
    private static readonly ConcurrentQueue<string> Failures = new();
    private static readonly RecordingListener Listener = new();

    /// <summary>
    /// Ensures that the recorder observes assertion failures before any listener which throws.
    /// </summary>
    public static void Install()
    {
        lock (Trace.Listeners)
        {
            Trace.Listeners.Remove(Listener);
            Trace.Listeners.Insert(0, Listener);
        }
    }

    /// <summary>
    /// Asserts that activation code raised no assertion failure since the previous check.
    /// </summary>
    public static void AssertNone()
    {
        var failures = new List<string>();
        while (Failures.TryDequeue(out var failure))
        {
            failures.Add(failure);
        }

        Assert.True(failures.Count == 0, $"Activation assertion failures:{Environment.NewLine}{string.Join(Environment.NewLine, failures)}");
    }

    private sealed class RecordingListener : TraceListener
    {
        public override void Fail(string? message, string? detailMessage)
        {
            var stackTrace = Environment.StackTrace;
            if (stackTrace.Contains("Orleans.Runtime.ActivationData", StringComparison.Ordinal))
            {
                Failures.Enqueue($"{message} {detailMessage}{Environment.NewLine}{stackTrace}");
            }
        }

        public override void Write(string? message)
        {
        }

        public override void WriteLine(string? message)
        {
        }
    }
}
