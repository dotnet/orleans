using Orleans.Runtime;
using Orleans.Streams.Core;
using System;
using System.Threading.Tasks;

namespace Orleans.Streams
{
    public interface IStreamProvider
    {
        /// <summary>Name of the stream provider.</summary>
        string Name { get; }

        IAsyncStream<T> GetStream<T>(Guid streamId, string streamNamespace);
        /// <summary>
        /// Determines whether this is a rewindable provider - supports creating rewindable streams 
        /// (streams that allow subscribing from previous point in time).
        /// </summary>
        /// <returns>True if this is a rewindable provider, false otherwise.</returns>
        bool IsRewindable { get; }

        /// <summary>
        /// 
        /// </summary>
        /// <typeparam name="T">Type the stream subscription handle is handling</typeparam>
        /// <param name="onAdd">delegate which will be executed when subscription added</param>
        /// <param name="onRemove">delegate which will be executed when subscription removed. Parameter of the delegate is streamProviderName, stream identity and subscription Id </param>
        Task OnSubscriptionChange<T>(Func<StreamSubscriptionHandle<T>, Task> onAdd, Func<string, IStreamIdentity, Guid, Task> onRemove = null);
    }
}

