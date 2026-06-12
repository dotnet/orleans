using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using Orleans.Metadata;

namespace Orleans.Core.Diagnostics;

internal static class ManifestEvents
{
    internal const string ListenerName = "Orleans.Manifest";

    private static readonly DiagnosticListener Listener = new(ListenerName);

    internal static IObservable<ManifestEvent> AllEvents { get; } = new Observable();

    internal static bool IsClusterManifestUpdatedEnabled() => Listener.IsEnabled(nameof(ClusterManifestUpdated));

    internal abstract class ManifestEvent
    {
    }

    internal sealed class ClusterManifestUpdated(
        object source,
        ClusterManifest manifest) : ManifestEvent
    {
        public readonly object Source = source;

        public readonly ClusterManifest Manifest = manifest;
    }

    internal static void EmitClusterManifestUpdated(object source, ClusterManifest manifest)
    {
        if (!IsClusterManifestUpdatedEnabled())
        {
            return;
        }

        Emit(source, manifest);

        [MethodImpl(MethodImplOptions.NoInlining)]
        static void Emit(object source, ClusterManifest manifest)
        {
            Listener.Write(nameof(ClusterManifestUpdated), new ClusterManifestUpdated(source, manifest));
        }
    }

    private sealed class Observable : IObservable<ManifestEvent>
    {
        public IDisposable Subscribe(IObserver<ManifestEvent> observer) => Listener.Subscribe(new Observer(observer));

        private sealed class Observer(IObserver<ManifestEvent> observer) : IObserver<KeyValuePair<string, object?>>
        {
            public void OnCompleted() => observer.OnCompleted();
            public void OnError(Exception error) => observer.OnError(error);

            public void OnNext(KeyValuePair<string, object?> value)
            {
                if (value.Value is ManifestEvent evt)
                {
                    observer.OnNext(evt);
                }
            }
        }
    }
}
