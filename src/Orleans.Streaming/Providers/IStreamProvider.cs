using Orleans.Runtime;
using System;

namespace Orleans.Streams
{
    /// <summary>
    /// Functionality for providing streams to consumers and producers.
    /// </summary>
    public interface IStreamProvider
    {
        /// <summary>
        /// Gets the name of the stream provider.
        /// </summary>
        /// <value>The name.</value>
        string Name { get; }

        /// <summary>
        /// Gets the stream with the specified identity.
        /// </summary>
        /// <typeparam name="T">The stream element type.</typeparam>
        /// <param name="streamId">The stream identifier.</param>
        /// <returns>The stream.</returns>
        IAsyncStream<T> GetStream<T>(StreamId streamId);
        /// <summary>
        /// Gets a value indicating whether this is a rewindable provider - supports creating rewindable streams 
        /// (streams that allow subscribing from previous point in time).
        /// </summary>
        /// <returns><see langword="true"/> if this is a rewindable provider, <see langword="false"/> otherwise.</returns>
        bool IsRewindable { get; }
    }

    /// <summary>
    /// Extensions for <see cref="IStreamProvider"/>.
    /// </summary>
    public static class StreamProviderExtensions
    {
        /// <summary>
        /// Gets the stream with the specified identity and namespace.
        /// </summary>
        /// <typeparam name="T">The stream element type.</typeparam>
        /// <param name="streamProvider">The stream provider.</param>
        /// <param name="id">The identifier.</param>
        /// <returns>The stream.</returns>
        public static IAsyncStream<T> GetStream<T>(this IStreamProvider streamProvider, Guid id) => streamProvider.GetStream<T>(StreamId.Create(null, id));

        /// <summary>
        /// Gets the stream with the specified identity and namespace.
        /// </summary>
        /// <typeparam name="T">The stream element type.</typeparam>
        /// <param name="streamProvider">The stream provider.</param>
        /// <param name="ns">The namespace.</param>
        /// <param name="id">The identifier.</param>
        /// <returns>The stream.</returns>
        public static IAsyncStream<T> GetStream<T>(this IStreamProvider streamProvider, string ns, Guid id) => streamProvider.GetStream<T>(StreamId.Create(ns, id));

        /// <summary>
        /// Gets the stream with the specified identity and namespace.
        /// </summary>
        /// <typeparam name="T">The stream element type.</typeparam>
        /// <param name="streamProvider">The stream provider.</param>
        /// <param name="id">The identifier.</param>
        /// <returns>The stream.</returns>
        public static IAsyncStream<T> GetStream<T>(this IStreamProvider streamProvider, string id) => streamProvider.GetStream<T>(StreamId.Create(null, id));

        /// <summary>
        /// Gets the stream with the specified identity and namespace.
        /// </summary>
        /// <typeparam name="T">The stream element type.</typeparam>
        /// <param name="streamProvider">The stream provider.</param>
        /// <param name="ns">The namespace.</param>
        /// <param name="id">The identifier.</param>
        /// <returns>The stream.</returns>
        public static IAsyncStream<T> GetStream<T>(this IStreamProvider streamProvider, string ns, string id) => streamProvider.GetStream<T>(StreamId.Create(ns, id));

        /// <summary>
        /// Gets the stream with the specified identity and namespace.
        /// </summary>
        /// <typeparam name="T">The stream element type.</typeparam>
        /// <param name="streamProvider">The stream provider.</param>
        /// <param name="id">The identifier.</param>
        /// <returns>The stream.</returns>
        public static IAsyncStream<T> GetStream<T>(this IStreamProvider streamProvider, long id) => streamProvider.GetStream<T>(StreamId.Create(null, id));

        /// <summary>
        /// Gets the stream with the specified identity and namespace.
        /// </summary>
        /// <typeparam name="T">The stream element type.</typeparam>
        /// <param name="streamProvider">The stream provider.</param>
        /// <param name="ns">The namespace.</param>
        /// <param name="id">The identifier.</param>
        /// <returns>The stream.</returns>
        public static IAsyncStream<T> GetStream<T>(this IStreamProvider streamProvider, string ns, long id) => streamProvider.GetStream<T>(StreamId.Create(ns, id));
    }
}

