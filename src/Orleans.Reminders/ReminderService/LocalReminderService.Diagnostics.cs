using System;
using System.Diagnostics;
using Orleans.Diagnostics;

#nullable disable
namespace Orleans.Runtime.ReminderService
{
    internal sealed partial class LocalReminderService
    {
        private static readonly DiagnosticListener DiagnosticListener = new(OrleansRemindersDiagnostics.ListenerName);

        private void EmitReminderRegisteredDiagnostics(GrainId grainId, string reminderName, TimeSpan dueTime, TimeSpan period)
        {
            if (!DiagnosticListener.IsEnabled(OrleansRemindersDiagnostics.EventNames.Registered))
            {
                return;
            }

            DiagnosticListener.Write(
                OrleansRemindersDiagnostics.EventNames.Registered,
                new ReminderRegisteredEvent(grainId, reminderName, dueTime, period, Silo));
        }

        private void EmitReminderUnregisteredDiagnostics(GrainId grainId, string reminderName)
        {
            if (!DiagnosticListener.IsEnabled(OrleansRemindersDiagnostics.EventNames.Unregistered))
            {
                return;
            }

            DiagnosticListener.Write(
                OrleansRemindersDiagnostics.EventNames.Unregistered,
                new ReminderUnregisteredEvent(grainId, reminderName, Silo));
        }

        private sealed partial class LocalReminderData
        {
            private void EmitTickCompletedDiagnostics(TimeSpan elapsed)
            {
                if (!DiagnosticListener.IsEnabled(OrleansRemindersDiagnostics.EventNames.TickCompleted))
                {
                    return;
                }

                DiagnosticListener.Write(
                    OrleansRemindersDiagnostics.EventNames.TickCompleted,
                    new ReminderTickCompletedEvent(Identity.GrainId, Identity.ReminderName, elapsed, siloAddress));
            }

            private void EmitTickFailedDiagnostics(Exception exception, TimeSpan elapsed)
            {
                if (!DiagnosticListener.IsEnabled(OrleansRemindersDiagnostics.EventNames.TickFailed))
                {
                    return;
                }

                DiagnosticListener.Write(
                    OrleansRemindersDiagnostics.EventNames.TickFailed,
                    new ReminderTickFailedEvent(Identity.GrainId, Identity.ReminderName, exception, elapsed, siloAddress));
            }

            private void EmitTickFiringDiagnostics(DateTime before)
            {
                if (!DiagnosticListener.IsEnabled(OrleansRemindersDiagnostics.EventNames.TickFiring))
                {
                    return;
                }

                DiagnosticListener.Write(
                    OrleansRemindersDiagnostics.EventNames.TickFiring,
                    new ReminderTickFiringEvent(Identity.GrainId, Identity.ReminderName, before, siloAddress));
            }
        }
    }
}
