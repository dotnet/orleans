using System;
using System.Diagnostics;
using Orleans.Diagnostics;

namespace Orleans.Runtime
{
    public partial class SiloLifecycleSubject
    {
        private static readonly DiagnosticListener Listener = new(OrleansLifecycleDiagnostics.SiloLifecycleListenerName);

        private void EmitStageCompletedDiagnostics(int stage, TimeSpan elapsed)
        {
            if (!Listener.IsEnabled(OrleansLifecycleDiagnostics.EventNames.StageCompleted))
            {
                return;
            }

            Listener.Write(OrleansLifecycleDiagnostics.EventNames.StageCompleted, new LifecycleStageCompletedEvent(
                stage,
                this.GetStageName(stage),
                _siloAddress,
                elapsed));
        }

        private void EmitStageStoppedDiagnostics(int stage, TimeSpan elapsed)
        {
            if (!Listener.IsEnabled(OrleansLifecycleDiagnostics.EventNames.StageStopped))
            {
                return;
            }

            Listener.Write(OrleansLifecycleDiagnostics.EventNames.StageStopped, new LifecycleStageStoppedEvent(
                stage,
                this.GetStageName(stage),
                _siloAddress,
                elapsed));
        }

        private partial class MonitoredObserver
        {
            private void EmitObserverCompletedDiagnostics(TimeSpan elapsed)
            {
                if (!Listener.IsEnabled(OrleansLifecycleDiagnostics.EventNames.ObserverCompleted))
                {
                    return;
                }

                Listener.Write(OrleansLifecycleDiagnostics.EventNames.ObserverCompleted, new LifecycleObserverCompletedEvent(
                    this.Name,
                    this.Stage,
                    this.StageName,
                    _siloAddress,
                    elapsed));
            }

            private void EmitObserverFailedDiagnostics(Exception exception, TimeSpan elapsed)
            {
                if (!Listener.IsEnabled(OrleansLifecycleDiagnostics.EventNames.ObserverFailed))
                {
                    return;
                }

                Listener.Write(OrleansLifecycleDiagnostics.EventNames.ObserverFailed, new LifecycleObserverFailedEvent(
                    this.Name,
                    this.Stage,
                    this.StageName,
                    _siloAddress,
                    exception,
                    elapsed));
            }

            private void EmitObserverStartingDiagnostics()
            {
                if (!Listener.IsEnabled(OrleansLifecycleDiagnostics.EventNames.ObserverStarting))
                {
                    return;
                }

                Listener.Write(OrleansLifecycleDiagnostics.EventNames.ObserverStarting, new LifecycleObserverStartingEvent(
                    this.Name,
                    this.Stage,
                    this.StageName,
                    _siloAddress));
            }

            private void EmitObserverStoppedDiagnostics(TimeSpan elapsed)
            {
                if (!Listener.IsEnabled(OrleansLifecycleDiagnostics.EventNames.ObserverStopped))
                {
                    return;
                }

                Listener.Write(OrleansLifecycleDiagnostics.EventNames.ObserverStopped, new LifecycleObserverStoppedEvent(
                    this.Name,
                    this.Stage,
                    this.StageName,
                    _siloAddress,
                    elapsed));
            }

            private void EmitObserverStoppingDiagnostics()
            {
                if (!Listener.IsEnabled(OrleansLifecycleDiagnostics.EventNames.ObserverStopping))
                {
                    return;
                }

                Listener.Write(OrleansLifecycleDiagnostics.EventNames.ObserverStopping, new LifecycleObserverStoppingEvent(
                    this.Name,
                    this.Stage,
                    this.StageName,
                    _siloAddress));
            }
        }
    }
}
