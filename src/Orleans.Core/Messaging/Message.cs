using System;
using System.Collections.Generic;
using System.Text;
using Orleans.Serialization.Invocation;

namespace Orleans.Runtime
{
    [WellKnownId(101)]
    internal sealed class Message
    {
        public const int LENGTH_HEADER_SIZE = 8;
        public const int LENGTH_META_HEADER = 4;

        [NonSerialized]
        private string _targetHistory;

        [NonSerialized]
        private int _retryCount;

        // Cache values of TargetAddess and SendingAddress as they are used very frequently
        [NonSerialized]
        private GrainAddress _targetAddress;

        [NonSerialized]
        private GrainAddress _sendingAddress;

        // For statistical measuring of time spent in queues.
        [NonSerialized]
        private CoarseStopwatch _timeInterval;

        [NonSerialized]
        public readonly CoarseStopwatch _timeSinceCreation = CoarseStopwatch.StartNew();

        public object BodyObject { get; set; }

        public Categories _category;
        public Directions? _direction;
        public bool _isReadOnly;
        public bool _isAlwaysInterleave;
        public bool _isUnordered;
        public int _forwardCount;
        public CorrelationId _id;

        public CorrelationId _callChainId;
        public Dictionary<string, object> _requestContextData;

        public SiloAddress _targetSilo;
        public GrainId _targetGrain;
        public ActivationId _targetActivation;

        public ushort _interfaceVersion;
        public GrainInterfaceType _interfaceType;

        public SiloAddress _sendingSilo;
        public GrainId _sendingGrain;
        public ActivationId _sendingActivation;
        public TimeSpan? _timeToLive;

        public List<GrainAddress> _cacheInvalidationHeader;
        public ResponseTypes _result;
        public RejectionTypes _rejectionType;
        public string _rejectionInfo;

        [GenerateSerializer]
        public enum Categories : byte
        {
            None,
            Ping,
            System,
            Application,
        }

        [GenerateSerializer]
        public enum Directions : byte
        {
            None,
            Request,
            Response,
            OneWay
        }

        [GenerateSerializer]
        public enum ResponseTypes : byte
        {
            None,
            Success,
            Error,
            Rejection,
            Status
        }

        [GenerateSerializer]
        public enum RejectionTypes : byte
        {
            None,
            Transient,
            Overloaded,
            DuplicateRequest,
            Unrecoverable,
            GatewayTooBusy,
            CacheInvalidation
        }

        public Directions Direction
        {
            get => _direction ?? default;
            set => _direction = value;
        }

        public bool HasDirection => _direction.HasValue;

        public bool IsFullyAddressed => TargetSilo is object && !TargetGrain.IsDefault && !TargetActivation.IsDefault;

        public GrainAddress TargetAddress
        {
            get
            {
                if (_targetAddress is { } result) return result;
                if (!TargetGrain.IsDefault)
                {
                    return _targetAddress = GrainAddress.GetAddress(TargetSilo, TargetGrain, TargetActivation);
                }

                return null;
            }

            set
            {
                TargetGrain = value.GrainId;
                TargetActivation = value.ActivationId;
                TargetSilo = value.SiloAddress;
                _targetAddress = value;
            }
        }
        
        public GrainAddress SendingAddress
        {
            get => _sendingAddress ??= GrainAddress.GetAddress(SendingSilo, SendingGrain, SendingActivation);
            set
            {
                SendingGrain = value.GrainId;
                SendingActivation = value.ActivationId;
                SendingSilo = value.SiloAddress;
                _sendingAddress = value;
            }
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

        public string TargetHistory
        {
            get => _targetHistory;
            set => _targetHistory = value;
        }

        public int RetryCount
        {
            get => _retryCount;
            set => _retryCount = value;
        }

        public bool HasCacheInvalidationHeader => CacheInvalidationHeader is { Count: > 0 };

        public TimeSpan Elapsed => _timeInterval.Elapsed;

        public Categories Category
        {
            get => _category;
            set => _category = value;
        }

        public bool IsReadOnly
        {
            get => _isReadOnly;
            set => _isReadOnly = value;
        }

        public bool IsAlwaysInterleave
        {
            get => _isAlwaysInterleave;
            set => _isAlwaysInterleave = value;
        }

        public bool IsUnordered
        {
            get => _isUnordered;
            set => _isUnordered = value;
        }

        public CorrelationId Id
        {
            get => _id;
            set => _id = value;
        }

        public int ForwardCount
        {
            get => _forwardCount;
            set => _forwardCount = value;
        }

        public SiloAddress TargetSilo
        {
            get => _targetSilo;
            set
            {
                _targetSilo = value;
                _targetAddress = null;
            }
        }

        public GrainId TargetGrain
        {
            get => _targetGrain;
            set
            {
                _targetGrain = value;
                _targetAddress = null;
            }
        }

        public ActivationId TargetActivation
        {
            get => _targetActivation;
            set
            {
                _targetActivation = value;
                _targetAddress = null;
            }
        }

        public SiloAddress SendingSilo
        {
            get => _sendingSilo;
            set
            {
                _sendingSilo = value;
                _sendingAddress = null;
            }
        }

        public GrainId SendingGrain
        {
            get => _sendingGrain;
            set
            {
                _sendingGrain = value;
                _sendingAddress = null;
            }
        }

        public ActivationId SendingActivation
        {
            get => _sendingActivation;
            set
            {
                _sendingActivation = value;
                _sendingAddress = null;
            }
        }

        public ushort InterfaceVersion
        {
            get => _interfaceVersion;
            set => _interfaceVersion = value;
        }

        public ResponseTypes Result
        {
            get => _result;
            set => _result = value;
        }

        public TimeSpan? TimeToLive
        {
            get => _timeToLive - _timeSinceCreation.Elapsed;
            set => _timeToLive = value;
        }

        public List<GrainAddress> CacheInvalidationHeader
        {
            get => _cacheInvalidationHeader;
            set => _cacheInvalidationHeader = value;
        }

        public RejectionTypes RejectionType
        {
            get => _rejectionType;
            set => _rejectionType = value;
        }

        public string RejectionInfo
        {
            get => _rejectionInfo ?? "";
            set => _rejectionInfo = value;
        }

        public Dictionary<string, object> RequestContextData
        {
            get => _requestContextData;
            set => _requestContextData = value;
        }

        public CorrelationId CallChainId
        {
            get => _callChainId;
            set => _callChainId = value;
        }

        public GrainInterfaceType InterfaceType
        {
            get => _interfaceType;
            set => _interfaceType = value;
        }

        public bool IsExpirableMessage(bool dropExpiredMessages)
        {
            if (!dropExpiredMessages) return false;

            GrainId id = TargetGrain;
            if (id.IsDefault) return false;

            // don't set expiration for one way, system target and system grain messages.
            return Direction != Directions.OneWay && !id.IsSystemTarget();
        }
        
        internal void AddToCacheInvalidationHeader(GrainAddress address)
        {
            var list = new List<GrainAddress>();
            if (CacheInvalidationHeader != null)
            {
                list.AddRange(CacheInvalidationHeader);
            }

            list.Add(address);
            CacheInvalidationHeader = list;
        }
        
        public void ClearTargetAddress()
        {
            _targetAddress = null;
        }

        // For testing and logging/tracing
        public string ToLongString()
        {
            var sb = new StringBuilder();

            AppendIfExists(Headers.CACHE_INVALIDATION_HEADER, sb, (m) => m.CacheInvalidationHeader);
            AppendIfExists(Headers.CATEGORY, sb, (m) => m.Category);
            AppendIfExists(Headers.DIRECTION, sb, (m) => m.Direction);
            AppendIfExists(Headers.TIME_TO_LIVE, sb, (m) => m.TimeToLive);
            AppendIfExists(Headers.FORWARD_COUNT, sb, (m) => m.ForwardCount);
            AppendIfExists(Headers.CORRELATION_ID, sb, (m) => m.Id);
            AppendIfExists(Headers.ALWAYS_INTERLEAVE, sb, (m) => m.IsAlwaysInterleave);
            AppendIfExists(Headers.READ_ONLY, sb, (m) => m.IsReadOnly);
            AppendIfExists(Headers.IS_UNORDERED, sb, (m) => m.IsUnordered);
            AppendIfExists(Headers.REJECTION_INFO, sb, (m) => m.RejectionInfo);
            AppendIfExists(Headers.REJECTION_TYPE, sb, (m) => m.RejectionType);
            AppendIfExists(Headers.REQUEST_CONTEXT, sb, (m) => m.RequestContextData);
            AppendIfExists(Headers.RESULT, sb, (m) => m.Result);
            AppendIfExists(Headers.SENDING_ACTIVATION, sb, (m) => m.SendingActivation);
            AppendIfExists(Headers.SENDING_GRAIN, sb, (m) => m.SendingGrain);
            AppendIfExists(Headers.SENDING_SILO, sb, (m) => m.SendingSilo);
            AppendIfExists(Headers.TARGET_ACTIVATION, sb, (m) => m.TargetActivation);
            AppendIfExists(Headers.TARGET_GRAIN, sb, (m) => m.TargetGrain);
            AppendIfExists(Headers.CALL_CHAIN_ID, sb, (m) => m.CallChainId);
            AppendIfExists(Headers.TARGET_SILO, sb, (m) => m.TargetSilo);

            return sb.ToString();
        }
        
        private void AppendIfExists(Headers header, StringBuilder sb, Func<Message, object> valueProvider)
        {
            // used only under log3 level
            if ((GetHeadersMask() & header) != Headers.NONE)
            {
                sb.AppendFormat("{0}={1};", header, valueProvider(this));
                sb.AppendLine();
            }
        }

        public override string ToString()
        {
            var response = "";
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

            return $"{(IsReadOnly ? "ReadOnly" : "")}" +
                $"{(IsAlwaysInterleave ? " IsAlwaysInterleave" : "")}" +
                $" {response}{Direction}" +
                $" {$"[{SendingSilo} {SendingGrain} {SendingActivation}]"}->{$"[{TargetSilo} {TargetGrain} {TargetActivation}]"}" +
                $"{(BodyObject is { } request ? $" {request}" : string.Empty)}" +
                $" #{Id}{(ForwardCount > 0 ? "[ForwardCount=" + ForwardCount + "]" : "")}";
        }

        public string GetTargetHistory()
        {
            var history = new StringBuilder();
            history.Append("<");
            if (TargetSilo != null)
            {
                history.Append(TargetSilo).Append(":");
            }
            if (!TargetGrain.IsDefault)
            {
                history.Append(TargetGrain).Append(":");
            }
            if (!TargetActivation.IsDefault)
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

        public void Start()
        {
            _timeInterval = CoarseStopwatch.StartNew();
        }

        public void Stop()
        {
            _timeInterval.Stop();
        }

        public void Restart()
        {
            _timeInterval.Restart();
        }

        public static Message CreatePromptExceptionResponse(Message request, Exception exception)
        {
            return new Message
            {
                Category = request.Category,
                Direction = Message.Directions.Response,
                Result = Message.ResponseTypes.Error,
                BodyObject = Response.FromException(exception)
            };
        }

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
            SENDING_GRAIN = 1 << 16,
            SENDING_SILO = 1 << 17,
            //IS_NEW_PLACEMENT = 1 << 18,

            TARGET_ACTIVATION = 1 << 19,
            TARGET_GRAIN = 1 << 20,
            TARGET_SILO = 1 << 21,
            TARGET_OBSERVER = 1 << 22,
            IS_UNORDERED = 1 << 23,
            REQUEST_CONTEXT = 1 << 24,
            INTERFACE_VERSION = 1 << 26,

            CALL_CHAIN_ID = 1 << 29,

            INTERFACE_TYPE = 1 << 31
            // Do not add over int.MaxValue of these.
        }

        internal Headers GetHeadersMask()
        {
            var headers = Headers.NONE;
            if (Category != default)
            {
                headers |= Headers.CATEGORY;
            }

            headers = _direction == null ? headers & ~Headers.DIRECTION : headers | Headers.DIRECTION;

            if (IsReadOnly)
            {
                headers |= Headers.READ_ONLY;
            }

            if (IsAlwaysInterleave)
            {
                headers |= Headers.ALWAYS_INTERLEAVE;
            }

            if (IsUnordered)
            {
                headers |= Headers.IS_UNORDERED;
            }

            headers = _id.ToInt64() == 0 ? headers & ~Headers.CORRELATION_ID : headers | Headers.CORRELATION_ID;

            if (_forwardCount != default(int))
            {
                headers |= Headers.FORWARD_COUNT;
            }

            headers = _targetSilo == null ? headers & ~Headers.TARGET_SILO : headers | Headers.TARGET_SILO;
            headers = _targetGrain.IsDefault ? headers & ~Headers.TARGET_GRAIN : headers | Headers.TARGET_GRAIN;
            headers = _targetActivation.IsDefault ? headers & ~Headers.TARGET_ACTIVATION : headers | Headers.TARGET_ACTIVATION;
            headers = _sendingSilo is null ? headers & ~Headers.SENDING_SILO : headers | Headers.SENDING_SILO;
            headers = _sendingGrain.IsDefault ? headers & ~Headers.SENDING_GRAIN : headers | Headers.SENDING_GRAIN;
            headers = _sendingActivation.IsDefault ? headers & ~Headers.SENDING_ACTIVATION : headers | Headers.SENDING_ACTIVATION;
            headers = _interfaceVersion == 0 ? headers & ~Headers.INTERFACE_VERSION : headers | Headers.INTERFACE_VERSION;
            headers = _result == default(ResponseTypes) ? headers & ~Headers.RESULT : headers | Headers.RESULT;
            headers = _timeToLive == null ? headers & ~Headers.TIME_TO_LIVE : headers | Headers.TIME_TO_LIVE;
            headers = _cacheInvalidationHeader == null || _cacheInvalidationHeader.Count == 0 ? headers & ~Headers.CACHE_INVALIDATION_HEADER : headers | Headers.CACHE_INVALIDATION_HEADER;
            headers = _rejectionType == default(RejectionTypes) ? headers & ~Headers.REJECTION_TYPE : headers | Headers.REJECTION_TYPE;
            headers = string.IsNullOrEmpty(_rejectionInfo) ? headers & ~Headers.REJECTION_INFO : headers | Headers.REJECTION_INFO;
            headers = _requestContextData == null || _requestContextData.Count == 0 ? headers & ~Headers.REQUEST_CONTEXT : headers | Headers.REQUEST_CONTEXT;
            headers = _callChainId.ToInt64() == 0 ? headers & ~Headers.CALL_CHAIN_ID : headers | Headers.CALL_CHAIN_ID;
            headers = _interfaceType.IsDefault ? headers & ~Headers.INTERFACE_TYPE : headers | Headers.INTERFACE_TYPE;
            return headers;
        }
    }
}
