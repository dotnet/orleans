using System;
using System.Threading.Tasks;
using System.Runtime.Serialization;
using Orleans.Providers;
using Orleans.Runtime;

namespace Orleans.Streams
{
    public interface IStreamProviderImpl : IStreamProvider, IProvider
    {
        Task Start();
    }

    /// <summary>
    /// Exception thrown whenever a provider has failed to be started.
    /// </summary>
    [Serializable]
    public class ProviderStartException : OrleansException
    {
        public ProviderStartException()
        { }
        public ProviderStartException(string msg)
            : base(msg)
        { }
        public ProviderStartException(string msg, Exception exc)
            : base(msg, exc)
        { }
#if !NETSTANDARD
        protected ProviderStartException(SerializationInfo info, StreamingContext context)
            : base(info, context)
        { }
#endif
    }
}
