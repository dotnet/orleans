using System;

namespace Orleans
{
    /// <summary>
    /// Defines the state of a grain
    /// </summary>
    /// <typeparam name="T">
    /// The underlying state type.
    /// </typeparam>
    public interface IGrainState<T>
    {
        /// <summary>
        /// Gets or sets the state.
        /// </summary>
        T State { get; set; }

        /// <summary>Gets or sets the ETag that allows optimistic concurrency checks at the storage provider level.</summary>
        string ETag { get; set; }

        /// <summary>
        /// Gets or sets a value indicating whether the record exists in storage.
        /// </summary>
        bool RecordExists { get; set; }
    }

    /// <summary>
    /// Default implementation of <see cref="IGrainState{T}"/>.
    /// </summary>
    /// <typeparam name="T">The type of application level payload.</typeparam>
    [Serializable]
    [GenerateSerializer]
    public sealed class GrainState<T> : IGrainState<T>
    {
        /// <inheritdoc />
        [Id(0)]
        public T State { get; set; }

        /// <inheritdoc />
        [Id(1)]
        public string ETag { get; set; }

        /// <inheritdoc />
        [Id(2)]
        public bool RecordExists { get; set; }

        /// <summary>
        /// Initializes a new instance of the <see cref="GrainState{T}"/> class. 
        /// </summary>
        public GrainState()
        {
        }

        /// <summary>
        /// Initializes a new instance of the <see cref="GrainState{T}"/> class. 
        /// </summary>
        /// <param name="state">
        /// The initial value of the state.
        /// </param>
        public GrainState(T state) : this(state, null)
        {
        }

        /// <summary>
        /// Initializes a new instance of the <see cref="GrainState{T}"/> class.
        /// </summary>
        /// <param name="state">
        /// The initial value of the state.
        /// </param>
        /// <param name="eTag">
        /// The initial e-tag value that allows optimistic concurrency checks at the storage provider level.
        /// </param>
        public GrainState(T state, string eTag)
        {
            State = state;
            ETag = eTag;
        }
    }
}
