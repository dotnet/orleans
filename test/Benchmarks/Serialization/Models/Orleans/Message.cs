using System;
using System.Collections.Generic;
using System.Text;
using Orleans;

namespace FakeFx.Runtime
{
    [WellKnownId(101)]
    [GenerateSerializer]
    internal sealed class Message
    {
        public const int LENGTH_HEADER_SIZE = 8;
        public const int LENGTH_META_HEADER = 4;

        [NonSerialized]
        internal string _targetHistory;

        [NonSerialized]
        internal DateTime? _queuedTime;

        [NonSerialized]
        internal int _retryCount;

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

        public int RetryCount
        {
            get { return _retryCount; }
            set { _retryCount = value; }
        }
        
        // Cache values of TargetAddess and SendingAddress as they are used very frequently
        internal ActivationAddress targetAddress;
        internal ActivationAddress sendingAddress;
        
        static Message()
        {
        }

        [GenerateSerializer]
        public enum Categories
        {
            Ping,
            System,
            Application,
        }

        [GenerateSerializer]
        public enum Directions
        {
            Request,
            Response,
            OneWay
        }

        [GenerateSerializer]
        public enum ResponseTypes
        {
            Success,
            Error,
            Rejection,
            Status
        }

        [GenerateSerializer]
        public enum RejectionTypes
        {
            Transient,
            Overloaded,
            DuplicateRequest,
            Unrecoverable,
            GatewayTooBusy,
            CacheInvalidation
        }

        internal HeadersContainer Headers { get; set; } = new();

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

        public bool IsTransactionRequired
        {
            get { return Headers.IsTransactionRequired; }
            set { Headers.IsTransactionRequired = value; }
        }

        public CorrelationId Id
        {
            get { return Headers.Id; }
            set { Headers.Id = value; }
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
            get
            {
                if (targetAddress is object)
                {
                    return targetAddress;
                }

                if (!TargetGrain.IsDefault)
                {
                    return targetAddress = ActivationAddress.GetAddress(TargetSilo, TargetGrain, TargetActivation);
                }

                return null;
            }

            set
            {
                TargetGrain = value.Grain;
                TargetActivation = value.Activation;
                TargetSilo = value.Silo;
                targetAddress = value;
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

        public CorrelationId CallChainId
        {
            get { return Headers.CallChainId; }
            set { Headers.CallChainId = value; }
        }
        
        public TraceContext TraceContext {
            get { return Headers.TraceContext; }
            set { Headers.TraceContext = value; }
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

        public ushort InterfaceVersion
        {
            get { return Headers.InterfaceVersion; }
            set
            {
                Headers.InterfaceVersion = value;
            }
        }

        public GrainInterfaceType InterfaceType
        {
            get { return Headers.InterfaceType; }
            set
            {
                Headers.InterfaceType = value;
            }
        }

        public ResponseTypes Result
        {
            get { return Headers.Result; }
            set {  Headers.Result = value; }
        }

        public TimeSpan? TimeToLive
        {
            get { return Headers.TimeToLive; }
            set { Headers.TimeToLive = value; }
        }

        public bool IsExpired
        {
            get
            {
                if (!TimeToLive.HasValue)
                {
                    return false;
                }

                return TimeToLive <= TimeSpan.Zero;
            }
        }

        public object TransactionInfo
        {
            get { return Headers.TransactionInfo; }
            set { Headers.TransactionInfo = value; }
        }

        public List<ActivationAddress> CacheInvalidationHeader
        {
            get { return Headers.CacheInvalidationHeader; }
            set { Headers.CacheInvalidationHeader = value; }
        }

        public bool HasCacheInvalidationHeader => this.CacheInvalidationHeader != null
                                                  && this.CacheInvalidationHeader.Count > 0;
        
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

        public object BodyObject { get; set; }

        public void ClearTargetAddress()
        {
            targetAddress = null;
        }

        internal static string GetNotNullString(string s)
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
                
        // For testing and logging/tracing
        public string ToLongString()
        {
            var sb = new StringBuilder();

            AppendIfExists(HeadersContainer.Headers.CACHE_INVALIDATION_HEADER, sb, (m) => m.CacheInvalidationHeader);
            AppendIfExists(HeadersContainer.Headers.CATEGORY, sb, (m) => m.Category);
            AppendIfExists(HeadersContainer.Headers.DIRECTION, sb, (m) => m.Direction);
            AppendIfExists(HeadersContainer.Headers.TIME_TO_LIVE, sb, (m) => m.TimeToLive);
            AppendIfExists(HeadersContainer.Headers.FORWARD_COUNT, sb, (m) => m.ForwardCount);
            AppendIfExists(HeadersContainer.Headers.CORRELATION_ID, sb, (m) => m.Id);
            AppendIfExists(HeadersContainer.Headers.ALWAYS_INTERLEAVE, sb, (m) => m.IsAlwaysInterleave);
            AppendIfExists(HeadersContainer.Headers.IS_NEW_PLACEMENT, sb, (m) => m.IsNewPlacement);
            AppendIfExists(HeadersContainer.Headers.IS_RETURNED_FROM_REMOTE_CLUSTER, sb, (m) => m.IsReturnedFromRemoteCluster);
            AppendIfExists(HeadersContainer.Headers.READ_ONLY, sb, (m) => m.IsReadOnly);
            AppendIfExists(HeadersContainer.Headers.IS_UNORDERED, sb, (m) => m.IsUnordered);
            AppendIfExists(HeadersContainer.Headers.REJECTION_INFO, sb, (m) => m.RejectionInfo);
            AppendIfExists(HeadersContainer.Headers.REJECTION_TYPE, sb, (m) => m.RejectionType);
            AppendIfExists(HeadersContainer.Headers.REQUEST_CONTEXT, sb, (m) => m.RequestContextData);
            AppendIfExists(HeadersContainer.Headers.RESULT, sb, (m) => m.Result);
            AppendIfExists(HeadersContainer.Headers.SENDING_ACTIVATION, sb, (m) => m.SendingActivation);
            AppendIfExists(HeadersContainer.Headers.SENDING_GRAIN, sb, (m) => m.SendingGrain);
            AppendIfExists(HeadersContainer.Headers.SENDING_SILO, sb, (m) => m.SendingSilo);
            AppendIfExists(HeadersContainer.Headers.TARGET_ACTIVATION, sb, (m) => m.TargetActivation);
            AppendIfExists(HeadersContainer.Headers.TARGET_GRAIN, sb, (m) => m.TargetGrain);
            AppendIfExists(HeadersContainer.Headers.CALL_CHAIN_ID, sb, (m) => m.CallChainId);
            AppendIfExists(HeadersContainer.Headers.TRACE_CONTEXT, sb, (m) => m.TraceContext);
            AppendIfExists(HeadersContainer.Headers.TARGET_SILO, sb, (m) => m.TargetSilo);

            return sb.ToString();
        }
        
        internal void AppendIfExists(HeadersContainer.Headers header, StringBuilder sb, Func<Message, object> valueProvider)
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
            string response = string.Empty;
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

                    case ResponseTypes.Status:
                        response = "Status ";
                        break;

                    default:
                        break;
                }
            }
            return string.Format("{0}{1}{2}{3}{4} {5}->{6} #{7}{8}",
                IsReadOnly ? "ReadOnly " : "", //0
                IsAlwaysInterleave ? "IsAlwaysInterleave " : "", //1
                IsNewPlacement ? "NewPlacement " : "", // 2
                response,  //3
                Direction, //4
                $"[{SendingSilo} {SendingGrain} {SendingActivation}]", //5
                $"[{TargetSilo} {TargetGrain} {TargetActivation}]", //6
                Id, //7
                ForwardCount > 0 ? "[ForwardCount=" + ForwardCount + "]" : ""); //8
        }

        public static Message CreatePromptExceptionResponse(Message request, Exception exception)
        {
            return new()
            {
                Category = request.Category,
                Direction = Message.Directions.Response,
                Result = Message.ResponseTypes.Error,
                BodyObject = Response.ExceptionResponse(exception)
            };
        }

        [Serializable]
        [GenerateSerializer]
        [SuppressReferenceTracking]
        [OmitDefaultMemberValues]
        public sealed class HeadersContainer
        {
            [Flags]
            public enum Headers
            {
                NONE = 0,
                ALWAYS_INTERLEAVE = 1 << 0,
                CACHE_INVALIDATION_HEADER = 1 << 1,
                CATEGORY = 1 << 2,
                CORRELATION_ID = 1 << 3,
                DEBUG_CONTEXT = 1 << 4, // No longer used
                DIRECTION = 1 << 5,
                TIME_TO_LIVE = 1 << 6,
                FORWARD_COUNT = 1 << 7,
                NEW_GRAIN_TYPE = 1 << 8,
                GENERIC_GRAIN_TYPE = 1 << 9,
                RESULT = 1 << 10,
                REJECTION_INFO = 1 << 11,
                REJECTION_TYPE = 1 << 12,
                READ_ONLY = 1 << 13,
                RESEND_COUNT = 1 << 14, // Support removed. Value retained for backwards compatibility.
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
                INTERFACE_VERSION = 1 << 26,

                // transactions
                TRANSACTION_INFO = 1 << 27,
                IS_TRANSACTION_REQUIRED = 1 << 28,

                CALL_CHAIN_ID = 1 << 29,

                TRACE_CONTEXT = 1 << 30,

                INTERFACE_TYPE = 1 << 31
                // Do not add over int.MaxValue of these.
            }

            [Id(1)]
            public Categories _category;
            [Id(2)]
            public Directions? _direction;
            [Id(3)]
            public bool _isReadOnly;
            [Id(4)]
            public bool _isAlwaysInterleave;
            [Id(5)]
            public bool _isUnordered;
            [Id(6)]
            public bool _isReturnedFromRemoteCluster;
            [Id(7)]
            public bool _isTransactionRequired;
            [Id(8)]
            public CorrelationId _id;
            [Id(9)]
            public int _forwardCount;
            [Id(10)]
            public SiloAddress _targetSilo;
            [Id(11)]
            public GrainId _targetGrain;
            [Id(12)]
            public ActivationId _targetActivation;
            [Id(13)]
            public SiloAddress _sendingSilo;
            [Id(14)]
            public GrainId _sendingGrain;
            [Id(15)]
            public ActivationId _sendingActivation;
            [Id(16)]
            public bool _isNewPlacement;
            [Id(17)]
            public ushort _interfaceVersion;
            [Id(18)]
            public ResponseTypes _result;
            [Id(19)]
            public object _transactionInfo;
            [Id(20)]
            public TimeSpan? _timeToLive;
            [Id(21)]
            public List<ActivationAddress> _cacheInvalidationHeader;
            [Id(22)]
            public RejectionTypes _rejectionType;
            [Id(23)]
            public string _rejectionInfo;
            [Id(24)]
            public Dictionary<string, object> _requestContextData;
            [Id(25)]
            public CorrelationId _callChainId;
            [Id(26)]
            public readonly DateTime _localCreationTime;
            [Id(27)]
            public TraceContext _traceContext;
            [Id(28)]
            public GrainInterfaceType interfaceType;

            public HeadersContainer()
            {
                _localCreationTime = DateTime.UtcNow;
            }

            public TraceContext TraceContext
            {
                get { return _traceContext; }
                set { _traceContext = value; }
            }

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

            public bool IsTransactionRequired
            {
                get { return _isTransactionRequired; }
                set
                {
                    _isTransactionRequired = value;
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

            public ushort InterfaceVersion
            {
                get { return _interfaceVersion; }
                set
                {
                    _interfaceVersion = value;
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

            public object TransactionInfo
            {
                get { return _transactionInfo; }
                set
                {
                    _transactionInfo = value;
                }
            }

            public TimeSpan? TimeToLive
            {
                get
                {
                    return _timeToLive - (DateTime.UtcNow - _localCreationTime);
                }
                set
                {
                    _timeToLive = value;
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

            public CorrelationId CallChainId
            {
                get { return _callChainId; }
                set
                {
                    _callChainId = value;
                }
            }

            public GrainInterfaceType InterfaceType
            {
                get { return interfaceType; }
                set
                {
                    interfaceType = value;
                }
            }

            internal Headers GetHeadersMask()
            {
                Headers headers = Headers.NONE;
                if(Category != default(Categories))
                {
                    headers = headers | Headers.CATEGORY;
                }

                headers = _direction == null ? headers & ~Headers.DIRECTION : headers | Headers.DIRECTION;
                if (IsReadOnly)
                {
                    headers = headers | Headers.READ_ONLY;
                }

                if (IsAlwaysInterleave)
                {
                    headers = headers | Headers.ALWAYS_INTERLEAVE;
                }

                if (IsUnordered)
                {
                    headers = headers | Headers.IS_UNORDERED;
                }

                headers = _id == null ? headers & ~Headers.CORRELATION_ID : headers | Headers.CORRELATION_ID;

                if(_forwardCount != default (int))
                {
                    headers = headers | Headers.FORWARD_COUNT;
                }

                headers = _targetSilo == null ? headers & ~Headers.TARGET_SILO : headers | Headers.TARGET_SILO;
                headers = _targetGrain.IsDefault ? headers & ~Headers.TARGET_GRAIN : headers | Headers.TARGET_GRAIN;
                headers = _targetActivation is null ? headers & ~Headers.TARGET_ACTIVATION : headers | Headers.TARGET_ACTIVATION;
                headers = _sendingSilo is null ? headers & ~Headers.SENDING_SILO : headers | Headers.SENDING_SILO;
                headers = _sendingGrain.IsDefault ? headers & ~Headers.SENDING_GRAIN : headers | Headers.SENDING_GRAIN;
                headers = _sendingActivation is null ? headers & ~Headers.SENDING_ACTIVATION : headers | Headers.SENDING_ACTIVATION;
                headers = _isNewPlacement == default(bool) ? headers & ~Headers.IS_NEW_PLACEMENT : headers | Headers.IS_NEW_PLACEMENT;
                headers = _isReturnedFromRemoteCluster == default(bool) ? headers & ~Headers.IS_RETURNED_FROM_REMOTE_CLUSTER : headers | Headers.IS_RETURNED_FROM_REMOTE_CLUSTER;
                headers = _interfaceVersion == 0 ? headers & ~Headers.INTERFACE_VERSION : headers | Headers.INTERFACE_VERSION;
                headers = _result == default(ResponseTypes)? headers & ~Headers.RESULT : headers | Headers.RESULT;
                headers = _timeToLive == null ? headers & ~Headers.TIME_TO_LIVE : headers | Headers.TIME_TO_LIVE;
                headers = _cacheInvalidationHeader == null || _cacheInvalidationHeader.Count == 0 ? headers & ~Headers.CACHE_INVALIDATION_HEADER : headers | Headers.CACHE_INVALIDATION_HEADER;
                headers = _rejectionType == default(RejectionTypes) ? headers & ~Headers.REJECTION_TYPE : headers | Headers.REJECTION_TYPE;
                headers = string.IsNullOrEmpty(_rejectionInfo) ? headers & ~Headers.REJECTION_INFO : headers | Headers.REJECTION_INFO;
                headers = _requestContextData == null || _requestContextData.Count == 0 ? headers & ~Headers.REQUEST_CONTEXT : headers | Headers.REQUEST_CONTEXT;
                headers = _callChainId == null ? headers & ~Headers.CALL_CHAIN_ID : headers | Headers.CALL_CHAIN_ID;
                headers = _traceContext == null? headers & ~Headers.TRACE_CONTEXT : headers | Headers.TRACE_CONTEXT;
                headers = IsTransactionRequired ? headers | Headers.IS_TRANSACTION_REQUIRED : headers & ~Headers.IS_TRANSACTION_REQUIRED;
                headers = _transactionInfo == null ? headers & ~Headers.TRANSACTION_INFO : headers | Headers.TRANSACTION_INFO;
                headers = interfaceType.IsDefault ? headers & ~Headers.INTERFACE_TYPE : headers | Headers.INTERFACE_TYPE;
                return headers;
            }

            public override bool Equals(object obj)
            {
                if (obj is not HeadersContainer container)
                {
                    return false;
                }

                return _category == container._category
                    && _direction == container._direction
                    && _isReadOnly == container._isReadOnly
                    && _isAlwaysInterleave == container._isAlwaysInterleave
                    && _isUnordered == container._isUnordered
                    && _isReturnedFromRemoteCluster == container._isReturnedFromRemoteCluster
                    && _isTransactionRequired == container._isTransactionRequired
                    && EqualityComparer<CorrelationId>.Default.Equals(_id, container._id)
                    && _forwardCount == container._forwardCount
                    && EqualityComparer<SiloAddress>.Default.Equals(_targetSilo, container._targetSilo)
                    && _targetGrain.Equals(container._targetGrain)
                    && EqualityComparer<ActivationId>.Default.Equals(_targetActivation, container._targetActivation)
                    && EqualityComparer<SiloAddress>.Default.Equals(_sendingSilo, container._sendingSilo)
                    && _sendingGrain.Equals(container._sendingGrain)
                    && EqualityComparer<ActivationId>.Default.Equals(_sendingActivation, container._sendingActivation)
                    && _isNewPlacement == container._isNewPlacement
                    && _interfaceVersion == container._interfaceVersion
                    && _result == container._result
                    && EqualityComparer<object>.Default.Equals(_transactionInfo, container._transactionInfo)
                    && EqualityComparer<TimeSpan?>.Default.Equals(_timeToLive, container._timeToLive)
                    && EqualityComparer<List<ActivationAddress>>.Default.Equals(_cacheInvalidationHeader, container._cacheInvalidationHeader)
                    && _rejectionType == container._rejectionType
                    && _rejectionInfo == container._rejectionInfo
                    && EqualityComparer<CorrelationId>.Default.Equals(_callChainId, container._callChainId)
                    && _localCreationTime == container._localCreationTime
                    && EqualityComparer<TraceContext>.Default.Equals(_traceContext, container._traceContext)
                    && interfaceType.Equals(container.interfaceType);
            }

            public override int GetHashCode()
            {
                var hash = new HashCode();
                hash.Add(_category);
                hash.Add(_direction);
                hash.Add(_isReadOnly);
                hash.Add(_isAlwaysInterleave);
                hash.Add(_isUnordered);
                hash.Add(_isReturnedFromRemoteCluster);
                hash.Add(_isTransactionRequired);
                hash.Add(_id);
                hash.Add(_forwardCount);
                hash.Add(_targetSilo);
                hash.Add(_targetGrain);
                hash.Add(_targetActivation);
                hash.Add(_sendingSilo);
                hash.Add(_sendingGrain);
                hash.Add(_sendingActivation);
                hash.Add(_isNewPlacement);
                hash.Add(_interfaceVersion);
                hash.Add(_result);
                hash.Add(_transactionInfo);
                hash.Add(_timeToLive);
                hash.Add(_cacheInvalidationHeader);
                hash.Add(_rejectionType);
                hash.Add(_rejectionInfo);
                hash.Add(_requestContextData);
                hash.Add(_callChainId);
                hash.Add(_localCreationTime);
                hash.Add(_traceContext);
                hash.Add(interfaceType);
                hash.Add(TraceContext);
                hash.Add(Category);
                hash.Add(Direction);
                hash.Add(IsReadOnly);
                hash.Add(IsAlwaysInterleave);
                hash.Add(IsUnordered);
                hash.Add(IsReturnedFromRemoteCluster);
                hash.Add(IsTransactionRequired);
                hash.Add(Id);
                hash.Add(ForwardCount);
                hash.Add(TargetSilo);
                hash.Add(TargetGrain);
                hash.Add(TargetActivation);
                hash.Add(SendingSilo);
                hash.Add(SendingGrain);
                hash.Add(SendingActivation);
                hash.Add(IsNewPlacement);
                hash.Add(InterfaceVersion);
                hash.Add(Result);
                hash.Add(TransactionInfo);
                hash.Add(TimeToLive);
                hash.Add(CacheInvalidationHeader);
                hash.Add(RejectionType);
                hash.Add(RejectionInfo);
                hash.Add(RequestContextData);
                hash.Add(CallChainId);
                hash.Add(InterfaceType);
                return hash.ToHashCode();
            }
        }
    }

    [GenerateSerializer]
    internal class TraceContext
    {
        [Id(1)]
        public Guid ActivityId { get; set; }
    }
}
