using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Xml;

namespace Orleans.Runtime.Configuration
{
    /// <summary>
    /// Specifies global messaging configuration that are common to client and silo.
    /// </summary>
    public interface IMessagingConfiguration
    {
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
        /// Whether to use automatic batching of messages. Default is false.
        /// </summary>
        bool UseMessageBatching { get; set; }
        /// <summary>
        /// The maximum batch size for automatic batching of messages, when message batching is used.
        /// </summary>
        int MaxMessageBatchingSize { get; set; }

        /// <summary>
        /// The list of serialization providers
        /// </summary>
        List<TypeInfo> SerializationProviders { get; }
    }

    /// <summary>
    /// Messaging configuration that are common to client and silo.
    /// </summary>
    [Serializable]
    public class MessagingConfiguration : IMessagingConfiguration
    {
        public TimeSpan ResponseTimeout { get; set; }
        public int MaxResendCount { get; set; }
        public bool ResendOnTimeout { get; set; }
        public TimeSpan MaxSocketAge { get; set; }
        public bool DropExpiredMessages { get; set; }

        public int SiloSenderQueues { get; set; }
        public int GatewaySenderQueues { get; set; }
        public int ClientSenderBuckets { get; set; }
        public TimeSpan ClientDropTimeout { get; set; }

        public int BufferPoolBufferSize { get; set; }
        public int BufferPoolMaxSize { get; set; }
        public int BufferPoolPreallocationSize { get; set; }

        public bool UseMessageBatching { get; set; }
        public int MaxMessageBatchingSize { get; set; }

        /// <summary>
        /// The MaxForwardCount attribute specifies the maximal number of times a message is being forwared from one silo to another.
        /// Forwarding is used internally by the tuntime as a recovery mechanism when silos fail and the membership is unstable.
        /// In such times the messages might not be routed correctly to destination, and runtime attempts to forward such messages a number of times before rejecting them.
        /// </summary>
        public int MaxForwardCount { get; set; }

        public List<TypeInfo> SerializationProviders { get; private set; }
        internal double RejectionInjectionRate { get; set; }
        internal double MessageLossInjectionRate { get; set; }

        private static readonly TimeSpan DEFAULT_MAX_SOCKET_AGE = TimeSpan.MaxValue;
        internal const int DEFAULT_MAX_FORWARD_COUNT = 2;
        private const bool DEFAULT_RESEND_ON_TIMEOUT = false;
        private static readonly int DEFAULT_SILO_SENDER_QUEUES = Environment.ProcessorCount;
        private static readonly int DEFAULT_GATEWAY_SENDER_QUEUES = Environment.ProcessorCount;
        private static readonly int DEFAULT_CLIENT_SENDER_BUCKETS = (int)Math.Pow(2, 13);

        private const int DEFAULT_BUFFER_POOL_BUFFER_SIZE = 4 * 1024;
        private const int DEFAULT_BUFFER_POOL_MAX_SIZE = 10000;
        private const int DEFAULT_BUFFER_POOL_PREALLOCATION_SIZE = 250;
        private const bool DEFAULT_DROP_EXPIRED_MESSAGES = true;
        private const double DEFAULT_ERROR_INJECTION_RATE = 0.0;
        private const bool DEFAULT_USE_MESSAGE_BATCHING = false;
        private const int DEFAULT_MAX_MESSAGE_BATCH_SIZE = 10;

        private readonly bool isSiloConfig;

        internal MessagingConfiguration(bool isSilo)
        {
            isSiloConfig = isSilo;

            ResponseTimeout = Constants.DEFAULT_RESPONSE_TIMEOUT;
            MaxResendCount = 0;
            ResendOnTimeout = DEFAULT_RESEND_ON_TIMEOUT;
            MaxSocketAge = DEFAULT_MAX_SOCKET_AGE;
            DropExpiredMessages = DEFAULT_DROP_EXPIRED_MESSAGES;

            SiloSenderQueues = DEFAULT_SILO_SENDER_QUEUES;
            GatewaySenderQueues = DEFAULT_GATEWAY_SENDER_QUEUES;
            ClientSenderBuckets = DEFAULT_CLIENT_SENDER_BUCKETS;
            ClientDropTimeout = Constants.DEFAULT_CLIENT_DROP_TIMEOUT;

            BufferPoolBufferSize = DEFAULT_BUFFER_POOL_BUFFER_SIZE;
            BufferPoolMaxSize = DEFAULT_BUFFER_POOL_MAX_SIZE;
            BufferPoolPreallocationSize = DEFAULT_BUFFER_POOL_PREALLOCATION_SIZE;

            if (isSiloConfig)
            {
                MaxForwardCount = DEFAULT_MAX_FORWARD_COUNT;
                RejectionInjectionRate = DEFAULT_ERROR_INJECTION_RATE;
                MessageLossInjectionRate = DEFAULT_ERROR_INJECTION_RATE;
            }
            else
            {
                MaxForwardCount = 0;
                RejectionInjectionRate = 0.0;
                MessageLossInjectionRate = 0.0;
            }
            UseMessageBatching = DEFAULT_USE_MESSAGE_BATCHING;
            MaxMessageBatchingSize = DEFAULT_MAX_MESSAGE_BATCH_SIZE;
            SerializationProviders = new List<TypeInfo>();
        }

        public override string ToString()
        {
            var sb = new StringBuilder();
            sb.AppendFormat("   Messaging:").AppendLine();
            sb.AppendFormat("       Response timeout: {0}", ResponseTimeout).AppendLine();
            sb.AppendFormat("       Maximum resend count: {0}", MaxResendCount).AppendLine();
            sb.AppendFormat("       Resend On Timeout: {0}", ResendOnTimeout).AppendLine();
            sb.AppendFormat("       Maximum Socket Age: {0}", MaxSocketAge).AppendLine();
            sb.AppendFormat("       Drop Expired Messages: {0}", DropExpiredMessages).AppendLine();

            if (isSiloConfig)
            {
                sb.AppendFormat("       Silo Sender queues: {0}", SiloSenderQueues).AppendLine();
                sb.AppendFormat("       Gateway Sender queues: {0}", GatewaySenderQueues).AppendLine();
                sb.AppendFormat("       Client Drop Timeout: {0}", ClientDropTimeout).AppendLine();
            }
            else
            {
                sb.AppendFormat("       Client Sender Buckets: {0}", ClientSenderBuckets).AppendLine();
            }
            sb.AppendFormat("       Buffer Pool Buffer Size: {0}", BufferPoolBufferSize).AppendLine();
            sb.AppendFormat("       Buffer Pool Max Size: {0}", BufferPoolMaxSize).AppendLine();
            sb.AppendFormat("       Buffer Pool Preallocation Size: {0}", BufferPoolPreallocationSize).AppendLine();
            sb.AppendFormat("       Use Message Batching: {0}", UseMessageBatching).AppendLine();
            sb.AppendFormat("       Max Message Batching Size: {0}", MaxMessageBatchingSize).AppendLine();

            if (isSiloConfig)
            {
                sb.AppendFormat("       Maximum forward count: {0}", MaxForwardCount).AppendLine();
            }

            SerializationProviders.ForEach(sp =>
                sb.AppendFormat("       Serialization provider: {0}", sp.FullName).AppendLine());
            return sb.ToString();
        }

        internal virtual void Load(XmlElement child)
        {
            ResponseTimeout = child.HasAttribute("ResponseTimeout")
                                      ? ConfigUtilities.ParseTimeSpan(child.GetAttribute("ResponseTimeout"),
                                                                 "Invalid ResponseTimeout")
                                      : Constants.DEFAULT_RESPONSE_TIMEOUT;

            if (child.HasAttribute("MaxResendCount"))
            {
                MaxResendCount = ConfigUtilities.ParseInt(child.GetAttribute("MaxResendCount"),
                                                          "Invalid integer value for the MaxResendCount attribute on the Messaging element");
            }
            if (child.HasAttribute("ResendOnTimeout"))
            {
                ResendOnTimeout = ConfigUtilities.ParseBool(child.GetAttribute("ResendOnTimeout"),
                                                          "Invalid Boolean value for the ResendOnTimeout attribute on the Messaging element");
            }
            if (child.HasAttribute("MaxSocketAge"))
            {
                MaxSocketAge = ConfigUtilities.ParseTimeSpan(child.GetAttribute("MaxSocketAge"),
                    "Invalid time span set for the MaxSocketAge attribute on the Messaging element");
            }
            if (child.HasAttribute("DropExpiredMessages"))
            {
                DropExpiredMessages = ConfigUtilities.ParseBool(child.GetAttribute("DropExpiredMessages"),
                                                          "Invalid integer value for the DropExpiredMessages attribute on the Messaging element");
            }
            //--
            if (isSiloConfig)
            {
                if (child.HasAttribute("SiloSenderQueues"))
                {
                    SiloSenderQueues = ConfigUtilities.ParseInt(child.GetAttribute("SiloSenderQueues"),
                                                            "Invalid integer value for the SiloSenderQueues attribute on the Messaging element");
                }
                if (child.HasAttribute("GatewaySenderQueues"))
                {
                    GatewaySenderQueues = ConfigUtilities.ParseInt(child.GetAttribute("GatewaySenderQueues"),
                                                            "Invalid integer value for the GatewaySenderQueues attribute on the Messaging element");
                }
                ClientDropTimeout = child.HasAttribute("ClientDropTimeout")
                                          ? ConfigUtilities.ParseTimeSpan(child.GetAttribute("ClientDropTimeout"),
                                                                     "Invalid ClientDropTimeout")
                                          : Constants.DEFAULT_CLIENT_DROP_TIMEOUT;
            }
            else
            {
                if (child.HasAttribute("ClientSenderBuckets"))
                {
                    ClientSenderBuckets = ConfigUtilities.ParseInt(child.GetAttribute("ClientSenderBuckets"),
                                                            "Invalid integer value for the ClientSenderBuckets attribute on the Messaging element");
                }
            }
            
            //--
            if (child.HasAttribute("BufferPoolBufferSize"))
            {
                BufferPoolBufferSize = ConfigUtilities.ParseInt(child.GetAttribute("BufferPoolBufferSize"),
                                                          "Invalid integer value for the BufferPoolBufferSize attribute on the Messaging element");
            }
            if (child.HasAttribute("BufferPoolMaxSize"))
            {
                BufferPoolMaxSize = ConfigUtilities.ParseInt(child.GetAttribute("BufferPoolMaxSize"),
                                                          "Invalid integer value for the BufferPoolMaxSize attribute on the Messaging element");
            }
            if (child.HasAttribute("BufferPoolPreallocationSize"))
            {
                BufferPoolPreallocationSize = ConfigUtilities.ParseInt(child.GetAttribute("BufferPoolPreallocationSize"),
                                                          "Invalid integer value for the BufferPoolPreallocationSize attribute on the Messaging element");
            }
            if (child.HasAttribute("UseMessageBatching"))
            {
                UseMessageBatching = ConfigUtilities.ParseBool(child.GetAttribute("UseMessageBatching"),
                                                          "Invalid boolean value for the UseMessageBatching attribute on the Messaging element");
            }
            if (child.HasAttribute("MaxMessageBatchingSize"))
            {
                MaxMessageBatchingSize = ConfigUtilities.ParseInt(child.GetAttribute("MaxMessageBatchingSize"),
                                                          "Invalid integer value for the MaxMessageBatchingSize attribute on the Messaging element");
            }
            //--
            if (isSiloConfig)
            {
                if (child.HasAttribute("MaxForwardCount"))
                {
                    MaxForwardCount = ConfigUtilities.ParseInt(child.GetAttribute("MaxForwardCount"),
                                                              "Invalid integer value for the MaxForwardCount attribute on the Messaging element");
                }
            }

            if (child.HasChildNodes)
            {
                var serializerNode = child.ChildNodes.OfType<XmlElement>().FirstOrDefault(n => n.Name == "SerializationProviders");
                if (serializerNode != null && serializerNode.HasChildNodes)
                {
                    var typeNames = serializerNode.ChildNodes.OfType<XmlElement>()
                        .Where(n => n.Name == "Provider")
                        .Select(e => e.Attributes["type"])
                        .Where(a => a != null)
                        .Select(a => a.Value);
                    var types = typeNames.Select(t => ConfigUtilities.ParseFullyQualifiedType(t, "The type specification for the 'type' attribute of the Provider element could not be loaded"));
                    foreach (var type in types)
                    {
                        var typeinfo = type.GetTypeInfo();
                        ConfigUtilities.ValidateSerializationProvider(typeinfo);
                        if (SerializationProviders.Contains(typeinfo) == false)
                        {
                            SerializationProviders.Add(typeinfo);
                        }
                    }
                }
            }
        }
    }
}
