using Orleans.Streams;
using Orleans.Runtime;
using Orleans.Streams.PubSub;

namespace UnitTests.Grains.ProgrammaticSubscribe
{
    public interface ISubscribeGrain : IGrainWithGuidKey
    {
        Task<bool> CanGetSubscriptionManager(string providerName);
    }

    public class SubscribeGrain : Grain, ISubscribeGrain
    {
        public Task<bool> CanGetSubscriptionManager(string providerName)
        {
            return Task.FromResult(this.ServiceProvider.GetServiceByName<IStreamProvider>(providerName).TryGetStreamSubscriptionManager(out _));
        }
    }

    [Serializable]
    [GenerateSerializer]
    public class FullStreamIdentity : IStreamIdentity
    {
        public FullStreamIdentity(Guid streamGuid, string streamNamespace, string providerName)
        {
            Guid = streamGuid;
            Namespace = streamNamespace;
            this.ProviderName = providerName;
        }

        [Id(0)]
        public string ProviderName;

        /// <summary>
        /// Stream primary key guid.
        /// </summary>
        [Id(1)]
        public Guid Guid { get; }

        /// <summary>
        /// Stream namespace.
        /// </summary>
        [Id(2)]
        public string Namespace { get; }

        public static implicit operator StreamId(FullStreamIdentity identity) => StreamId.Create(identity);
    }
}
