using Orleans.Streams;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace UnitTests.Grains
{
    public interface IExternalStreamSubscriptionHandle
    {
        Task UnsubscribeAsync();
    }

    public class ExternalStreamSubscriptionHandle<T> : StreamSubscriptionHandle<T>, IExternalStreamSubscriptionHandle
    {
        private StreamSubscriptionHandle<T> handle;
        public ExternalStreamSubscriptionHandle(StreamSubscriptionHandle<T> handle)
        {
            this.handle = handle;
        }

        public override IStreamIdentity StreamIdentity => this.handle.StreamIdentity;

        public override string ProviderName => this.handle.ProviderName;

        public override Guid HandleId => this.handle.HandleId;

        public override Task UnsubscribeAsync()
        {
            return this.handle.UnsubscribeAsync();
        }

        public override Task<StreamSubscriptionHandle<T>> ResumeAsync(IAsyncObserver<T> observer, StreamSequenceToken token = null)
        {
            return this.handle.ResumeAsync(observer, token);
        }

        public override bool Equals(StreamSubscriptionHandle<T> other)
        {
            return this.handle.Equals(other);
        }
    }
}
