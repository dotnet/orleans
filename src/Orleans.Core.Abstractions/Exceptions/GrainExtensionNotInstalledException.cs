using System;
using System.Runtime.Serialization;

namespace Orleans.Runtime
{
    /// <summary>
    /// Signifies that an attempt was made to invoke a grain extension method on a grain where that extension was not installed.
    /// </summary>
    [Serializable]
    [GenerateSerializer]
    public class GrainExtensionNotInstalledException : OrleansException
    {
        public GrainExtensionNotInstalledException() : base("GrainExtensionNotInstalledException") { }
        public GrainExtensionNotInstalledException(string msg) : base(msg) { }
        public GrainExtensionNotInstalledException(string message, Exception innerException) : base(message, innerException) { }

        protected GrainExtensionNotInstalledException(SerializationInfo info, StreamingContext context)
            : base(info, context)
        { }
    }
}

