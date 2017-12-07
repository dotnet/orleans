using System;
using System.Runtime.Serialization;

namespace Orleans.Runtime
{
    /// <summary>
    /// An exception class used by the Orleans runtime for reporting errors.
    /// </summary>
    /// <remarks>
    /// This is also the base class for any more specific exceptions 
    /// raised by the Orleans runtime.
    /// </remarks>
    [Serializable]
    public class OrleansException : Exception
    {
        public OrleansException() : base("Unexpected error.") { }

        public OrleansException(string message) : base(message) { }

        public OrleansException(string message, Exception innerException) : base(message, innerException) { }

        protected OrleansException(SerializationInfo info, StreamingContext context)
            : base(info, context)
        {
        }
    }
}