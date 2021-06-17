using System;
using System.Runtime.Serialization;

namespace Orleans.Runtime
{
    /// <summary>
    /// Indicates a lifecycle was canceled, either by request or due to observer error.
    /// </summary>
    [Serializable]
    [GenerateSerializer]
    public class OrleansLifecycleCanceledException : OrleansException
    {
        internal OrleansLifecycleCanceledException(string message)
            : base(message)
        {
        }

        internal OrleansLifecycleCanceledException(string message,
            Exception innerException) : base(message, innerException)
        {
        }

        protected OrleansLifecycleCanceledException(SerializationInfo info, StreamingContext context)
            : base(info, context)
        { }
    }
}

