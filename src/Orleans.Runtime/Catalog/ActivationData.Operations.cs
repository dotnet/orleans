using System;
using System.Buffers;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Orleans.Configuration;
using Orleans.Core.Internal;
using Orleans.Diagnostics;
using Orleans.GrainDirectory;
using Orleans.Internal;
using Orleans.Runtime.Diagnostics;
using Orleans.Runtime.GrainDirectory;
using Orleans.Runtime.Placement;
using Orleans.Runtime.Scheduler;
using Orleans.Serialization.Invocation;
using Orleans.Serialization.Session;
using Orleans.Serialization.TypeSystem;

namespace Orleans.Runtime;

internal sealed partial class ActivationData
{
    private void ScheduleOperation(object operation)
    {
        lock (_lock)
        {
            _operations.Enqueue(operation);
        }

        _messagePump.Signal();
    }

    private void CancelPendingOperations()
    {
        lock (_lock)
        {
            _operations.CancelPending((exception, command) =>
            {
                LogErrorCancellingOperation(_shared.Logger, exception, command);
            });
        }
    }

    private static async Task ProcessOperationsAsync(ActivationData activation)
    {
        object? operation = null;
        while (true)
        {
            lock (activation._lock)
            {
                if (operation is not null)
                {
                    activation._operations.CompleteCurrent();
                }

                if (!activation._operations.TryPeek(out operation))
                {
                    return;
                }
            }

            try
            {
                switch (operation)
                {
                    case Command.Rehydrate command:
                        activation.RehydrateInternal(command.Context);
                        break;
                    case Command.Activate command:
                        await ActivateAsync(activation, command.RequestContext, command.Metrics, command.CancellationToken).SuppressThrowing();
                        break;
                    case Command.Deactivate command:
                        await FinishDeactivating(activation, command, command.CancellationToken).SuppressThrowing();
                        break;
                    case Command.Delay command:
                        await Task.Delay(command.Duration, activation.GrainRuntime.TimeProvider, command.CancellationToken).SuppressThrowing();
                        break;
                    default:
                        throw new NotSupportedException($"Encountered unknown operation of type {operation?.GetType().ToString() ?? "null"} {operation}.");
                }
            }
            catch (Exception exception)
            {
                LogErrorInProcessOperationsAsync(activation._shared.Logger, exception, activation);
            }
            finally
            {
                if (operation is not null)
                {
                    await DisposeAsync(operation);
                }
            }
        }
    }

    private abstract class Command(CancellationTokenSource cts) : IDisposable
    {
        private bool _disposed;
#if NET10_0_OR_GREATER
        private readonly Lock _lock = new();
#else
        private readonly object _lock = new();
#endif
        private readonly CancellationTokenSource _cts = cts;
        public CancellationToken CancellationToken => _cts.Token;

        public virtual void Cancel()
        {
            lock (_lock)
            {
                if (_disposed) return;
                _cts.Cancel();
            }
        }

        public virtual void Dispose()
        {
            try
            {
                lock (_lock)
                {
                    _disposed = true;
                    _cts.Dispose();
                }
            }
            catch
            {
                // Ignore.
            }

            GC.SuppressFinalize(this);
        }

        public sealed class Deactivate(CancellationTokenSource cts, ActivationState previousState, Activity? activity) : Command(cts)
        {
            public ActivationState PreviousState { get; } = previousState;
            public Activity? Activity { get; } = activity;
        }

        public sealed class Activate(Dictionary<string, object>? requestContext, CancellationTokenSource cts, CatalogInstruments.ActivationMetricTracker metrics) : Command(cts)
        {
            public Dictionary<string, object>? RequestContext { get; } = requestContext;
            public CatalogInstruments.ActivationMetricTracker Metrics { get; } = metrics;
        }

        public sealed class Rehydrate(IRehydrationContext context) : Command(new())
        {
            public readonly IRehydrationContext Context = context;

            public override void Dispose()
            {
                base.Dispose();
                (Context as IDisposable)?.Dispose();
            }
        }

        public sealed class Delay(TimeSpan duration) : Command(new())
        {
            public TimeSpan Duration { get; } = duration;
        }
    }
}
