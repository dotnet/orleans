using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Orleans.CodeGeneration;
using Orleans.Runtime.Configuration;
using Orleans.Serialization;

namespace Orleans.Runtime
{
    internal class Message : IOutgoingMessage
    {
        // NOTE:  These are encoded on the wire as bytes for efficiency.  They are only integer enums to avoid boxing
        // This means we can't have over byte.MaxValue of them.
        public enum Header
        {
            ALWAYS_INTERLEAVE = 1,
            CACHE_INVALIDATION_HEADER,
            CATEGORY,
            CORRELATION_ID,
            DEBUG_CONTEXT,
            DIRECTION,
            EXPIRATION,
            FORWARD_COUNT,
            INTERFACE_ID,  // DEPRECATED - leave that enum value to maintain next enum numerical values
            METHOD_ID,  // DEPRECATED - leave that enum value to maintain next enum numerical values
            NEW_GRAIN_TYPE,
            GENERIC_GRAIN_TYPE,
            RESULT,
            REJECTION_INFO,
            REJECTION_TYPE,
            READ_ONLY,
            RESEND_COUNT,
            SENDING_ACTIVATION,
            SENDING_GRAIN,
            SENDING_SILO,
            IS_NEW_PLACEMENT,

            TARGET_ACTIVATION,
            TARGET_GRAIN,
            TARGET_SILO,
            TARGET_OBSERVER,
            TIMESTAMPS, // DEPRECATED - leave that enum value to maintain next enum numerical values
            IS_UNORDERED,

            PRIOR_MESSAGE_ID,
            PRIOR_MESSAGE_TIMES,

            REQUEST_CONTEXT,
            // Do not add over byte.MaxValue of these.
        }

        public static class Metadata
        {
            public const string MAX_RETRIES = "MaxRetries";
            public const string EXCLUDE_TARGET_ACTIVATIONS = "#XA";
            public const string TARGET_HISTORY = "TargetHistory";
            public const string ACTIVATION_DATA = "ActivationData";
        }


        public static int LargeMessageSizeThreshold { get; set; }
        public const int LENGTH_HEADER_SIZE = 8;
        public const int LENGTH_META_HEADER = 4;

        private readonly Dictionary<Header, object> headers;
        [NonSerialized]
        private Dictionary<string, object> metadata;

        /// <summary>
        /// NOTE: The contents of bodyBytes should never be modified
        /// </summary>
        private List<ArraySegment<byte>> bodyBytes;

        private List<ArraySegment<byte>> headerBytes;

        private object bodyObject;

        // Cache values of TargetAddess and SendingAddress as they are used very frequently
        private ActivationAddress targetAddress;
        private ActivationAddress sendingAddress;
        private static readonly TraceLogger logger;
        
        static Message()
        {
            logger = TraceLogger.GetLogger("Message", TraceLogger.LoggerType.Runtime);
        }

        public enum Categories
        {
            Ping,
            System,
            Application,
        }

        public enum Directions
        {
            Request,
            Response,
            OneWay
        }

        public enum ResponseTypes
        {
            Success,
            Error,
            Rejection
        }

        public enum RejectionTypes
        {
            Transient,
            Overloaded,
            DuplicateRequest,
            Unrecoverable,
            GatewayTooBusy,
        }

        public Categories Category
        {
            get { return GetScalarHeader<Categories>(Header.CATEGORY); }
            set { SetHeader(Header.CATEGORY, value); }
        }

        public Directions Direction
        {
            get { return GetScalarHeader<Directions>(Header.DIRECTION); }
            set { SetHeader(Header.DIRECTION, value); }
        }

        public bool IsReadOnly
        {
            get { return GetScalarHeader<bool>(Header.READ_ONLY); }
            set { SetHeader(Header.READ_ONLY, value); }
        }

        public bool IsAlwaysInterleave
        {
            get { return GetScalarHeader<bool>(Header.ALWAYS_INTERLEAVE); }
            set { SetHeader(Header.ALWAYS_INTERLEAVE, value); }
        }

        public bool IsUnordered
        {
            get { return GetScalarHeader<bool>(Header.IS_UNORDERED); }
            set
            {
                if (value || ContainsHeader(Header.IS_UNORDERED))
                    SetHeader(Header.IS_UNORDERED, value);
            }
        }

        public CorrelationId Id
        {
            get { return GetSimpleHeader<CorrelationId>(Header.CORRELATION_ID); }
            set { SetHeader(Header.CORRELATION_ID, value); }
        }

        public int ResendCount
        {
            get { return GetScalarHeader<int>(Header.RESEND_COUNT); }
            set { SetHeader(Header.RESEND_COUNT, value); }
        }

        public int ForwardCount
        {
            get { return GetScalarHeader<int>(Header.FORWARD_COUNT); }
            set { SetHeader(Header.FORWARD_COUNT, value); }
        }

        public SiloAddress TargetSilo
        {
            get { return (SiloAddress)GetHeader(Header.TARGET_SILO); }
            set
            {
                SetHeader(Header.TARGET_SILO, value);
                targetAddress = null;
            }
        }

        public GrainId TargetGrain
        {
            get { return GetSimpleHeader<GrainId>(Header.TARGET_GRAIN); }
            set
            {
                SetHeader(Header.TARGET_GRAIN, value);
                targetAddress = null;
            }
        }

        public ActivationId TargetActivation
        {
            get { return GetSimpleHeader<ActivationId>(Header.TARGET_ACTIVATION); }
            set
            {
                SetHeader(Header.TARGET_ACTIVATION, value);
                targetAddress = null;
            }
        }

        public ActivationAddress TargetAddress
        {
            get { return targetAddress ?? (targetAddress = ActivationAddress.GetAddress(TargetSilo, TargetGrain, TargetActivation)); }
            set
            {
                TargetGrain = value.Grain;
                TargetActivation = value.Activation;
                TargetSilo = value.Silo;
                targetAddress = value;
            }
        }

        public GuidId TargetObserverId
        {
            get { return GetSimpleHeader<GuidId>(Header.TARGET_OBSERVER); }
            set
            {
                SetHeader(Header.TARGET_OBSERVER, value);
                targetAddress = null;
            }
        }

        public SiloAddress SendingSilo
        {
            get { return (SiloAddress)GetHeader(Header.SENDING_SILO); }
            set
            {
                SetHeader(Header.SENDING_SILO, value);
                sendingAddress = null;
            }
        }

        public GrainId SendingGrain
        {
            get { return GetSimpleHeader<GrainId>(Header.SENDING_GRAIN); }
            set
            {
                SetHeader(Header.SENDING_GRAIN, value);
                sendingAddress = null;
            }
        }

        public ActivationId SendingActivation
        {
            get { return GetSimpleHeader<ActivationId>(Header.SENDING_ACTIVATION); }
            set
            {
                SetHeader(Header.SENDING_ACTIVATION, value);
                sendingAddress = null;
            }
        }

        public ActivationAddress SendingAddress
        {
            get { return sendingAddress ?? (sendingAddress = ActivationAddress.GetAddress(SendingSilo, SendingGrain, SendingActivation)); }
            set
            {
                SendingGrain = value.Grain;
                SendingActivation = value.Activation;
                SendingSilo = value.Silo;
                sendingAddress = value;
            }
        }

        public bool IsNewPlacement
        {
            get { return GetScalarHeader<bool>(Header.IS_NEW_PLACEMENT); }
            set
            {
                if (value || ContainsHeader(Header.IS_NEW_PLACEMENT))
                    SetHeader(Header.IS_NEW_PLACEMENT, value);
            }
        }

        public ResponseTypes Result
        {
            get { return GetScalarHeader<ResponseTypes>(Header.RESULT); }
            set { SetHeader(Header.RESULT, value); }
        }

        public DateTime Expiration
        {
            get { return GetScalarHeader<DateTime>(Header.EXPIRATION); }
            set { SetHeader(Header.EXPIRATION, value); }
        }

        public bool IsExpired
        {
            get { return (ContainsHeader(Header.EXPIRATION)) && DateTime.UtcNow > Expiration; }
        }

        public bool IsExpirableMessage(IMessagingConfiguration config)
        {
            if (!config.DropExpiredMessages) return false;

            GrainId id = TargetGrain;
            if (id == null) return false;

            // don't set expiration for one way, system target and system grain messages.
            return Direction != Directions.OneWay && !id.IsSystemTarget && !Constants.IsSystemGrain(id);
        }
        
        public string DebugContext
        {
            get { return GetStringHeader(Header.DEBUG_CONTEXT); }
            set { SetHeader(Header.DEBUG_CONTEXT, value); }
        }

        public IEnumerable<ActivationAddress> CacheInvalidationHeader
        {
            get
            {
                object obj = GetHeader(Header.CACHE_INVALIDATION_HEADER);
                return obj == null ? null : ((IEnumerable)obj).Cast<ActivationAddress>();
            }
        }

        internal void AddToCacheInvalidationHeader(ActivationAddress address)
        {
            var list = new List<ActivationAddress>();
            if (ContainsHeader(Header.CACHE_INVALIDATION_HEADER))
            {
                var prevList = ((IEnumerable)GetHeader(Header.CACHE_INVALIDATION_HEADER)).Cast<ActivationAddress>();
                list.AddRange(prevList);
            }
            list.Add(address);
            SetHeader(Header.CACHE_INVALIDATION_HEADER, list);
        }

        // Resends are used by the sender, usualy due to en error to send or due to a transient rejection.
        public bool MayResend(IMessagingConfiguration config)
        {
            return ResendCount < config.MaxResendCount;
        }

        // Forwardings are used by the receiver, usualy when it cannot process the message and forwars it to another silo to perform the processing
        // (got here due to outdated cache, silo is shutting down/overloaded, ...).
        public bool MayForward(GlobalConfiguration config)
        {
            return ForwardCount < config.MaxForwardCount;
        }

        /// <summary>
        /// Set by sender's placement logic when NewPlacementRequested is true
        /// so that receiver knows desired grain type
        /// </summary>
        public string NewGrainType
        {
            get { return GetStringHeader(Header.NEW_GRAIN_TYPE); }
            set { SetHeader(Header.NEW_GRAIN_TYPE, value); }
        }

        /// <summary>
        /// Set by caller's grain reference 
        /// </summary>
        public string GenericGrainType
        {
            get { return GetStringHeader(Header.GENERIC_GRAIN_TYPE); }
            set { SetHeader(Header.GENERIC_GRAIN_TYPE, value); }
        }

        public RejectionTypes RejectionType
        {
            get { return GetScalarHeader<RejectionTypes>(Header.REJECTION_TYPE); }
            set { SetHeader(Header.REJECTION_TYPE, value); }
        }

        public string RejectionInfo
        {
            get { return GetStringHeader(Header.REJECTION_INFO); }
            set { SetHeader(Header.REJECTION_INFO, value); }
        }

        public Dictionary<string, object> RequestContextData
        {
            get { return GetScalarHeader<Dictionary<string, object>>(Header.REQUEST_CONTEXT); }
            set { SetHeader(Header.REQUEST_CONTEXT, value); }
        }

        public object BodyObject
        {
            get
            {
                if (bodyObject != null)
                {
                    return bodyObject;
                }
                try
                {
                    bodyObject = DeserializeBody(bodyBytes);
                }
                finally
                {
                    if (bodyBytes != null)
                    {
                        BufferPool.GlobalPool.Release(bodyBytes);
                        bodyBytes = null;
                    }
                }
                return bodyObject;
            }
            set
            {
                bodyObject = value;
                if (bodyBytes == null) return;

                BufferPool.GlobalPool.Release(bodyBytes);
                bodyBytes = null;
            }
        }

        private static object DeserializeBody(List<ArraySegment<byte>> bytes)
        {
            if (bytes == null)
            {
                return null;
            }
            try
            {
                var stream = new BinaryTokenStreamReader(bytes);
                return SerializationManager.Deserialize(stream);
            }
            catch (Exception ex)
            {
                logger.Error(ErrorCode.Messaging_UnableToDeserializeBody, "Exception deserializing message body", ex);
                throw;
            }
        }

        public Message()
        {
            // average headers items count is 14 items, and while the Header enum contains 18 entries
            // the closest prime number is 17; assuming that possibility of all 18 headers being at the same time is low enough to
            // choose 17 in order to avoid allocations of two additional items on each call, and allocate 37 instead of 19 in rare cases
            headers = new Dictionary<Header, object>(17);
            metadata = new Dictionary<string, object>();
            bodyObject = null;
            bodyBytes = null;
            headerBytes = null;
        }

        private Message(Categories type, Directions subtype)
            : this()
        {
            Category = type;
            Direction = subtype;
        }

        internal static Message CreateMessage(InvokeMethodRequest request, InvokeMethodOptions options)
        {
            var message = new Message(
                Categories.Application,
                (options & InvokeMethodOptions.OneWay) != 0 ? Directions.OneWay : Directions.Request)
            {
                Id = CorrelationId.GetNext(),
                IsReadOnly = (options & InvokeMethodOptions.ReadOnly) != 0,
                IsUnordered = (options & InvokeMethodOptions.Unordered) != 0,
                BodyObject = request
            };

            if ((options & InvokeMethodOptions.AlwaysInterleave) != 0)
                message.IsAlwaysInterleave = true;

            var contextData = RequestContext.Export();
            if (contextData != null)
            {
                message.RequestContextData = contextData;
            }
            return message;
        }

        // Initializes body and header but does not take ownership of byte.
        // Caller must clean up bytes
        public Message(List<ArraySegment<byte>> header, List<ArraySegment<byte>> body, bool deserializeBody = false)
        {
            metadata = new Dictionary<string, object>();

            var input = new BinaryTokenStreamReader(header);
            headers = SerializationManager.DeserializeMessageHeaders(input);
            if (deserializeBody)
            {
                bodyObject = DeserializeBody(body);
            }
            else
            {
                bodyBytes = body;
            }
        }

        public Message CreateResponseMessage()
        {
            var response = new Message(this.Category, Directions.Response)
            {
                Id = this.Id,
                IsReadOnly = this.IsReadOnly,
                IsAlwaysInterleave = this.IsAlwaysInterleave,
                TargetSilo = this.SendingSilo
            };

            if (this.ContainsHeader(Header.SENDING_GRAIN))
            {
                response.SetHeader(Header.TARGET_GRAIN, this.GetHeader(Header.SENDING_GRAIN));
                if (this.ContainsHeader(Header.SENDING_ACTIVATION))
                {
                    response.SetHeader(Header.TARGET_ACTIVATION, this.GetHeader(Header.SENDING_ACTIVATION));
                }
            }

            response.SendingSilo = this.TargetSilo;
            if (this.ContainsHeader(Header.TARGET_GRAIN))
            {
                response.SetHeader(Header.SENDING_GRAIN, this.GetHeader(Header.TARGET_GRAIN));
                if (this.ContainsHeader(Header.TARGET_ACTIVATION))
                {
                    response.SetHeader(Header.SENDING_ACTIVATION, this.GetHeader(Header.TARGET_ACTIVATION));
                }
                else if (this.TargetGrain.IsSystemTarget)
                {
                    response.SetHeader(Header.SENDING_ACTIVATION, ActivationId.GetSystemActivation(TargetGrain, TargetSilo));
                }
            }

            if (this.ContainsHeader(Header.DEBUG_CONTEXT))
            {
                response.SetHeader(Header.DEBUG_CONTEXT, this.GetHeader(Header.DEBUG_CONTEXT));
            }
            if (this.ContainsHeader(Header.CACHE_INVALIDATION_HEADER))
            {
                response.SetHeader(Header.CACHE_INVALIDATION_HEADER, this.GetHeader(Header.CACHE_INVALIDATION_HEADER));
            }
            if (this.ContainsHeader(Header.EXPIRATION))
            {
                response.SetHeader(Header.EXPIRATION, this.GetHeader(Header.EXPIRATION));
            }

            var contextData = RequestContext.Export();
            if (contextData != null)
            {
                response.RequestContextData = contextData;
            }
            return response;
        }

        public Message CreateRejectionResponse(RejectionTypes type, string info, OrleansException ex = null)
        {
            var response = CreateResponseMessage();
            response.Result = ResponseTypes.Rejection;
            response.RejectionType = type;
            response.RejectionInfo = info;
            response.BodyObject = ex;
            if (logger.IsVerbose) logger.Verbose("Creating {0} rejection with info '{1}' for {2} at:" + Environment.NewLine + "{3}", type, info, this, new System.Diagnostics.StackTrace(true));
            return response;
        }

        public Message CreatePromptExceptionResponse(Exception exception)
        {
            return new Message(Category, Directions.Response)
            {
                Result = ResponseTypes.Error,
                BodyObject = Response.ExceptionResponse(exception)
            };
        }

        public bool ContainsHeader(Header tag)
        {
            return headers.ContainsKey(tag);
        }

        public void RemoveHeader(Header tag)
        {
            lock (headers)
            {
                headers.Remove(tag);
                if (tag == Header.TARGET_ACTIVATION || tag == Header.TARGET_GRAIN | tag == Header.TARGET_SILO)
                    targetAddress = null;
            }
        }

        public void SetHeader(Header tag, object value)
        {
            lock (headers)
            {
                headers[tag] = value;
            }
        }

        public object GetHeader(Header tag)
        {
            object val;
            bool flag;
            lock (headers)
            {
                flag = headers.TryGetValue(tag, out val);
            }
            return flag ? val : null;
        }

        public string GetStringHeader(Header tag)
        {
            object val;
            if (!headers.TryGetValue(tag, out val)) return String.Empty;

            var s = val as string;
            return s ?? String.Empty;
        }

        public T GetScalarHeader<T>(Header tag)
        {
            object val;
            if (headers.TryGetValue(tag, out val))
            {
                return (T)val;
            }
            return default(T);
        }

        public T GetSimpleHeader<T>(Header tag)
        {
            object val;
            if (!headers.TryGetValue(tag, out val) || val == null) return default(T);

            return val is T ? (T) val : default(T);
        }

        public bool ContainsMetadata(string tag)
        {
            return metadata != null && metadata.ContainsKey(tag);
        }

        public void SetMetadata(string tag, object data)
        {
            metadata = metadata ?? new Dictionary<string, object>();
            metadata[tag] = data;
        }

        public void RemoveMetadata(string tag)
        {
            if (metadata != null)
            {
                metadata.Remove(tag);
            }
        }

        public object GetMetadata(string tag)
        {
            object data;
            if (metadata != null && metadata.TryGetValue(tag, out data))
            {
                return data;
            }
            return null;
        }

        /// <summary>
        /// Tell whether two messages are duplicates of one another
        /// </summary>
        /// <param name="other"></param>
        /// <returns></returns>
        public bool IsDuplicate(Message other)
        {
            return Equals(SendingSilo, other.SendingSilo) && Equals(Id, other.Id);
        }

        #region Serialization

        public List<ArraySegment<byte>> Serialize(out int headerLength)
        {
            int dummy;
            return Serialize_Impl(out headerLength, out dummy);
        }

        private List<ArraySegment<byte>> Serialize_Impl(out int headerLengthOut, out int bodyLengthOut)
        {
            var headerStream = new BinaryTokenStreamWriter();
            lock (headers) // Guard against any attempts to modify message headers while we are serializing them
            {
                SerializationManager.SerializeMessageHeaders(headers, headerStream);
            }

            if (bodyBytes == null)
            {
                var bodyStream = new BinaryTokenStreamWriter();
                SerializationManager.Serialize(bodyObject, bodyStream);
                // We don't bother to turn this into a byte array and save it in bodyBytes because Serialize only gets called on a message
                // being sent off-box. In this case, the likelihood of needed to re-serialize is very low, and the cost of capturing the
                // serialized bytes from the steam -- where they're a list of ArraySegment objects -- into an array of bytes is actually
                // pretty high (an array allocation plus a bunch of copying).
                bodyBytes = bodyStream.ToBytes() as List<ArraySegment<byte>>;
            }

            if (headerBytes != null)
            {
                BufferPool.GlobalPool.Release(headerBytes);
            }
            headerBytes = headerStream.ToBytes() as List<ArraySegment<byte>>;
            int headerLength = headerBytes.Sum(ab => ab.Count);
            int bodyLength = bodyBytes.Sum(ab => ab.Count);

            var bytes = new List<ArraySegment<byte>>();
            bytes.Add(new ArraySegment<byte>(BitConverter.GetBytes(headerLength)));
            bytes.Add(new ArraySegment<byte>(BitConverter.GetBytes(bodyLength)));
           
            bytes.AddRange(headerBytes);
            bytes.AddRange(bodyBytes);

            if (headerLength + bodyLength > LargeMessageSizeThreshold)
            {
                logger.Info(ErrorCode.Messaging_LargeMsg_Outgoing, "Preparing to send large message Size={0} HeaderLength={1} BodyLength={2} #ArraySegments={3}. Msg={4}",
                    headerLength + bodyLength + LENGTH_HEADER_SIZE, headerLength, bodyLength, bytes.Count, this.ToString());
                if (logger.IsVerbose3) logger.Verbose3("Sending large message {0}", this.ToLongString());
            }

            headerLengthOut = headerLength;
            bodyLengthOut = bodyLength;
            return bytes;
        }


        public void ReleaseBodyAndHeaderBuffers()
        {
            ReleaseHeadersOnly();
            ReleaseBodyOnly();
        }

        public void ReleaseHeadersOnly()
        {
            if (headerBytes == null) return;

            BufferPool.GlobalPool.Release(headerBytes);
            headerBytes = null;
        }

        public void ReleaseBodyOnly()
        {
            if (bodyBytes == null) return;

            BufferPool.GlobalPool.Release(bodyBytes);
            bodyBytes = null;
        }

        #endregion

        // For testing and logging/tracing
        public string ToLongString()
        {
            var sb = new StringBuilder();

            string debugContex = DebugContext;
            if (!string.IsNullOrEmpty(debugContex))
            {
                // if DebugContex is present, print it first.
                sb.Append(debugContex).Append(".");
            }

            lock (headers)
            {
                foreach (var pair in headers)
                {
                    if (pair.Key != Header.DEBUG_CONTEXT)
                    {
                        sb.AppendFormat("{0}={1};", pair.Key, pair.Value);
                    }
                    sb.AppendLine();
                }
            }
            return sb.ToString();
        }

        public override string ToString()
        {
            string response = String.Empty;
            if (Direction == Directions.Response)
            {
                switch (Result)
                {
                    case ResponseTypes.Error:
                        response = "Error ";
                        break;

                    case ResponseTypes.Rejection:
                        response = string.Format("{0} Rejection (info: {1}) ", RejectionType, RejectionInfo);
                        break;

                    default:
                        break;
                }
            }
            return String.Format("{0}{1}{2}{3}{4} {5}->{6} #{7}{8}{9}: {10}",
                IsReadOnly ? "ReadOnly " : "", //0
                IsAlwaysInterleave ? "IsAlwaysInterleave " : "", //1
                IsNewPlacement ? "NewPlacement " : "", // 2
                response,  //3
                Direction, //4
                String.Format("{0}{1}{2}", SendingSilo, SendingGrain, SendingActivation), //5
                String.Format("{0}{1}{2}{3}", TargetSilo, TargetGrain, TargetActivation, TargetObserverId), //6
                Id, //7
                ResendCount > 0 ? "[ResendCount=" + ResendCount + "]" : "", //8
                ForwardCount > 0 ? "[ForwardCount=" + ForwardCount + "]" : "", //9
                DebugContext); //10
        }

        internal void SetTargetPlacement(PlacementResult value)
        {
            if ((value.IsNewPlacement ||
                     (ContainsHeader(Header.TARGET_ACTIVATION) &&
                     !TargetActivation.Equals(value.Activation))))
            {
                RemoveHeader(Header.PRIOR_MESSAGE_ID);
                RemoveHeader(Header.PRIOR_MESSAGE_TIMES);
            }
            TargetActivation = value.Activation;
            TargetSilo = value.Silo;

            if (value.IsNewPlacement)
                IsNewPlacement = true;

            if (!String.IsNullOrEmpty(value.GrainType))
                NewGrainType = value.GrainType;
        }


        public string GetTargetHistory()
        {
            var history = new StringBuilder();
            history.Append("<");
            if (ContainsHeader(Header.TARGET_SILO))
            {
                history.Append(TargetSilo).Append(":");
            }
            if (ContainsHeader(Header.TARGET_GRAIN))
            {
                history.Append(TargetGrain).Append(":");
            }
            if (ContainsHeader(Header.TARGET_ACTIVATION))
            {
                history.Append(TargetActivation);
            }
            history.Append(">");
            if (ContainsMetadata(Message.Metadata.TARGET_HISTORY))
            {
                history.Append("    ").Append(GetMetadata(Message.Metadata.TARGET_HISTORY));
            }
            return history.ToString();
        }

        public bool IsSameDestination(IOutgoingMessage other)
        {
            var msg = (Message)other;
            return msg != null && Object.Equals(TargetSilo, msg.TargetSilo);
        }

        // For statistical measuring of time spent in queues.
        private ITimeInterval timeInterval;

        public void Start()
        {
            timeInterval = TimeIntervalFactory.CreateTimeInterval(true);
            timeInterval.Start();
        }

        public void Stop()
        {
            timeInterval.Stop();
        }

        public void Restart()
        {
            timeInterval.Restart();
        }

        public TimeSpan Elapsed
        {
            get { return timeInterval.Elapsed; }
        }


        internal void DropExpiredMessage(MessagingStatisticsGroup.Phase phase)
        {
            MessagingStatisticsGroup.OnMessageExpired(phase);
            if (logger.IsVerbose2) logger.Verbose2("Dropping an expired message: {0}", this);
            ReleaseBodyAndHeaderBuffers();
        }
    }
}
