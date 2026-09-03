using System;
using System.Runtime.Serialization;
using Orleans.Runtime;

namespace Orleans.Providers
{
    internal enum ProviderState
    {
        None,

        Initialized,
        Started,
        Closed
    }
    internal class ProviderStateManager
    {
        public ProviderState State { get; private set; }
        private ProviderState presetState;

        public ProviderStateManager()
        {
            State = ProviderState.None;
        }

        public bool PresetState(ProviderState state)
        {
            presetState = state;
            switch (state)
            {
                case ProviderState.None:
                    throw new ProviderStateException("Provider state can not be set to none.");

                case ProviderState.Initialized:
                    switch (State)
                    {
                        case ProviderState.None:
                            return true;
                    }
                    break;

                case ProviderState.Started:
                    switch (State)
                    {
                        case ProviderState.None:
                            throw new ProviderStateException("Trying to start a provider that hasn't been initialized.");
                        case ProviderState.Initialized:
                            return true;
                        case ProviderState.Closed:
                            throw new ProviderStateException("Trying to start a provider that has been closed.");
                    }
                    break;

                case ProviderState.Closed:
                    switch (State)
                    {
                        case ProviderState.None:
                            throw new ProviderStateException("Trying to close a provider that hasn't been initialized.");
                        case ProviderState.Initialized:
                        case ProviderState.Started:
                            return true;
                    }
                    return true;
            }

            return false;
        }

        public void CommitState()
        {
            State = presetState;
        }
    }

    /// <summary>
    /// Represents an invalid provider lifecycle state or state transition.
    /// </summary>
    [Serializable, GenerateSerializer]
    public sealed class ProviderStateException : OrleansException
    {
        /// <summary>
        /// Initializes a new instance of the <see cref="ProviderStateException"/> class.
        /// </summary>
        public ProviderStateException() : base("Unexpected provider state")
        { }

        /// <summary>
        /// Initializes a new instance of the <see cref="ProviderStateException"/> class.
        /// </summary>
        /// <param name="message">The message that describes the error.</param>
        public ProviderStateException(string message) : base(message) { }

        /// <summary>
        /// Initializes a new instance of the <see cref="ProviderStateException"/> class.
        /// </summary>
        /// <param name="message">The message that describes the error.</param>
        /// <param name="innerException">The exception that caused the current exception.</param>
        public ProviderStateException(string message, Exception innerException) : base(message, innerException) { }

        [Obsolete]
        private ProviderStateException(SerializationInfo info, StreamingContext context)
            : base(info, context)
        {
        }
    }

}
