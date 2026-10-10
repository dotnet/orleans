using Microsoft.Extensions.DependencyInjection;
using Orleans.Journaling;
using Orleans.Runtime;

namespace Orleans.DurableMessaging.Tests.Support;

public sealed class FaultingInboxCodecFixture : DurableMessagingClusterFixture
{
    public Exception? NextFailure { get; set; }
    public bool FailOnSnapshot { get; set; }
    public bool? FailedSnapshot { get; private set; }
    protected override void ConfigureServices(IServiceCollection services)
    {
        var descriptor = services.Last(entry => entry.ServiceType == typeof(IDurableDictionaryCommandCodec<,>)
            && Equals(entry.ServiceKey, "orleans-binary"));
        var implementation = descriptor.KeyedImplementationType!.MakeGenericType(typeof(HierarchicalKey), typeof(DurableEnvelope));
        services.AddKeyedSingleton<IDurableDictionaryCommandCodec<HierarchicalKey, DurableEnvelope>>("orleans-binary", (sp, _) =>
            new FaultingCodec(this, (IDurableDictionaryCommandCodec<HierarchicalKey, DurableEnvelope>)ActivatorUtilities.CreateInstance(sp, implementation)));
    }

    private sealed class FaultingCodec(FaultingInboxCodecFixture owner,
        IDurableDictionaryCommandCodec<HierarchicalKey, DurableEnvelope> inner)
        : IDurableDictionaryCommandCodec<HierarchicalKey, DurableEnvelope>
    {
        private void Check(bool snapshot = false)
        {
            if (snapshot == owner.FailOnSnapshot && owner.NextFailure is { } failure)
            {
                owner.NextFailure = null;
                owner.FailedSnapshot = snapshot;
                throw failure;
            }
        }
        public void WriteSet(HierarchicalKey key, DurableEnvelope value, JournalStreamWriter writer)
        {
            Check();
            inner.WriteSet(key, value, writer);
        }
        public void WriteRemove(HierarchicalKey key, JournalStreamWriter writer) { Check(); inner.WriteRemove(key, writer); }
        public void WriteClear(JournalStreamWriter writer) { Check(); inner.WriteClear(writer); }
        public void WriteSnapshot(IReadOnlyCollection<KeyValuePair<HierarchicalKey, DurableEnvelope>> items, JournalStreamWriter writer)
        {
            Check(snapshot: true);
            inner.WriteSnapshot(items, writer);
        }
        public void Apply(JournalBufferReader input, IDurableDictionaryCommandHandler<HierarchicalKey, DurableEnvelope> consumer) => inner.Apply(input, consumer);
    }
}
