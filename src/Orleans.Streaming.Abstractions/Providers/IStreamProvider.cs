using Orleans.Runtime;
using System;

namespace Orleans.Streams
{
    public interface IStreamProvider
    {
        /// <summary>Name of the stream provider.</summary>
        string Name { get; }

        IAsyncStream<T> GetStream<T>(StreamId streamId);
        /// <summary>
        /// Determines whether this is a rewindable provider - supports creating rewindable streams 
        /// (streams that allow subscribing from previous point in time).
        /// </summary>
        /// <returns>True if this is a rewindable provider, false otherwise.</returns>
        bool IsRewindable { get; }
    }

    public static class StreamProviderExtensions
    {
        // For compatibility
        public static IAsyncStream<T> GetStream<T>(this IStreamProvider streamProvider, Guid id, string ns) => streamProvider.GetStream<T>(StreamId.Create(ns, id));
    }
}

