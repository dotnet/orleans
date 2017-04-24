using System;
using System.Runtime.Serialization;

namespace Orleans.Storage.Internal
{
    /// <summary>Exception used to communicate with the storage provider, so that it throws this exception to its caller.</summary>
    [Serializable]
    internal class WrappedException : Exception
    {
        public WrappedException(Exception wrappedException)
            : base(null, wrappedException)
        {
            if (wrappedException == null) throw new ArgumentNullException(nameof(wrappedException));
        }

#if !NETSTANDARD
        protected WrappedException(SerializationInfo info, StreamingContext context)
            : base(info, context)
        { }
#endif
        public override string Message => InnerException.Message;
    }
}
