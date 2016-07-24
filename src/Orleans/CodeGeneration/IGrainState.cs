using System;

namespace Orleans
{
    public interface IGrainState
    {
        object State { get; set; }
        string ETag { get; set; }
    }

    [Serializable]
    internal class GrainState<T> : IGrainState
    {
        public T State;

        object IGrainState.State
        {
            get
            {
                return State;
                
            }
            set
            {
                State = (T)value;
            }
        }

        public string ETag { get; set; }

        public GrainState()
        {
        }

        public GrainState(T state) : this(state, null)
        {
        }

        public GrainState(T state, string eTag)
        {
            State = state;
            ETag = eTag;
        }
    }
}
