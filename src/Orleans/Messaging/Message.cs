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
            get { return Headers.Category; }
            set { Headers.Category = value; }
        }

        public Directions Direction
        {
            get { return Headers.Direction ?? default(Directions); }
            set { Headers.Direction = value; }
        }

        public bool HasDirection => Headers.Direction.HasValue;

        public bool IsReadOnly
        {
            get { return Headers.IsReadOnly; }
            set { Headers.IsReadOnly = value; }
        }

        public bool IsAlwaysInterleave
        {
            get { return Headers.IsAlwaysInterleave; }
            set { Headers.IsAlwaysInterleave = value; }
        }

        public bool IsUnordered
        {
            get { return Headers.IsUnordered; }
            set { Headers.IsUnordered = value; }
        }

        public bool IsReturnedFromRemoteCluster
        {
            get { return Headers.IsReturnedFromRemoteCluster; }
            set { Headers.IsReturnedFromRemoteCluster = value; }
        }

        public CorrelationId Id
        {
            get { return Headers.Id; }
            set { Headers.Id = value; }
        }

        public int ResendCount
        {
            get { return Headers.ResendCount; }
            set {  Headers.ResendCount = value; }
        }

        public int ForwardCount
        {
            get { return Headers.ForwardCount; }
            set {  Headers.ForwardCount = value; }
        }
        
        public SiloAddress TargetSilo
        {
            get { return Headers.TargetSilo; }
            set
            {
                Headers.TargetSilo = value;
                targetAddress = null;
            }
        }
        
        public GrainId TargetGrain
        {
            get { return Headers.TargetGrain; }
            set
            {
                Headers.TargetGrain = value;
                targetAddress = null;
            }
        }
        
        public ActivationId TargetActivation
        {
            get { return Headers.TargetActivation; }
            set
            {
                Headers.TargetActivation = value;
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
            get { return Headers.TargetObserverId; }
            set
            {
                Headers.TargetObserverId = value;
                targetAddress = null;
            }
        }
        
        public SiloAddress SendingSilo
        {
            get { return Headers.SendingSilo; }
            set
            {
                Headers.SendingSilo = value;
                sendingAddress = null;
            }
        }
        
        public GrainId SendingGrain
        {
            get { return Headers.SendingGrain; }
            set
            {
                Headers.SendingGrain = value;
                sendingAddress = null;
            }
        }
        
        public ActivationId SendingActivation
        {
            get { return Headers.SendingActivation; }
            set
            {
                Headers.SendingActivation = value;
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
            get { return Headers.IsNewPlacement; }
            set
            {
                 Headers.IsNewPlacement = value;
            }
        }

        public ResponseTypes Result
        {
            get { return Headers.Result; }
            set {  Headers.Result = value; }
        }

        public DateTime? Expiration
        {
            get { return Headers.Expiration; }
            set { Headers.Expiration = value; }
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
            get { return GetNotNullString(Headers.DebugContext); }
            set { Headers.DebugContext = value; }
        }

        public List<ActivationAddress> CacheInvalidationHeader
        {
            get { return Headers.CacheInvalidationHeader; }
            set { Headers.CacheInvalidationHeader = value; }
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
            SerializationManager.SerializeMessageHeaders(Headers, headerStream);

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

            AppendIfExists(HeadersContainer.Headers.CACHE_INVALIDATION_HEADER, sb, (m) => m.CacheInvalidationHeader);
            AppendIfExists(HeadersContainer.Headers.CATEGORY, sb, (m) => m.Category);
            AppendIfExists(HeadersContainer.Headers.DIRECTION, sb, (m) => m.Direction);
            AppendIfExists(HeadersContainer.Headers.EXPIRATION, sb, (m) => m.Expiration);
            AppendIfExists(HeadersContainer.Headers.FORWARD_COUNT, sb, (m) => m.ForwardCount);
            AppendIfExists(HeadersContainer.Headers.GENERIC_GRAIN_TYPE, sb, (m) => m.GenericGrainType);
            AppendIfExists(HeadersContainer.Headers.CORRELATION_ID, sb, (m) => m.Id);
            AppendIfExists(HeadersContainer.Headers.ALWAYS_INTERLEAVE, sb, (m) => m.IsAlwaysInterleave);
            AppendIfExists(HeadersContainer.Headers.IS_NEW_PLACEMENT, sb, (m) => m.IsNewPlacement);
            AppendIfExists(HeadersContainer.Headers.READ_ONLY, sb, (m) => m.IsReadOnly);
            AppendIfExists(HeadersContainer.Headers.IS_UNORDERED, sb, (m) => m.IsUnordered);
            AppendIfExists(HeadersContainer.Headers.IS_RETURNED_FROM_REMOTE_CLUSTER, sb, (m) => m.IsReturnedFromRemoteCluster);
            AppendIfExists(HeadersContainer.Headers.NEW_GRAIN_TYPE, sb, (m) => m.NewGrainType);
            AppendIfExists(HeadersContainer.Headers.REJECTION_INFO, sb, (m) => m.RejectionInfo);
            AppendIfExists(HeadersContainer.Headers.REJECTION_TYPE, sb, (m) => m.RejectionType);
            AppendIfExists(HeadersContainer.Headers.REQUEST_CONTEXT, sb, (m) => m.RequestContextData);
            AppendIfExists(HeadersContainer.Headers.RESEND_COUNT, sb, (m) => m.ResendCount);
            AppendIfExists(HeadersContainer.Headers.RESULT, sb, (m) => m.Result);
            AppendIfExists(HeadersContainer.Headers.SENDING_ACTIVATION, sb, (m) => m.SendingActivation);
            AppendIfExists(HeadersContainer.Headers.SENDING_GRAIN, sb, (m) => m.SendingGrain);
            AppendIfExists(HeadersContainer.Headers.SENDING_SILO, sb, (m) => m.SendingSilo);
            AppendIfExists(HeadersContainer.Headers.TARGET_ACTIVATION, sb, (m) => m.TargetActivation);
            AppendIfExists(HeadersContainer.Headers.TARGET_GRAIN, sb, (m) => m.TargetGrain);
            AppendIfExists(HeadersContainer.Headers.TARGET_OBSERVER, sb, (m) => m.TargetObserverId);
            AppendIfExists(HeadersContainer.Headers.TARGET_SILO, sb, (m) => m.TargetSilo);

            return sb.ToString();
        }
        
        private void AppendIfExists(HeadersContainer.Headers header, StringBuilder sb, Func<Message, object> valueProvider)
        {
            // used only under log3 level
            if ((Headers.GetHeadersMask() & header) != HeadersContainer.Headers.NONE)
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
            [Flags]
            public enum Headers
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
                IS_RETURNED_FROM_REMOTE_CLUSTER = 1 << 25,
                // Do not add over int.MaxValue of these.
            }

            private Categories _category;
            private Directions? _direction;
            private bool _isReadOnly;
            private bool _isAlwaysInterleave;
            private bool _isUnordered;
            private bool _isReturnedFromRemoteCluster;
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

            public Categories Category
            {
                get { return _category; }
                set
                {
                    _category = value;
                }
            }

            public Directions? Direction
            {
                get { return _direction; }
                set
                {
                    _direction = value;
                }
            }

            public bool IsReadOnly
            {
                get { return _isReadOnly; }
                set
                {
                    _isReadOnly = value;
                }
            }

            public bool IsAlwaysInterleave
            {
                get { return _isAlwaysInterleave; }
                set
                {
                    _isAlwaysInterleave = value;
                }
            }

            public bool IsUnordered
            {
                get { return _isUnordered; }
                set
                {
                    _isUnordered = value;
                }
            }

            public bool IsReturnedFromRemoteCluster
            {
                get { return _isReturnedFromRemoteCluster; }
                set
                {
                    _isReturnedFromRemoteCluster = value;
                }
            }

            public CorrelationId Id
            {
                get { return _id; }
                set
                {
                    _id = value;
                }
            }

            public int ResendCount
            {
                get { return _resendCount; }
                set
                {
                    _resendCount = value;
                }
            }

            public int ForwardCount
            {
                get { return _forwardCount; }
                set
                {
                    _forwardCount = value;
                }
            }

            public SiloAddress TargetSilo
            {
                get { return _targetSilo; }
                set
                {
                    _targetSilo = value;
                }
            }

            public GrainId TargetGrain
            {
                get { return _targetGrain; }
                set
                {
                    _targetGrain = value;
                }
            }

            public ActivationId TargetActivation
            {
                get { return _targetActivation; }
                set
                {
                    _targetActivation = value;
                }
            }

            public GuidId TargetObserverId
            {
                get { return _targetObserverId; }
                set
                {
                    _targetObserverId = value;
                }
            }

            public SiloAddress SendingSilo
            {
                get { return _sendingSilo; }
                set
                {
                    _sendingSilo = value;
                }
            }

            public GrainId SendingGrain
            {
                get { return _sendingGrain; }
                set
                {
                    _sendingGrain = value;
                }
            }

            public ActivationId SendingActivation
            {
                get { return _sendingActivation; }
                set
                {
                    _sendingActivation = value;
                }
            }

            public bool IsNewPlacement
            {
                get { return _isNewPlacement; }
                set
                {
                    _isNewPlacement = value;
                }
            }

            public ResponseTypes Result
            {
                get { return _result; }
                set
                {
                    _result = value;
                }
            }

            public DateTime? Expiration
            {
                get { return _expiration; }
                set
                {
                    _expiration = value;
                }
            }


            public string DebugContext
            {
                get { return _debugContext; }
                set
                {
                    _debugContext = value;
                }
            }

            public List<ActivationAddress> CacheInvalidationHeader
            {
                get { return _cacheInvalidationHeader; }
                set
                {
                    _cacheInvalidationHeader = value;
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
                }
            }

            public RejectionTypes RejectionType
            {
                get { return _rejectionType; }
                set
                {
                    _rejectionType = value;
                }
            }

            public string RejectionInfo
            {
                get { return _rejectionInfo; }
                set
                {
                    _rejectionInfo = value;
                }
            }

            public Dictionary<string, object> RequestContextData
            {
                get { return _requestContextData; }
                set
                {
                    _requestContextData = value;
                }
            }

            internal Headers GetHeadersMask()
            {
                Headers headers = Headers.NONE;
                if(Category != default(Categories))
                    headers = headers | Headers.CATEGORY;

                headers = _direction == null ? headers & ~Headers.DIRECTION : headers | Headers.DIRECTION;
                if (IsReadOnly)
                    headers = headers | Headers.READ_ONLY;
                if (IsAlwaysInterleave)
                    headers = headers | Headers.ALWAYS_INTERLEAVE;
                if(IsUnordered)
                    headers = headers | Headers.IS_UNORDERED;

                headers = _id == null ? headers & ~Headers.CORRELATION_ID : headers | Headers.CORRELATION_ID;

                if (_resendCount != default(int))
                    headers = headers | Headers.RESEND_COUNT;
                if(_forwardCount != default (int))
                    headers = headers | Headers.FORWARD_COUNT;

                headers = _targetSilo == null ? headers & ~Headers.TARGET_SILO : headers | Headers.TARGET_SILO;
                headers = _targetGrain == null ? headers & ~Headers.TARGET_GRAIN : headers | Headers.TARGET_GRAIN;
                headers = _targetActivation == null ? headers & ~Headers.TARGET_ACTIVATION : headers | Headers.TARGET_ACTIVATION;
                headers = _targetObserverId == null ? headers & ~Headers.TARGET_OBSERVER : headers | Headers.TARGET_OBSERVER;
                headers = _sendingSilo == null ? headers & ~Headers.SENDING_SILO : headers | Headers.SENDING_SILO;
                headers = _sendingGrain == null ? headers & ~Headers.SENDING_GRAIN : headers | Headers.SENDING_GRAIN;
                headers = _sendingActivation == null ? headers & ~Headers.SENDING_ACTIVATION : headers | Headers.SENDING_ACTIVATION;
                headers = _isNewPlacement == default(bool) ? headers & ~Headers.IS_NEW_PLACEMENT : headers | Headers.IS_NEW_PLACEMENT;
                headers = _result == default(ResponseTypes)? headers & ~Headers.RESULT : headers | Headers.RESULT;
                headers = _expiration == null ? headers & ~Headers.EXPIRATION : headers | Headers.EXPIRATION;
                headers = string.IsNullOrEmpty(_debugContext) ? headers & ~Headers.DEBUG_CONTEXT : headers | Headers.DEBUG_CONTEXT;
                headers = _cacheInvalidationHeader == null || _cacheInvalidationHeader.Count == 0 ? headers & ~Headers.CACHE_INVALIDATION_HEADER : headers | Headers.CACHE_INVALIDATION_HEADER;
                headers = string.IsNullOrEmpty(_newGrainType) ? headers & ~Headers.NEW_GRAIN_TYPE : headers | Headers.NEW_GRAIN_TYPE;
                headers = string.IsNullOrEmpty(GenericGrainType) ? headers & ~Headers.GENERIC_GRAIN_TYPE : headers | Headers.GENERIC_GRAIN_TYPE;
                headers = _rejectionType == default(RejectionTypes) ? headers & ~Headers.REJECTION_TYPE : headers | Headers.REJECTION_TYPE;
                headers = string.IsNullOrEmpty(_rejectionInfo) ? headers & ~Headers.REJECTION_INFO : headers | Headers.REJECTION_INFO;
                headers = _requestContextData == null || _requestContextData.Count == 0 ? headers & ~Headers.REQUEST_CONTEXT : headers | Headers.REQUEST_CONTEXT;
                return headers;
            }

            static HeadersContainer()
            {
                Register();
            }


            [Orleans.CodeGeneration.CopierMethodAttribute]
            public static System.Object DeepCopier(System.Object original)
            {
                return original;
            }

            [Orleans.CodeGeneration.SerializerMethodAttribute]
            public static void Serializer(System.Object untypedInput,  BinaryTokenStreamWriter stream, System.Type expected)
            {
                HeadersContainer input = (HeadersContainer)untypedInput;
                var headers = input.GetHeadersMask();
                stream.Write((int)headers);
                if ((headers & Headers.CACHE_INVALIDATION_HEADER) != Headers.NONE)
                {
                    var count = input.CacheInvalidationHeader.Count;
                    stream.Write(input.CacheInvalidationHeader.Count);
                    for (int i = 0; i < count; i++)
                    {
                        WriteObj(stream, typeof(ActivationAddress), input.CacheInvalidationHeader[i]);
                    }
                }

                if ((headers & Headers.CATEGORY) != Headers.NONE)
                {
                    stream.Write((byte)input.Category);
                }

                if ((headers & Headers.DEBUG_CONTEXT) != Headers.NONE)
                    stream.Write(input.DebugContext);

                if ((headers & Headers.DIRECTION) != Headers.NONE)
                    stream.Write((byte)input.Direction.Value);

                if ((headers & Headers.EXPIRATION) != Headers.NONE)
                    stream.Write(input.Expiration.Value);

                if ((headers & Headers.FORWARD_COUNT) != Headers.NONE)
                    stream.Write(input.ForwardCount);

                if ((headers & Headers.GENERIC_GRAIN_TYPE) != Headers.NONE)
                    stream.Write(input.GenericGrainType);

                if ((headers & Headers.CORRELATION_ID) != Headers.NONE)
                    stream.Write(input.Id);

                if ((headers & Headers.ALWAYS_INTERLEAVE) != Headers.NONE)
                    stream.Write(input.IsAlwaysInterleave);

                if ((headers & Headers.IS_NEW_PLACEMENT) != Headers.NONE)
                    stream.Write(input.IsNewPlacement);

                if ((headers & Headers.READ_ONLY) != Headers.NONE)
                    stream.Write(input.IsReadOnly);

                if ((headers & Headers.IS_UNORDERED) != Headers.NONE)
                    stream.Write(input.IsUnordered);

                if ((headers & Headers.NEW_GRAIN_TYPE) != Headers.NONE)
                    stream.Write(input.NewGrainType);

                if ((headers & Headers.REJECTION_INFO) != Headers.NONE)
                    stream.Write(input.RejectionInfo);

                if ((headers & Headers.REJECTION_TYPE) != Headers.NONE)
                    stream.Write((byte)input.RejectionType);

                if ((headers & Headers.REQUEST_CONTEXT) != Headers.NONE)
                {
                    var requestData = input.RequestContextData;
                    var count = requestData.Count;
                    stream.Write(count);
                    foreach (var d in requestData)
                    {
                        stream.Write(d.Key);
                        SerializationManager.SerializeInner(d.Value, stream, typeof(object));
                    }
                }

                if ((headers & Headers.RESEND_COUNT) != Headers.NONE)
                    stream.Write(input.ResendCount);

                if ((headers & Headers.RESULT) != Headers.NONE)
                    stream.Write((byte)input.Result);

                if ((headers & Headers.SENDING_ACTIVATION) != Headers.NONE)
                {
                    stream.Write(input.SendingActivation);
                }

                if ((headers & Headers.SENDING_GRAIN) != Headers.NONE)
                {
                    stream.Write(input.SendingGrain);
                }

                if ((headers & Headers.SENDING_SILO) != Headers.NONE)
                {
                    stream.Write(input.SendingSilo);
                }

                if ((headers & Headers.TARGET_ACTIVATION) != Headers.NONE)
                {
                    stream.Write(input.TargetActivation);
                }

                if ((headers & Headers.TARGET_GRAIN) != Headers.NONE)
                {
                    stream.Write(input.TargetGrain);
                }

                if ((headers & Headers.TARGET_OBSERVER) != Headers.NONE)
                {
                    WriteObj(stream, typeof(GuidId), input.TargetObserverId);
                }

                if ((headers & Headers.TARGET_SILO) != Headers.NONE)
                {
                    stream.Write(input.TargetSilo);
                }
            }

            [Orleans.CodeGeneration.DeserializerMethodAttribute]
            public static System.Object Deserializer(System.Type expected,  BinaryTokenStreamReader stream)
            {
                var result = new HeadersContainer();
                Orleans.Serialization.DeserializationContext.Current.RecordObject(result);
                var headers = (Headers)stream.ReadInt();

                if ((headers & Headers.CACHE_INVALIDATION_HEADER) != Headers.NONE)
                {
                    var n = stream.ReadInt();
                    if (n > 0)
                    {
                       var list = result.CacheInvalidationHeader = new List<ActivationAddress>(n);
                        for (int i = 0; i < n; i++)
                        {
                            list.Add((ActivationAddress)ReadObj(typeof(ActivationAddress), stream));
                        }
                    }
                }

                if ((headers & Headers.CATEGORY) != Headers.NONE)
                    result.Category = (Categories)stream.ReadByte();

                if ((headers & Headers.DEBUG_CONTEXT) != Headers.NONE)
                    result.DebugContext = stream.ReadString();

                if ((headers & Headers.DIRECTION) != Headers.NONE)
                    result.Direction = (Message.Directions)stream.ReadByte();

                if ((headers & Headers.EXPIRATION) != Headers.NONE)
                    result.Expiration = stream.ReadDateTime();

                if ((headers & Headers.FORWARD_COUNT) != Headers.NONE)
                    result.ForwardCount = stream.ReadInt();

                if ((headers & Headers.GENERIC_GRAIN_TYPE) != Headers.NONE)
                    result.GenericGrainType = stream.ReadString();

                if ((headers & Headers.CORRELATION_ID) != Headers.NONE)
                    result.Id = (Orleans.Runtime.CorrelationId)ReadObj(typeof(Orleans.Runtime.CorrelationId), stream);

                if ((headers & Headers.ALWAYS_INTERLEAVE) != Headers.NONE)
                    result.IsAlwaysInterleave = ReadBool(stream);

                if ((headers & Headers.IS_NEW_PLACEMENT) != Headers.NONE)
                    result.IsNewPlacement = ReadBool(stream);

                if ((headers & Headers.READ_ONLY) != Headers.NONE)
                    result.IsReadOnly = ReadBool(stream);

                if ((headers & Headers.IS_UNORDERED) != Headers.NONE)
                    result.IsUnordered = ReadBool(stream);

                if ((headers & Headers.NEW_GRAIN_TYPE) != Headers.NONE)
                    result.NewGrainType = stream.ReadString();

                if ((headers & Headers.REJECTION_INFO) != Headers.NONE)
                    result.RejectionInfo = stream.ReadString();

                if ((headers & Headers.REJECTION_TYPE) != Headers.NONE)
                    result.RejectionType = (RejectionTypes)stream.ReadByte();

                if ((headers & Headers.REQUEST_CONTEXT) != Headers.NONE)
                {
                    var c = stream.ReadInt();
                    var requestData = new Dictionary<string, object>(c);
                    for (int i = 0; i < c; i++)
                    {
                        requestData[stream.ReadString()] = SerializationManager.DeserializeInner(null, stream);
                    }
                    result.RequestContextData = requestData;
                }

                if ((headers & Headers.RESEND_COUNT) != Headers.NONE)
                    result.ResendCount = stream.ReadInt();

                if ((headers & Headers.RESULT) != Headers.NONE)
                    result.Result = (Orleans.Runtime.Message.ResponseTypes)stream.ReadByte();

                if ((headers & Headers.SENDING_ACTIVATION) != Headers.NONE)
                    result.SendingActivation = stream.ReadActivationId();

                if ((headers & Headers.SENDING_GRAIN) != Headers.NONE)
                    result.SendingGrain = stream.ReadGrainId();

                if ((headers & Headers.SENDING_SILO) != Headers.NONE)
                    result.SendingSilo = stream.ReadSiloAddress();

                if ((headers & Headers.TARGET_ACTIVATION) != Headers.NONE) 
                    result.TargetActivation = stream.ReadActivationId();

                if ((headers & Headers.TARGET_GRAIN) != Headers.NONE)
                    result.TargetGrain = stream.ReadGrainId();

                if ((headers & Headers.TARGET_OBSERVER) != Headers.NONE)
                    result.TargetObserverId = (Orleans.Runtime.GuidId)ReadObj(typeof(Orleans.Runtime.GuidId), stream);

                if ((headers & Headers.TARGET_SILO) != Headers.NONE)
                    result.TargetSilo = stream.ReadSiloAddress();

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
                 SerializationManager.Register(typeof(HeadersContainer), DeepCopier, Serializer, Deserializer);
            }
        }
    }
}
