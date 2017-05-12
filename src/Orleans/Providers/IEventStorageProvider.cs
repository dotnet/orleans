using System;
using System.Net;
using System.Runtime.Serialization;
using System.Threading.Tasks;
using Orleans.Providers;
using Orleans.Runtime;
using System.Collections.Generic;

namespace Orleans.Storage
{
    /// <summary>
    /// Interface to be implemented for a event storage provider able to read from and append to streams
    /// representing the state of Journaled Grains
    /// </summary>
    public interface IEventStorageProvider : IProvider, IEventStorage
    {
        /// <summary>Logger used by this event storage provider instance.</summary>
        /// <returns>Reference to the Logger object used by this provider.</returns>
        /// <seealso cref="Logger"/>
        Logger Log { get; }

        /// <summary>
        /// The provider can specify a default stream name to use for a given grain.
        /// </summary>
        /// <param name="grainType">The type of the grain</param>
        /// <param name="grainReference">The grain reference for the grain</param>
        /// <returns></returns>
        string DefaultStreamName(Type grainType, GrainReference grainReference);
    }

    /// <summary>
    /// The event store interface, as implemented by event storage providers
    /// </summary>
    /// 
    public interface IEventStorage
    {
        /// <summary>
        /// Returns a handle to an event stream that can be used to perform operations on it.
        /// </summary>
        /// <param name="streamName">The name of the stream</param>
        /// <returns>A handle for performing operations on the stream</returns>
        IEventStreamHandle GetEventStreamHandle(string streamName);
    }

    /// <summary>
    /// The event stream interface, as implemented by event storage providers. 
    /// Should be disposed when no longer needed.
    /// </summary>
    public interface IEventStreamHandle : IDisposable
    {
        /// <summary>
        /// The name of this event stream.
        /// </summary>
        string StreamName { get; }

        /// <summary>
        /// Returns the number of events in the event stream, which corresponds to the version number.
        /// Returns zero for a not-yet-created stream or for a deleted stream.
        /// </summary>
        /// <returns>a nonnegative integer representing the number of events in the stream.</returns>
        Task<int> GetVersion();

        /// <summary>
        /// Loads a sequence of events from the event stream.
        /// </summary>
        /// <typeparam name="E">The base class for the events</typeparam>
        /// <param name="startAtVersion">The version (stream position) at which to start</param>
        /// <param name="endAtVersion">The version (stream position) at which to end, or null to return up and including the most recent</param>
        /// <returns>A <see cref="EventStreamSegment{E}"/> structure containing a subrange of the event stream.</returns>
        Task<EventStreamSegment<E>> Load<E>(int startAtVersion = 0, int? endAtVersion = null);

        /// <summary>
        /// Appends a sequence of events to the event stream.
        /// </summary>
        /// <typeparam name="E">The base class for the events</typeparam>
        /// <param name="events">The sequence of events, including their Guids</param>
        /// <param name="expectedVersion">null for unconditional events, or the expected version (stream position) for conditional events</param>
        /// <returns>a boolean task that returns true if the append succeeded, or false if the version (stream position) did not match</returns>
        Task<bool> Append<E>(IEnumerable<KeyValuePair<Guid, E>> events, int? expectedVersion = null);


        /// <summary>
        /// Deletes the event stream entirely and permanently. Deleted streams must not be appended to 
        /// after deletion (effect is unspecified).
        /// </summary>
        /// <param name="expectedVersion">null for unconditional events, or the expected version (stream position) for conditional events</param>
        /// <returns>a boolean task that returns true if the deletion succeeded, or false if the version did not match</returns>
        Task<bool> Delete(int? expectedVersion = null);
    }

    /// <summary>
    /// Response returned when loading events from an <see cref="IEventStreamHandle"/>.
    /// The response is always guaranteed to be a valid subrange of the stream,
    /// but may not always match the interval that was requested.
    /// </summary>
    /// <typeparam name="E">The base class for the events</typeparam>
    [Serializable]
    public struct EventStreamSegment<E>
    {
        /// <summary>The name of the stream.</summary>
        public string StreamName;

        ///<summary>The version (stream position) at which this subrange starts. Ranges from 0 to the latest version of the stream.</summary> 
        public int FromVersion;

        ///<summary>The version (stream position) at which this subrange ends. Ranges from 0 to the latest version of the stream.</summary> 
        public int ToVersion;

        /// <summary> 
        /// The sequence of events, each with its Guid. 
        /// Never null. The length of this list is always equal to (ToVersion-FromVersion).
        /// </summary>
        public IReadOnlyList<KeyValuePair<Guid, E>> Events;
    }


}
