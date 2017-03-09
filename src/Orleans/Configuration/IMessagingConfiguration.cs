using System;
using System.Collections.Generic;
using System.Reflection;

namespace Orleans.Runtime.Configuration
{
    /// <summary>
    /// Specifies global messaging configuration that are common to client and silo.
    /// </summary>
    public interface IMessagingConfiguration
    {
        /// <summary>
        /// The OpenConnectionTimeout attribute specifies the timeout before a connection open is assumed to have failed
        /// </summary>
        TimeSpan OpenConnectionTimeout { get; set; }
        /// <summary>
        /// The ResponseTimeout attribute specifies the default timeout before a request is assumed to have failed.
        /// </summary>
        TimeSpan ResponseTimeout { get; set; }
        /// <summary>
        /// The MaxResendCount attribute specifies the maximal number of resends of the same message.
        /// </summary>
        int MaxResendCount { get; set; }
        /// <summary>
        /// The ResendOnTimeout attribute specifies whether the message should be automaticaly resend by the runtime when it times out on the sender.
        /// Default is false.
        /// </summary>
        bool ResendOnTimeout { get; set; }
        /// <summary>
        /// The MaxSocketAge attribute specifies how long to keep an open socket before it is closed.
        /// Default is TimeSpan.MaxValue (never close sockets automatically, unles they were broken).
        /// </summary>
        TimeSpan MaxSocketAge { get; set; }
        /// <summary>
        /// The DropExpiredMessages attribute specifies whether the message should be dropped if it has expired, that is if it was not delivered 
        /// to the destination before it has timed out on the sender.
        /// Default is true.
        /// </summary>
        bool DropExpiredMessages { get; set; }

        /// <summary>
        /// The SiloSenderQueues attribute specifies the number of parallel queues and attendant threads used by the silo to send outbound
        /// messages (requests, responses, and notifications) to other silos.
        /// If this attribute is not specified, then System.Environment.ProcessorCount is used.
        /// </summary>
        int SiloSenderQueues { get; set; }
        /// <summary>
        /// The GatewaySenderQueues attribute specifies the number of parallel queues and attendant threads used by the silo Gateway to send outbound
        ///  messages (requests, responses, and notifications) to clients that are connected to it.
        ///  If this attribute is not specified, then System.Environment.ProcessorCount is used.
        /// </summary>
        int GatewaySenderQueues { get; set; }
        /// <summary>
        ///  The ClientSenderBuckets attribute specifies the total number of grain buckets used by the client in client-to-gateway communication
        ///  protocol. In this protocol, grains are mapped to buckets and buckets are mapped to gateway connections, in order to enable stickiness
        ///  of grain to gateway (messages to the same grain go to the same gateway, while evenly spreading grains across gateways).
        ///  This number should be about 10 to 100 times larger than the expected number of gateway connections.
        ///  If this attribute is not specified, then Math.Pow(2, 13) is used.
        /// </summary>
        int ClientSenderBuckets { get; set; }
        /// <summary>
        ///  This is the period of time a gateway will wait before dropping a disconnected client.
        /// </summary>
        TimeSpan ClientDropTimeout { get; set; }

        /// <summary>
        /// The size of a buffer in the messaging buffer pool.
        /// </summary>
        int BufferPoolBufferSize { get; set; }
        /// <summary>
        /// The maximum size of the messaging buffer pool.
        /// </summary>
        int BufferPoolMaxSize { get; set; }
        /// <summary>
        /// The initial size of the messaging buffer pool that is pre-allocated.
        /// </summary>
        int BufferPoolPreallocationSize { get; set; }

        /// <summary>
        /// The list of serialization providers
        /// </summary>
        List<TypeInfo> SerializationProviders { get; }

        /// <summary>
        /// Gets the fallback serializer, used as a last resort when no other serializer is able to serialize an object.
        /// </summary>
        TypeInfo FallbackSerializationProvider { get; set; }
    }
}