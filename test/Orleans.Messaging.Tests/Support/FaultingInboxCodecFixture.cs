using Microsoft.Extensions.DependencyInjection;
using Orleans.Journaling;

namespace Orleans.Messaging.Tests.Support;

public sealed class FaultingInboxCodecFixture : MessagingClusterFixture
{
    public Exception? NextFailure { get; set; }
    public bool FailOnSnapshot { get; set; }
    public bool? FailedSnapshot { get; private set; }
    protected override void ConfigureServices(IServiceCollection services)
    {
        var descriptor = services.Last(entry => entry.ServiceType == typeof(IDurableValueCommandCodec<>)
            && Equals(entry.ServiceKey, "orleans-binary"));
        var implementation = descriptor.KeyedImplementationType!.MakeGenericType(typeof(InboxCommand));
        services.AddKeyedSingleton<IDurableValueCommandCodec<InboxCommand>>("orleans-binary", (sp, _) =>
            new FaultingCodec(this, (IDurableValueCommandCodec<InboxCommand>)ActivatorUtilities.CreateInstance(sp, implementation)));
    }

    private sealed class FaultingCodec(FaultingInboxCodecFixture owner, IDurableValueCommandCodec<InboxCommand> inner)
        : IDurableValueCommandCodec<InboxCommand>
    {
        public void WriteSet(InboxCommand value, JournalStreamWriter writer)
        {
            var snapshot = value.Kind == InboxCommandKind.Snapshot;
            if (snapshot == owner.FailOnSnapshot && owner.NextFailure is { } failure)
            {
                owner.NextFailure = null;
                owner.FailedSnapshot = snapshot;
                throw failure;
            }

            inner.WriteSet(value, writer);
        }

        public void Apply(JournalBufferReader input, IDurableValueCommandHandler<InboxCommand> consumer) => inner.Apply(input, consumer);
    }
}
