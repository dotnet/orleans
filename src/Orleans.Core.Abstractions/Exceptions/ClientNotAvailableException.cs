﻿using System;
using System.Runtime.Serialization;
using Orleans.Core;

namespace Orleans.Runtime
{
    /// <summary>
    /// Indicates that a client is not longer reachable.
    /// </summary>
    [Serializable]
    public class ClientNotAvailableException : OrleansException
    {
        internal ClientNotAvailableException(IGrainIdentity clientId) : base("No activation for client " + clientId) { }
        internal ClientNotAvailableException(string msg) : base(msg) { }
        internal ClientNotAvailableException(string message, Exception innerException) : base(message, innerException) { }

        protected ClientNotAvailableException(SerializationInfo info, StreamingContext context)
            : base(info, context)
        { }
    }
}

