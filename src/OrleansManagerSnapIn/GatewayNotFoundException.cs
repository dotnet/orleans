using System;
using System.Runtime.Serialization;
using Orleans.Runtime;
using OrleansManager.Properties;

namespace OrleansManager
{
    /// <summary>
    /// Use when <see cref="Orleans.GrainClient.Gateways"/> doesn't return any <see cref="System.Uri"/>.
    /// </summary>
    [Serializable]
    public class GatewayNotFoundException : OrleansException
    {
        public GatewayNotFoundException() 
            : base(Resources.GatewayNotFound)
        {
        }

        public GatewayNotFoundException(string message) 
            : base(message)
        {
        }

        public GatewayNotFoundException(string message, Exception innerException) 
            : base(message, innerException)
        {
        }

        protected GatewayNotFoundException(SerializationInfo info, StreamingContext context)
            : base(info, context)
        {
        }
    }
}