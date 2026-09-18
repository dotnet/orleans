using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Orleans.Core;
using Orleans.Internal;
using Orleans.Runtime;

namespace Orleans.Streams;

[GenerateSerializer]
internal sealed class PullingAgentPublisherState
{
    [Id(0)]
    public HashSet<QualifiedStreamId> Streams { get; set; } = [];
}

internal sealed class PullingAgentPublisherRegistry(
    IStorage<PullingAgentPublisherState> storage,
    IStreamPubSub pubSub,
    GrainId producer,
    ILogger logger)
{
    private readonly SemaphoreSlim _storageLock = new(1);
    private AdmissionGate _registrations = new();
    private bool _reload;

    internal Task Load(CancellationToken cancellationToken) => storage.ReadStateAsync(cancellationToken);

    internal void Open() => _registrations = new();

    internal Task<ISet<PubSubSubscriptionState>> RegisterProducer(QualifiedStreamId stream, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return RegisterCore(stream).WaitAsync(cancellationToken);
    }

    private async Task<ISet<PubSubSubscriptionState>> RegisterCore(QualifiedStreamId stream)
    {
        using var admission = _registrations.TryEnter();
        if (!admission.Entered)
        {
            throw new InvalidOperationException($"Publisher registration for {producer} is stopped.");
        }

        try
        {
            await _storageLock.WaitAsync();
            try
            {
                await Reload();
                if (storage.State.Streams.Add(stream))
                {
                    await storage.WriteStateAsync(CancellationToken.None);
                }
            }
            catch
            {
                _reload = true;
                throw;
            }
            finally
            {
                _storageLock.Release();
            }

            // Track the actual RPC completion, even when the engine abandons its wait during shutdown.
            return await pubSub.RegisterProducer(stream, producer, CancellationToken.None);
        }
        catch (Exception exception)
        {
            logger.LogError(exception, "Failed to register durable pulling-agent publisher {Producer} for {Stream}.", producer, stream);
            throw;
        }
    }

    internal Task Drain() => _registrations.CloseAsync();

    internal async Task Retire()
    {
        await Drain();
        await _storageLock.WaitAsync();
        try
        {
            await Reload();
            foreach (var stream in storage.State.Streams.ToArray())
            {
                await pubSub.UnregisterProducer(stream, producer, CancellationToken.None);
                storage.State.Streams.Remove(stream);
                await storage.WriteStateAsync(CancellationToken.None);
            }
        }
        catch (Exception exception)
        {
            _reload = true;
            logger.LogError(exception, "Failed to retire durable pulling-agent publisher {Producer}. Retry provider stop before changing hosting mode.", producer);
            throw;
        }
        finally
        {
            _storageLock.Release();
        }
    }

    private async Task Reload()
    {
        if (_reload)
        {
            await storage.ReadStateAsync(CancellationToken.None);
            _reload = false;
        }
    }
}
