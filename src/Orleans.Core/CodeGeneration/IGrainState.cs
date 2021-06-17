using System;

namespace Orleans
{
    /// <summary>Defines the state of a grain</summary>
    public interface IGrainState
    {
        /// <summary>The application level payload that is the actual state.</summary>
        object State { get; set; }

        /// <summary>Type of the grain state</summary>
        Type Type { get; }

        /// <summary>An e-tag that allows optimistic concurrency checks at the storage provider level.</summary>
        string ETag { get; set; }

        bool RecordExists { get; set; }
    }

    /// <summary>
    /// Typed default implementation of <see cref="IGrainState"/>.
    /// </summary>
    /// <typeparam name="T">The type of application level payload.</typeparam>
    [Serializable]
    [GenerateSerializer]
    public class GrainState<T> : IGrainState
    {
        [Id(1)]
        public T State;

        object IGrainState.State
        {
            get => State;
            set => State = (T)value;
        }

        /// <inheritdoc />
        public Type Type => typeof(T);

        /// <inheritdoc />
        [Id(2)]
        public string ETag { get; set; }
        [Id(3)]
        public bool RecordExists { get; set; }

        /// <summary>Initializes a new instance of <see cref="GrainState{T}"/>.</summary>
        public GrainState()
        {
        }

        /// <summary>Initializes a new instance of <see cref="GrainState{T}"/>.</summary>
        /// <param name="state"> The initial value of the state.</param>
        public GrainState(T state) : this(state, null)
        {
        }

        /// <summary>Initializes a new instance of <see cref="GrainState{T}"/>.</summary>
        /// <param name="state">The initial value of the state.</param>
        /// <param name="eTag">The initial e-tag value that allows optimistic concurrency checks at the storage provider level.</param>
        public GrainState(T state, string eTag)
        {
            State = state;
            ETag = eTag;
        }
    }
}
