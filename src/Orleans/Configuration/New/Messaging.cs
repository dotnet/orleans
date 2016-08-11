using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Xml;
using Orleans.Runtime;
using Orleans.Runtime.Configuration;

namespace Orleans.Runtime.Configuration.New
{
    /// <summary>
    /// Messaging configuration that are common to client and silo.
    /// </summary>
    [Serializable]
    public class Messaging
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
        public bool UseStandardSerializer { get; set; }
        public bool UseJsonFallbackSerializer { get; set; }

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

        public List<string> SerializationProviders { get; set; } = new List<string>();
        internal double RejectionInjectionRate { get; set; }
        internal double MessageLossInjectionRate { get; set; }

        private static readonly TimeSpan DEFAULT_MAX_SOCKET_AGE = TimeSpan.MaxValue;
        internal const int DEFAULT_MAX_FORWARD_COUNT = 2;
        private const bool DEFAULT_RESEND_ON_TIMEOUT = false;
        private const bool DEFAULT_USE_STANDARD_SERIALIZER = false;
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

        public Messaging()
        {
            //TODO: fix
            isSiloConfig = false;

            ResponseTimeout = Constants.DEFAULT_RESPONSE_TIMEOUT;
            MaxResendCount = 0;
            ResendOnTimeout = DEFAULT_RESEND_ON_TIMEOUT;
            MaxSocketAge = DEFAULT_MAX_SOCKET_AGE;
            DropExpiredMessages = DEFAULT_DROP_EXPIRED_MESSAGES;

            SiloSenderQueues = DEFAULT_SILO_SENDER_QUEUES;
            GatewaySenderQueues = DEFAULT_GATEWAY_SENDER_QUEUES;
            ClientSenderBuckets = DEFAULT_CLIENT_SENDER_BUCKETS;
            ClientDropTimeout = Constants.DEFAULT_CLIENT_DROP_TIMEOUT;
            UseStandardSerializer = DEFAULT_USE_STANDARD_SERIALIZER;

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
            sb.AppendFormat("       Use standard (.NET) serializer: {0}", UseStandardSerializer)
                .AppendLine(isSiloConfig ? "" : "   [NOTE: This *MUST* match the setting on the server or nothing will work!]");
            sb.AppendFormat("       Use fallback json serializer: {0}", UseJsonFallbackSerializer)
                .AppendLine(isSiloConfig ? "" : "   [NOTE: This *MUST* match the setting on the server or nothing will work!]");
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
                sb.AppendFormat("       Serialization provider: {0}", sp).AppendLine());
            return sb.ToString();
        }
    }
}
