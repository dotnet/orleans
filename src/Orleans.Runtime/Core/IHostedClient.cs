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
}