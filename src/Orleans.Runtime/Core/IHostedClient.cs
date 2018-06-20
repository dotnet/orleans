using System;
using System.Threading.Tasks;
using Orleans.CodeGeneration;
using Orleans.Streams;

namespace Orleans.Runtime
{
    /// <summary>
    /// A client which is hosted within a silo.
    /// </summary>
    internal interface IHostedClient
    {
        ActivationAddress ClientAddress { get; }
        GrainId ClientId { get; }
        StreamDirectory StreamDirectory { get; }
        GrainReference CreateObjectReference(IAddressable obj, IGrainMethodInvoker invoker);
        void DeleteObjectReference(IAddressable obj);
        Task<Tuple<TExtension, TExtensionInterface>> BindExtension<TExtension, TExtensionInterface>(Func<TExtension> newExtensionFunc)
            where TExtension : IGrainExtension
            where TExtensionInterface : IGrainExtension;
        bool TryDispatchToClient(Message message);
    }

    /// <summary>
    /// An implementation of IHostedClient which throws an exception whenever it is accessed in order to instruct the user to enable external context calls.
    /// </summary>
    internal class DisabledHostedClient : IHostedClient
    {
        public ActivationAddress ClientAddress => ThrowDisabledException<ActivationAddress>();
        public GrainId ClientId => ThrowDisabledException<GrainId>();
        public StreamDirectory StreamDirectory => ThrowDisabledException<StreamDirectory>();
        public GrainReference CreateObjectReference(IAddressable obj, IGrainMethodInvoker invoker) => ThrowDisabledException<GrainReference>();

        public void DeleteObjectReference(IAddressable obj) => ThrowDisabledException<int>();

        public Task<Tuple<TExtension, TExtensionInterface>> BindExtension<TExtension, TExtensionInterface>(Func<TExtension> newExtensionFunc)
            where TExtension : IGrainExtension where TExtensionInterface : IGrainExtension => ThrowDisabledException<Task<Tuple<TExtension, TExtensionInterface>>>();

        public bool TryDispatchToClient(Message message) => ThrowDisabledException<bool>();

        private static T ThrowDisabledException<T>() =>
            throw new InvalidOperationException(
                "Support for accessing the Orleans runtime from a non-Orleans context is not enabled. Enable support using the ISiloHostBuilder.EnableExternalContextCalls() method.");
    }
}