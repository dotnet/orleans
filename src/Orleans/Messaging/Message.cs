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
        public static int LargeMessageSizeThreshold { get; set; }
        public const int LENGTH_HEADER_SIZE = 8;
        public const int LENGTH_META_HEADER = 4;

        #region metadata

        [NonSerialized]
        private string _targetHistory;

        [NonSerialized]
        private DateTime? _queuedTime;

        [NonSerialized]
        private int? _retryCount;

        [NonSerialized]
        private int? _maxRetries;

        public string TargetHistory
        {
            get { return _targetHistory; }
            set { _targetHistory = value; }
        }

        
        public DateTime? QueuedTime
        {
            get { return _queuedTime; }
            set { _queuedTime = value; }
        }

        public int? RetryCount
        {
            get { return _retryCount; }
            set { _retryCount = value; }
        }

        public int? MaxRetries
        {
            get { return _maxRetries; }
            set { _maxRetries = value; }
        }

        #endregion

        /// <summary>
        /// NOTE: The contents of bodyBytes should never be modified
        /// </summary>
        private List<ArraySegment<byte>> bodyBytes;

        private List<ArraySegment<byte>> headerBytes;

        private object bodyObject;

        // Cache values of TargetAddess and SendingAddress as they are used very frequently
        private ActivationAddress targetAddress;
        private ActivationAddress sendingAddress;
        private static readonly Logger logger;
        
        static Message()
        {
            logger = LogManager.GetLogger("Message", LoggerType.Runtime);
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

        internal HeadersContainer Headers { get; set; } = new HeadersContainer();

        public Categories Category
        {
            get { lock (Headers) return Headers.Category; }
            set { lock (Headers) Headers.Category = value; }
        }

        public Directions Direction
        {
            get { lock (Headers) return Headers.Direction ?? default(Directions); }
            set { lock (Headers) Headers.Direction = value; }
        }

        public bool HasDirection => Headers.Direction.HasValue;

        public bool IsReadOnly
        {
            get { lock(Headers) return Headers.IsReadOnly; }
            set { lock (Headers) Headers.IsReadOnly = value; }
        }

        public bool IsAlwaysInterleave
        {
            get { lock (Headers) return Headers.IsAlwaysInterleave; }
            set { lock (Headers) Headers.IsAlwaysInterleave = value; }
        }

        public bool IsUnordered
        {
            get { lock (Headers) return Headers.IsUnordered; }
            set { lock (Headers) Headers.IsUnordered = value; }
        }

        public CorrelationId Id
        {
            get { lock (Headers) return Headers.Id; }
            set { lock (Headers) Headers.Id = value; }
        }

        public int ResendCount
        {
            get { lock (Headers) return Headers.ResendCount; }
            set { lock (Headers) Headers.ResendCount = value; }
        }

        public int ForwardCount
        {
            get { lock (Headers) return Headers.ForwardCount; }
            set { lock (Headers) Headers.ForwardCount = value; }
        }
        
        public SiloAddress TargetSilo
        {
            get { lock (Headers) return Headers.TargetSilo; }
            set
            {
                lock (Headers) Headers.TargetSilo = value;
                targetAddress = null;
            }
        }
        
        public GrainId TargetGrain
        {
            get { lock (Headers) return Headers.TargetGrain; }
            set
            {
                lock (Headers) Headers.TargetGrain = value;
                targetAddress = null;
            }
        }
        
        public ActivationId TargetActivation
        {
            get { lock (Headers) return Headers.TargetActivation; }
            set
            {
                lock (Headers) Headers.TargetActivation = value;
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
            get { lock (Headers) return Headers.TargetObserverId; }
            set
            {
                lock (Headers) Headers.TargetObserverId = value;
                targetAddress = null;
            }
        }
        
        public SiloAddress SendingSilo
        {
            get { lock (Headers) return Headers.SendingSilo; }
            set
            {
                lock (Headers) Headers.SendingSilo = value;
                sendingAddress = null;
            }
        }
        
        public GrainId SendingGrain
        {
            get { lock (Headers) return Headers.SendingGrain; }
            set
            {
                lock (Headers) Headers.SendingGrain = value;
                sendingAddress = null;
            }
        }
        
        public ActivationId SendingActivation
        {
            get { lock (Headers) return Headers.SendingActivation; }
            set
            {
                lock (Headers) Headers.SendingActivation = value;
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
            get { lock (Headers) return Headers.IsNewPlacement; }
            set
            {
                lock (Headers) Headers.IsNewPlacement = value;
            }
        }

        public ResponseTypes Result
        {
            get { lock (Headers) return Headers.Result; }
            set { lock (Headers) Headers.Result = value; }
        }

        public DateTime? Expiration
        {
            get { lock (Headers) return Headers.Expiration; }
            set { lock (Headers) Headers.Expiration = value; }
        }

        public bool IsExpired => Expiration.HasValue && DateTime.UtcNow > Expiration.Value;

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
            get { lock (Headers) return GetNotNullString(Headers.DebugContext); }
            set { lock (Headers) Headers.DebugContext = value; }
        }

        public List<ActivationAddress> CacheInvalidationHeader
        {
            get { lock (Headers) return Headers.CacheInvalidationHeader; }
            set { lock (Headers) Headers.CacheInvalidationHeader = value; }
        }

        internal void AddToCacheInvalidationHeader(ActivationAddress address)
        {
            var list = new List<ActivationAddress>();
            if (CacheInvalidationHeader != null)
            {
                list.AddRange(CacheInvalidationHeader);
            }

            list.Add(address);
            CacheInvalidationHeader = list;
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
            get { return GetNotNullString(Headers.NewGrainType); }
            set { Headers.NewGrainType = value; }
        }
        
        /// <summary>
        /// Set by caller's grain reference 
        /// </summary>
        public string GenericGrainType
        {
            get { return GetNotNullString(Headers.GenericGrainType); }
            set { Headers.GenericGrainType = value; }
        }

        public RejectionTypes RejectionType
        {
            get { return Headers.RejectionType; }
            set { Headers.RejectionType = value; }
        }

        public string RejectionInfo
        {
            get { return GetNotNullString(Headers.RejectionInfo); }
            set { Headers.RejectionInfo = value; }
        }

        public Dictionary<string, object> RequestContextData
        {
            get { return Headers.RequestContextData; }
            set { Headers.RequestContextData = value; }
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
            var input = new BinaryTokenStreamReader(header);
            Headers = SerializationManager.DeserializeMessageHeaders(input);
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

            if (SendingGrain != null)
            {
                response.TargetGrain = SendingGrain;
                if (SendingActivation != null)
                {
                    response.TargetActivation = SendingActivation;
                }
            }

            response.SendingSilo = this.TargetSilo;
            if (TargetGrain != null)
            {
                response.SendingGrain = TargetGrain;
                if (TargetActivation != null)
                {
                    response.SendingActivation = TargetActivation;
                }
                else if (this.TargetGrain.IsSystemTarget)
                {
                    response.SendingActivation = ActivationId.GetSystemActivation(TargetGrain, TargetSilo);
                }
            }

            if (DebugContext != null)
            {
                response.DebugContext = DebugContext;
            }

            response.CacheInvalidationHeader = CacheInvalidationHeader;
            response.Expiration = Expiration;

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
            if (logger.IsVerbose) logger.Verbose("Creating {0} rejection with info '{1}' for {2} at:" + Environment.NewLine + "{3}", type, info, this, Utils.GetStackTrace());
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

        public void ClearTargetAddress()
        {
            targetAddress = null;
        }

        private static string GetNotNullString(string s)
        {
            return s ?? string.Empty;
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
            lock (Headers) // Guard against any attempts to modify message headers while we are serializing them
            {
                SerializationManager.SerializeMessageHeaders(Headers, headerStream);
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

            AppendIfExists(HeadersContainer.Header.CACHE_INVALIDATION_HEADER, sb, (m) => m.CacheInvalidationHeader);

            AppendIfExists(HeadersContainer.Header.CATEGORY, sb, (m) => m.Category);
            AppendIfExists(HeadersContainer.Header.DIRECTION, sb, (m) => m.Direction);
            AppendIfExists(HeadersContainer.Header.EXPIRATION, sb, (m) => m.Expiration);
            AppendIfExists(HeadersContainer.Header.FORWARD_COUNT, sb, (m) => m.ForwardCount);
            AppendIfExists(HeadersContainer.Header.GENERIC_GRAIN_TYPE, sb, (m) => m.GenericGrainType);
            AppendIfExists(HeadersContainer.Header.CORRELATION_ID, sb, (m) => m.Id);
            AppendIfExists(HeadersContainer.Header.ALWAYS_INTERLEAVE, sb, (m) => m.IsAlwaysInterleave);
            AppendIfExists(HeadersContainer.Header.IS_NEW_PLACEMENT, sb, (m) => m.IsNewPlacement);
            AppendIfExists(HeadersContainer.Header.READ_ONLY, sb, (m) => m.IsReadOnly);
            AppendIfExists(HeadersContainer.Header.IS_UNORDERED, sb, (m) => m.IsUnordered);
            AppendIfExists(HeadersContainer.Header.NEW_GRAIN_TYPE, sb, (m) => m.NewGrainType);
            AppendIfExists(HeadersContainer.Header.REJECTION_INFO, sb, (m) => m.RejectionInfo);
            AppendIfExists(HeadersContainer.Header.REJECTION_TYPE, sb, (m) => m.RejectionType);
            AppendIfExists(HeadersContainer.Header.REQUEST_CONTEXT, sb, (m) => m.RequestContextData);
            AppendIfExists(HeadersContainer.Header.RESEND_COUNT, sb, (m) => m.ResendCount);
            AppendIfExists(HeadersContainer.Header.RESULT, sb, (m) => m.Result);
            AppendIfExists(HeadersContainer.Header.SENDING_ACTIVATION, sb, (m) => m.SendingActivation);
            AppendIfExists(HeadersContainer.Header.SENDING_GRAIN, sb, (m) => m.SendingGrain);
            AppendIfExists(HeadersContainer.Header.SENDING_SILO, sb, (m) => m.SendingSilo);
            AppendIfExists(HeadersContainer.Header.TARGET_ACTIVATION, sb, (m) => m.TargetActivation);
            AppendIfExists(HeadersContainer.Header.TARGET_GRAIN, sb, (m) => m.TargetGrain);
            AppendIfExists(HeadersContainer.Header.TARGET_OBSERVER, sb, (m) => m.TargetObserverId);
            AppendIfExists(HeadersContainer.Header.TARGET_SILO, sb, (m) => m.TargetSilo);

            return sb.ToString();
        }
        
        private void AppendIfExists(HeadersContainer.Header header, StringBuilder sb, Func<Message, object> valueProvider)
        {
            if ((Headers.Headers & header) != HeadersContainer.Header.NONE)
            {
                sb.AppendFormat("{0}={1};", header, valueProvider(this));
                sb.AppendLine();
            }
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
            if (TargetSilo != null)
            {
                history.Append(TargetSilo).Append(":");
            }
            if (TargetGrain != null)
            {
                history.Append(TargetGrain).Append(":");
            }
            if (TargetActivation != null)
            {
                history.Append(TargetActivation);
            }
            history.Append(">");
            if (!string.IsNullOrEmpty(TargetHistory))
            {
                history.Append("    ").Append(TargetHistory);
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

        [Serializable]
        public class HeadersContainer
        {
            // NOTE:  These are encoded on the wire as bytes for efficiency.  They are only integer enums to avoid boxing
            // This means we can't have over byte.MaxValue of them.
            [Flags]
            public enum Header
            {
                NONE = 0,
                ALWAYS_INTERLEAVE = 1 << 0,
                CACHE_INVALIDATION_HEADER = 1 << 1,
                CATEGORY = 1 << 2,
                CORRELATION_ID = 1 << 3,
                DEBUG_CONTEXT = 1 << 4,
                DIRECTION = 1 << 5,
                EXPIRATION = 1 << 6,
                FORWARD_COUNT = 1 << 7,
                NEW_GRAIN_TYPE = 1 << 8,
                GENERIC_GRAIN_TYPE = 1 << 9,
                RESULT = 1 << 10,
                REJECTION_INFO = 1 << 11,
                REJECTION_TYPE = 1 << 12,
                READ_ONLY = 1 << 13,
                RESEND_COUNT = 1 << 14,
                SENDING_ACTIVATION = 1 << 15,
                SENDING_GRAIN = 1 <<16,
                SENDING_SILO = 1 << 17,
                IS_NEW_PLACEMENT = 1 << 18,

                TARGET_ACTIVATION = 1 << 19,
                TARGET_GRAIN = 1 << 20,
                TARGET_SILO = 1 << 21,
                TARGET_OBSERVER = 1 << 22,
                IS_UNORDERED = 1 << 23,
                REQUEST_CONTEXT = 1 << 24,
                // Do not add over int.MaxValue of these.
            }

            private Header headers = default(Header);

            private Categories _category;
            private Directions? _direction;
            private bool _isReadOnly;
            private bool _isAlwaysInterleave;
            private bool _isUnordered;
            private CorrelationId _id;
            private int _resendCount;
            private int _forwardCount;
            private SiloAddress _targetSilo;
            private GrainId _targetGrain;
            private ActivationId _targetActivation;
            private GuidId _targetObserverId;
            private SiloAddress _sendingSilo;
            private GrainId _sendingGrain;
            private ActivationId _sendingActivation;
            private bool _isNewPlacement;
            private ResponseTypes _result;
            private DateTime? _expiration;
            private string _debugContext;
            private List<ActivationAddress> _cacheInvalidationHeader;
            private string _newGrainType;
            private string _genericGrainType;
            private RejectionTypes _rejectionType;
            private string _rejectionInfo;
            private Dictionary<string, object> _requestContextData;

            public Header Headers => headers;

            public Categories Category
            {
                get { return _category; }
                set
                {
                    _category = value;
                    headers = headers | Header.CATEGORY;
                }
            }

            public Directions? Direction
            {
                get { return _direction; }
                set
                {
                    _direction = value;
                    headers = value == null ? headers & ~Header.DIRECTION : headers | Header.DIRECTION;
                }
            }

            public bool IsReadOnly
            {
                get { return _isReadOnly; }
                set
                {
                    _isReadOnly = value;
                    headers = headers | Header.READ_ONLY;
                }
            }

            public bool IsAlwaysInterleave
            {
                get { return _isAlwaysInterleave; }
                set
                {
                    _isAlwaysInterleave = value;
                    headers = headers | Header.ALWAYS_INTERLEAVE;
                }
            }

            public bool IsUnordered
            {
                get { return _isUnordered; }
                set
                {
                    _isUnordered = value;
                    headers = headers | Header.IS_UNORDERED;
                }
            }

            public CorrelationId Id
            {
                get { return _id; }
                set
                {
                    _id = value;
                    headers = value == null ? headers & ~Header.CORRELATION_ID : headers | Header.CORRELATION_ID;
                }
            }

            public int ResendCount
            {
                get { return _resendCount; }
                set
                {
                    _resendCount = value;
                    headers = headers | Header.RESEND_COUNT;
                }
            }

            public int ForwardCount
            {
                get { return _forwardCount; }
                set
                {
                    _forwardCount = value;
                    headers = headers | Header.FORWARD_COUNT;
                }
            }

            public SiloAddress TargetSilo
            {
                get { return _targetSilo; }
                set
                {
                    _targetSilo = value;
                    headers = value == null ? headers & ~Header.TARGET_SILO :  headers | Header.TARGET_SILO;
                }
            }

            public GrainId TargetGrain
            {
                get { return _targetGrain; }
                set
                {
                    _targetGrain = value;
                    headers = value == null ? headers & ~Header.TARGET_GRAIN : headers | Header.TARGET_GRAIN;
                }
            }

            public ActivationId TargetActivation
            {
                get { return _targetActivation; }
                set
                {
                    _targetActivation = value;
                    headers = value == null ? headers & ~Header.TARGET_ACTIVATION : headers | Header.TARGET_ACTIVATION;
                }
            }

            public GuidId TargetObserverId
            {
                get { return _targetObserverId; }
                set
                {
                    _targetObserverId = value;
                    headers = value == null ? headers & ~Header.TARGET_OBSERVER : headers | Header.TARGET_OBSERVER;
                }
            }

            public SiloAddress SendingSilo
            {
                get { return _sendingSilo; }
                set
                {
                    _sendingSilo = value;
                    headers = value == null ? headers & ~Header.SENDING_SILO : headers | Header.SENDING_SILO;
                }
            }

            public GrainId SendingGrain
            {
                get { return _sendingGrain; }
                set
                {
                    _sendingGrain = value;
                    headers = value == null ? headers & ~Header.SENDING_GRAIN : headers | Header.SENDING_GRAIN;
                }
            }

            public ActivationId SendingActivation
            {
                get { return _sendingActivation; }
                set
                {
                    _sendingActivation = value;
                    headers = value == null ? headers & ~Header.SENDING_ACTIVATION : headers | Header.SENDING_ACTIVATION;
                }
            }

            public bool IsNewPlacement
            {
                get { return _isNewPlacement; }
                set
                {
                    _isNewPlacement = value;
                    headers = headers | Header.IS_NEW_PLACEMENT;
                }
            }

            public ResponseTypes Result
            {
                get { return _result; }
                set
                {
                    _result = value;
                    headers = headers | Header.RESULT;
                }
            }

            public DateTime? Expiration
            {
                get { return _expiration; }
                set
                {
                    _expiration = value;
                    headers = value == null ? headers & ~Header.EXPIRATION : headers | Header.EXPIRATION;
                }
            }


            public string DebugContext
            {
                get { return _debugContext; }
                set
                {
                    _debugContext = value;
                    headers = string.IsNullOrEmpty(value) ? headers & ~Header.DEBUG_CONTEXT : headers | Header.DEBUG_CONTEXT;
                }
            }

            public List<ActivationAddress> CacheInvalidationHeader
            {
                get { return _cacheInvalidationHeader; }
                set
                {
                    _cacheInvalidationHeader = value;
                    headers = value == null || value.Count == 0 ? headers & ~Header.CACHE_INVALIDATION_HEADER : headers | Header.CACHE_INVALIDATION_HEADER;
                }
            }

            /// <summary>
            /// Set by sender's placement logic when NewPlacementRequested is true
            /// so that receiver knows desired grain type
            /// </summary>
            public string NewGrainType
            {
                get { return _newGrainType; }
                set
                {
                    _newGrainType = value;
                    headers = string.IsNullOrEmpty(value) ? headers & ~Header.NEW_GRAIN_TYPE : headers | Header.NEW_GRAIN_TYPE;
                }
            }

            /// <summary>
            /// Set by caller's grain reference 
            /// </summary>
            public string GenericGrainType
            {
                get { return _genericGrainType; }
                set
                {
                    _genericGrainType = value;
                    headers = string.IsNullOrEmpty(value) ? headers & ~Header.GENERIC_GRAIN_TYPE : headers | Header.GENERIC_GRAIN_TYPE;
                }
            }

            public RejectionTypes RejectionType
            {
                get { return _rejectionType; }
                set
                {
                    _rejectionType = value;
                    headers = headers | Header.REJECTION_TYPE;
                }
            }

            public string RejectionInfo
            {
                get { return _rejectionInfo; }
                set
                {
                    _rejectionInfo = value;
                    headers = string.IsNullOrEmpty(value) ? headers & ~Header.REJECTION_INFO : headers | Header.REJECTION_INFO;
                }
            }

            public Dictionary<string, object> RequestContextData
            {
                get { return _requestContextData; }
                set
                {
                    _requestContextData = value;
                    headers = value  == null || value.Count == 0 ? headers & ~Header.REQUEST_CONTEXT : headers | Header.REQUEST_CONTEXT;
                }
            }

            static HeadersContainer()
            {
                Register();
            }


            [global::Orleans.CodeGeneration.CopierMethodAttribute]
            public static global::System.Object DeepCopier(global::System.Object original)
            {
                return original;
            }

            [global::Orleans.CodeGeneration.SerializerMethodAttribute]
            public static void Serializer(global::System.Object untypedInput,  BinaryTokenStreamWriter stream, global::System.Type expected)
            {
                HeadersContainer input = (HeadersContainer)untypedInput;
                var headers = input.headers;
                stream.Write((int)input.headers);
              //  SerializationManager.@SerializeInner(input.headers, stream, typeof(Header));
                if ((headers & Header.CACHE_INVALIDATION_HEADER) != Header.NONE)
                {
                    var count = input.@CacheInvalidationHeader.Count;
                    stream.Write(input.@CacheInvalidationHeader.Count);
                    for (int i = 0; i < count; i++)
                    {
                        WriteObj(stream, typeof(ActivationAddress), input.@CacheInvalidationHeader[i]);
                    }
                }

                if ((headers & Header.CATEGORY) != Header.NONE)
                {
                    stream.Write((byte)input.@Category);
                }

                if ((headers & Header.DEBUG_CONTEXT) != Header.NONE)
                    stream.Write(input.DebugContext);

                if ((headers & Header.DIRECTION) != Header.NONE)
                    stream.Write((byte)input.Direction.Value);

                if ((headers & Header.EXPIRATION) != Header.NONE)
                    stream.Write(input.@Expiration.Value);

                if ((headers & Header.FORWARD_COUNT) != Header.NONE)
                    stream.Write(input.@ForwardCount);

                if ((headers & Header.GENERIC_GRAIN_TYPE) != Header.NONE)
                    stream.Write(input.@GenericGrainType);

                if ((headers & Header.CORRELATION_ID) != Header.NONE)
                    stream.Write(input.@Id);

                if ((headers & Header.ALWAYS_INTERLEAVE) != Header.NONE)
                    stream.Write(input.@IsAlwaysInterleave);

                if ((headers & Header.IS_NEW_PLACEMENT) != Header.NONE)
                    stream.Write(input.@IsNewPlacement);

                if ((headers & Header.READ_ONLY) != Header.NONE)
                    stream.Write(input.@IsReadOnly);

                if ((headers & Header.IS_UNORDERED) != Header.NONE)
                    stream.Write(input.@IsUnordered);

                if ((headers & Header.NEW_GRAIN_TYPE) != Header.NONE)
                    stream.Write(input.@NewGrainType);

                if ((headers & Header.REJECTION_INFO) != Header.NONE)
                    stream.Write(input.@RejectionInfo);

                if ((headers & Header.REJECTION_TYPE) != Header.NONE)
                    stream.Write((byte)input.@RejectionType);

                if ((headers & Header.REQUEST_CONTEXT) != Header.NONE)
                {
                    var requestData = input.@RequestContextData;
                    var count = requestData.Count;
                    stream.Write(count);
                    foreach (var d in requestData)
                    {
                        stream.Write(d.Key);
                        SerializationManager.@SerializeInner(d.Value, stream, typeof(object));
                    }
                }

                if ((headers & Header.RESEND_COUNT) != Header.NONE)
                    stream.Write(input.@ResendCount);

                if ((headers & Header.RESULT) != Header.NONE)
                    stream.Write((byte)input.@Result);

                if ((headers & Header.SENDING_ACTIVATION) != Header.NONE)
                {
                    stream.Write(input.@SendingActivation);
                }

                if ((headers & Header.SENDING_GRAIN) != Header.NONE)
                {
                    stream.Write(input.@SendingGrain);
                }

                if ((headers & Header.SENDING_SILO) != Header.NONE)
                {
                    stream.Write(input.@SendingSilo);
                }

                if ((headers & Header.TARGET_ACTIVATION) != Header.NONE)
                {
                    stream.Write(input.@TargetActivation);
                }

                if ((headers & Header.TARGET_GRAIN) != Header.NONE)
                {
                    stream.Write(input.@TargetGrain);
                }

                if ((headers & Header.TARGET_OBSERVER) != Header.NONE)
                {
                    WriteObj(stream, typeof(GuidId), input.@TargetObserverId);
                }

                if ((headers & Header.TARGET_SILO) != Header.NONE)
                {
                    stream.Write(input.@TargetSilo);
                }
            }

            [global::Orleans.CodeGeneration.DeserializerMethodAttribute]
            public static global::System.Object Deserializer(global::System.Type expected,  BinaryTokenStreamReader stream)
            {
                var result = new HeadersContainer();
                global::Orleans.@Serialization.@DeserializationContext.@Current.@RecordObject(result);
                var headers = (Header)stream.ReadInt();

                if ((headers & Header.CACHE_INVALIDATION_HEADER) != Header.NONE)
                {
                    var n = stream.ReadInt();
                    if (n > 0)
                    {
                       var list = result.@CacheInvalidationHeader = new List<ActivationAddress>(n);
                        for (int i = 0; i < n; i++)
                        {
                            list.Add((ActivationAddress)ReadObj(typeof(ActivationAddress), stream));
                        }
                    }
                }

                if ((headers & Header.CATEGORY) != Header.NONE)
                    result.@Category = (Categories)stream.ReadByte();

                if ((headers & Header.DEBUG_CONTEXT) != Header.NONE)
                    result.@DebugContext = stream.ReadString();

                if ((headers & Header.DIRECTION) != Header.NONE)
                    result.@Direction = (Message.Directions)stream.ReadByte();

                if ((headers & Header.EXPIRATION) != Header.NONE)
                    result.@Expiration = stream.ReadDateTime();

                if ((headers & Header.FORWARD_COUNT) != Header.NONE)
                    result.@ForwardCount = stream.ReadInt();

                if ((headers & Header.GENERIC_GRAIN_TYPE) != Header.NONE)
                    result.@GenericGrainType = stream.ReadString();

                if ((headers & Header.CORRELATION_ID) != Header.NONE)
                    result.@Id = (global::Orleans.Runtime.CorrelationId)ReadObj(typeof(global::Orleans.Runtime.CorrelationId), stream);

                if ((headers & Header.ALWAYS_INTERLEAVE) != Header.NONE)
                    result.@IsAlwaysInterleave = ReadBool(stream);

                if ((headers & Header.IS_NEW_PLACEMENT) != Header.NONE)
                    result.@IsNewPlacement = ReadBool(stream);

                if ((headers & Header.READ_ONLY) != Header.NONE)
                    result.@IsReadOnly = ReadBool(stream);

                if ((headers & Header.IS_UNORDERED) != Header.NONE)
                    result.@IsUnordered = ReadBool(stream);

                if ((headers & Header.NEW_GRAIN_TYPE) != Header.NONE)
                    result.@NewGrainType = stream.ReadString();

                if ((headers & Header.REJECTION_INFO) != Header.NONE)
                    result.@RejectionInfo = stream.ReadString();

                if ((headers & Header.REJECTION_TYPE) != Header.NONE)
                    result.@RejectionType = (RejectionTypes)stream.ReadByte();

                if ((headers & Header.REQUEST_CONTEXT) != Header.NONE)
                {
                    var c = stream.ReadInt();
                    var requestData = new Dictionary<string, object>(c);
                    for (int i = 0; i < c; i++)
                    {
                        requestData[stream.ReadString()] = SerializationManager.DeserializeInner(null, stream);
                    }
                    result.@RequestContextData = requestData;
                }

                if ((headers & Header.RESEND_COUNT) != Header.NONE)
                    result.@ResendCount = stream.ReadInt();

                if ((headers & Header.RESULT) != Header.NONE)
                    result.@Result = (global::Orleans.Runtime.Message.ResponseTypes)stream.ReadByte();

                if ((headers & Header.SENDING_ACTIVATION) != Header.NONE)
                    result.@SendingActivation = stream.ReadActivationId();

                if ((headers & Header.SENDING_GRAIN) != Header.NONE)
                    result.@SendingGrain = stream.ReadGrainId();

                if ((headers & Header.SENDING_SILO) != Header.NONE)
                    result.@SendingSilo = stream.ReadSiloAddress();

                if ((headers & Header.TARGET_ACTIVATION) != Header.NONE) 
                    result.@TargetActivation = stream.ReadActivationId();

                if ((headers & Header.TARGET_GRAIN) != Header.NONE)
                    result.@TargetGrain = stream.ReadGrainId();

                if ((headers & Header.TARGET_OBSERVER) != Header.NONE)
                    result.@TargetObserverId = (global::Orleans.Runtime.GuidId)ReadObj(typeof(global::Orleans.Runtime.GuidId), stream);

                if ((headers & Header.TARGET_SILO) != Header.NONE)
                    result.@TargetSilo = stream.ReadSiloAddress();

                return (HeadersContainer)result;
            }

            private static bool ReadBool(BinaryTokenStreamReader stream)
            {
                return stream.ReadByte() == (byte) SerializationTokenType.True;
            }

            private static void WriteObj(BinaryTokenStreamWriter stream, Type type, object input)
            {
                var ser = SerializationManager.GetSerializer(type);
                ser.Invoke(input, stream, type);
            }

            private static object ReadObj(Type t, BinaryTokenStreamReader stream)
            {
                var des = SerializationManager.GetDeserializer(t);
                return des.Invoke(t, stream);
            }

            public static void Register()
            {
                 SerializationManager.@Register(typeof(HeadersContainer), DeepCopier, Serializer, Deserializer);
            }
        }
    }
}
