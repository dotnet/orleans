using System;
using System.Runtime.Serialization;
using Orleans.Providers;
using Orleans.Runtime;

namespace Orleans.Storage
{
    /// <summary>
    /// Interface to be implemented for a storage provider able to read and write Orleans grain state data.
    /// </summary>
    public interface IStorageProvider : IGrainStorage, IProvider
    {
    }

    /// <summary>
    /// Exception thrown whenever a grain call is attempted with a bad / missing storage provider configuration settings for that grain.
    /// </summary>
    [Serializable]
    public class BadProviderConfigException : OrleansException
    {
        public BadProviderConfigException()
        { }
        public BadProviderConfigException(string msg)
            : base(msg)
        { }
        public BadProviderConfigException(string msg, Exception exc)
            : base(msg, exc)
        { }

        protected BadProviderConfigException(SerializationInfo info, StreamingContext context)
            : base(info, context)
        { }
    }
}
