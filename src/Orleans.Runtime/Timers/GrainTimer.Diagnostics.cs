using System;
using System.Diagnostics;
using System.Threading;
using Orleans.Diagnostics;

namespace Orleans.Runtime;

internal abstract partial class GrainTimer
{
    private static readonly DiagnosticListener Listener = new(OrleansTimerDiagnostics.ListenerName);

    private void EmitCreatedDiagnostics()
    {
        if (!Listener.IsEnabled(OrleansTimerDiagnostics.EventNames.Created))
        {
            return;
        }

        Listener.Write(OrleansTimerDiagnostics.EventNames.Created, new GrainTimerCreatedEvent(
            GrainContext,
            null,
            Timeout.InfiniteTimeSpan,
            Timeout.InfiniteTimeSpan));
    }

    private void EmitDisposedDiagnostics()
    {
        if (!Listener.IsEnabled(OrleansTimerDiagnostics.EventNames.Disposed))
        {
            return;
        }

        Listener.Write(OrleansTimerDiagnostics.EventNames.Disposed, new GrainTimerDisposedEvent(
            GrainContext,
            null));
    }

    private TickDiagnosticsContext EmitTickStartDiagnostics()
    {
        long startTimestamp = 0;
        if (Listener.IsEnabled(OrleansTimerDiagnostics.EventNames.TickStart))
        {
            Listener.Write(OrleansTimerDiagnostics.EventNames.TickStart, new GrainTimerTickStartEvent(
                GrainContext,
                null));

            startTimestamp = Stopwatch.GetTimestamp();
            return new(true, startTimestamp);
        }

        return default;
    }

    private void EmitTickStopDiagnostics(TickDiagnosticsContext diagnostics, Exception? exception = null)
    {
        if (!diagnostics.EmitDiagnostics)
        {
            return;
        }

        Listener.Write(OrleansTimerDiagnostics.EventNames.TickStop, new GrainTimerTickStopEvent(
            GrainContext,
            null,
            Stopwatch.GetElapsedTime(diagnostics.StartTimestamp),
            exception));
    }

    private readonly record struct TickDiagnosticsContext(bool EmitDiagnostics, long StartTimestamp);
}
