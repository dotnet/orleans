using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Xml;

namespace Orleans.Runtime.Configuration
{
    /// <summary>
    /// Messaging configuration that are common to client and silo.
    /// </summary>
    [Serializable]
    public class MessagingConfiguration : IMessagingConfiguration
    {
        public TimeSpan OpenConnectionTimeout { get; set; }
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

        /// <summary>
        /// The MaxForwardCount attribute specifies the maximal number of times a message is being forwared from one silo to another.
        /// Forwarding is used internally by the tuntime as a recovery mechanism when silos fail and the membership is unstable.
        /// In such times the messages might not be routed correctly to destination, and runtime attempts to forward such messages a number of times before rejecting them.
        /// </summary>
        public int MaxForwardCount { get; set; }

        public List<TypeInfo> SerializationProviders { get; private set; }
        public TypeInfo FallbackSerializationProvider { get; set; }
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

        private readonly bool isSiloConfig;

        internal MessagingConfiguration(bool isSilo)
        {
            isSiloConfig = isSilo;

            OpenConnectionTimeout = Constants.DEFAULT_OPENCONNECTION_TIMEOUT;
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

            if (isSiloConfig)
            {
                sb.AppendFormat("       Maximum forward count: {0}", MaxForwardCount).AppendLine();
            }

            SerializationProviders.ForEach(sp =>
                sb.AppendFormat("       Serialization provider: {0}", sp.FullName).AppendLine());
            sb.AppendFormat("       Fallback serializer: {0}", this.FallbackSerializationProvider?.FullName).AppendLine();
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
                    var types =
                        typeNames.Select(
                            t =>
                            ConfigUtilities.ParseFullyQualifiedType(
                                t,
                                $"The type specification for the 'type' attribute of the Provider element could not be loaded. Type specification: '{t}'."));
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

                var fallbackSerializerNode = child.ChildNodes.OfType<XmlElement>().FirstOrDefault(n => n.Name == "FallbackSerializationProvider");
                if (fallbackSerializerNode != null)
                {
                    var typeName = fallbackSerializerNode.Attributes["type"]?.Value;
                    if (string.IsNullOrWhiteSpace(typeName))
                    {
                        var msg = "The FallbackSerializationProvider element requires a 'type' attribute specifying the fully-qualified type name of the serializer.";
                        throw new FormatException(msg);
                    }

                    var type = ConfigUtilities.ParseFullyQualifiedType(
                        typeName,
                        $"The type specification for the 'type' attribute of the FallbackSerializationProvider element could not be loaded. Type specification: '{typeName}'.");
                    this.FallbackSerializationProvider = type.GetTypeInfo();
                }
            }
        }
    }
}
