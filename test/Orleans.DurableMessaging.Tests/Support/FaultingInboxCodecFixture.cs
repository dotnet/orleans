using Microsoft.Extensions.DependencyInjection;
using Orleans.Journaling;
using Orleans.Runtime;

namespace Orleans.DurableMessaging.Tests.Support;

public sealed class FaultingInboxCodecFixture : DurableMessagingClusterFixture
{
    public Exception? NextFailure { get; set; }
    protected override void ConfigureServices(IServiceCollection services)
    {
        var descriptor = services.Last(entry => entry.ServiceType == typeof(IDurableDictionaryCommandCodec<,>)
            && Equals(entry.ServiceKey, "orleans-binary"));
        var implementation = descriptor.KeyedImplementationType!.MakeGenericType(typeof((GrainId, Guid)), typeof(DurableEnvelope));
        services.AddKeyedSingleton<IDurableDictionaryCommandCodec<(GrainId, Guid), DurableEnvelope>>("orleans-binary", (sp, _) =>
            new FaultingCodec(this, (IDurableDictionaryCommandCodec<(GrainId, Guid), DurableEnvelope>)ActivatorUtilities.CreateInstance(sp, implementation)));
    }

    private sealed class FaultingCodec(FaultingInboxCodecFixture owner,
        IDurableDictionaryCommandCodec<(GrainId, Guid), DurableEnvelope> inner)
        : IDurableDictionaryCommandCodec<(GrainId, Guid), DurableEnvelope>
    {
        private void Check()
        {
            if (owner.NextFailure is { } failure)
            {
                owner.NextFailure = null;
                throw failure;
            }
        }
        public void WriteSet((GrainId, Guid) key, DurableEnvelope value, JournalStreamWriter writer)
        {
            Check();
            inner.WriteSet(key, value, writer);
        }
        public void WriteRemove((GrainId, Guid) key, JournalStreamWriter writer) { Check(); inner.WriteRemove(key, writer); }
        public void WriteClear(JournalStreamWriter writer) { Check(); inner.WriteClear(writer); }
        public void WriteSnapshot(IReadOnlyCollection<KeyValuePair<(GrainId, Guid), DurableEnvelope>> items, JournalStreamWriter writer)
        {
            Check();
            inner.WriteSnapshot(items, writer);
        }
        public void Apply(JournalBufferReader input, IDurableDictionaryCommandHandler<(GrainId, Guid), DurableEnvelope> consumer) => inner.Apply(input, consumer);
    }
}
