using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Orleans.Serialization.Invocation;

namespace Orleans.Runtime
{
    [WellKnownId(101)]
    internal sealed class Message : ISpanFormattable
    {
        public const int LENGTH_HEADER_SIZE = 8;
        public const int LENGTH_META_HEADER = 4;

        [NonSerialized]
        private short _retryCount;

        public CoarseStopwatch _timeToExpiry;

        public object BodyObject { get; set; }

        public PackedHeaders _headers;
        public CorrelationId _id;

        public Guid _callChainId;
        public Dictionary<string, object> _requestContextData;

        public SiloAddress _targetSilo;
        public GrainId _targetGrain;

        public SiloAddress _sendingSilo;
        public GrainId _sendingGrain;

        public ushort _interfaceVersion;
        public GrainInterfaceType _interfaceType;

        public List<GrainAddress> _cacheInvalidationHeader;

        public PackedHeaders Headers { get => _headers; set => _headers = value; }

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
            Unrecoverable,
            GatewayTooBusy,
            CacheInvalidation
        }

        public Directions Direction
        {
            get => _headers.Direction;
            set => _headers.Direction = value;
        }

        public bool HasDirection => _headers.Direction != Directions.None;

        public bool IsFullyAddressed => TargetSilo is not null && !TargetGrain.IsDefault;

        public bool IsExpired => _timeToExpiry is { IsDefault: false, ElapsedMilliseconds: > 0 };

        public short RetryCount
        {
            get => _retryCount;
            set => _retryCount = value;
        }

        public bool HasCacheInvalidationHeader => CacheInvalidationHeader is { Count: > 0 };

        public bool IsSystemMessage
        {
            get => _headers.HasFlag(MessageFlags.SystemMessage);
            set => _headers.SetFlag(MessageFlags.SystemMessage, value);
        }

        public bool IsReadOnly
        {
            get => _headers.HasFlag(MessageFlags.ReadOnly);
            set => _headers.SetFlag(MessageFlags.ReadOnly, value);
        }

        public bool IsAlwaysInterleave
        {
            get => _headers.HasFlag(MessageFlags.AlwaysInterleave);
            set => _headers.SetFlag(MessageFlags.AlwaysInterleave, value);
        }

        public bool IsUnordered
        {
            get => _headers.HasFlag(MessageFlags.Unordered);
            set => _headers.SetFlag(MessageFlags.Unordered, value);
        }

        public CorrelationId Id
        {
            get => _id;
            set => _id = value;
        }

        public int ForwardCount
        {
            get => _headers.ForwardCount;
            set => _headers.ForwardCount = (byte)value;
        }

        public SiloAddress TargetSilo
        {
            get => _targetSilo;
            set
            {
                _targetSilo = value;
            }
        }

        public GrainId TargetGrain
        {
            get => _targetGrain;
            set
            {
                _targetGrain = value;
            }
        }

        public SiloAddress SendingSilo
        {
            get => _sendingSilo;
            set
            {
                _sendingSilo = value;
            }
        }

        public GrainId SendingGrain
        {
            get => _sendingGrain;
            set
            {
                _sendingGrain = value;
            }
        }

        public ushort InterfaceVersion
        {
            get => _interfaceVersion;
            set
            {
                _interfaceVersion = value;
                _headers.SetFlag(MessageFlags.HasInterfaceVersion, value is not 0);
            }
        }

        public ResponseTypes Result
        {
            get => _headers.ResponseType;
            set => _headers.ResponseType = value;
        }

        public TimeSpan? TimeToLive
        {
            get => _timeToExpiry.IsDefault ? null : -_timeToExpiry.Elapsed;
            set
            {
                if (value.HasValue)
                {
                    SetTimeToLiveMilliseconds((long)value.Value.TotalMilliseconds);
                }
                else
                {
                    SetInfiniteTimeToLive();
                }
            }
        }

        internal long GetTimeToLiveMilliseconds() => -_timeToExpiry.ElapsedMilliseconds;

        internal void SetTimeToLiveMilliseconds(long milliseconds)
        {
            _headers.SetFlag(MessageFlags.HasTimeToLive, true);
            _timeToExpiry = CoarseStopwatch.StartNew(-milliseconds);
        }

        internal void SetInfiniteTimeToLive()
        {
            _headers.SetFlag(MessageFlags.HasTimeToLive, false);
            _timeToExpiry = default;
        }

        public List<GrainAddress> CacheInvalidationHeader
        {
            get => _cacheInvalidationHeader;
            set
            {
                _cacheInvalidationHeader = value;
                _headers.SetFlag(MessageFlags.HasCacheInvalidationHeader, value is not null);
            }
        }

        public Dictionary<string, object> RequestContextData
        {
            get => _requestContextData;
            set
            {
                _requestContextData = value;
                _headers.SetFlag(MessageFlags.HasRequestContextData, value is not null);
            }
        }

        public Guid CallChainId
        {
            get => _callChainId;
            set
            {
                _callChainId = value;
                _headers.SetFlag(MessageFlags.HasCallChainId, value != Guid.Empty);
            }
        }

        public GrainInterfaceType InterfaceType
        {
            get => _interfaceType;
            set
            {
                _interfaceType = value;
                _headers.SetFlag(MessageFlags.HasInterfaceType, !value.IsDefault);
            }
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

        public override string ToString() => $"{this}";

        string IFormattable.ToString(string format, IFormatProvider formatProvider) => ToString();

        bool ISpanFormattable.TryFormat(Span<char> dst, out int charsWritten, ReadOnlySpan<char> format, IFormatProvider provider)
        {
            ref var origin = ref MemoryMarshal.GetReference(dst);
            int len;

            if (IsReadOnly && !Append(ref dst, "ReadOnly ")) goto grow;
            if (IsAlwaysInterleave && !Append(ref dst, "IsAlwaysInterleave ")) goto grow;

            if (Direction == Directions.Response)
            {
                switch (Result)
                {
                    case ResponseTypes.Rejection when BodyObject is RejectionResponse rejection:
                        if (!dst.TryWrite($"{rejection.RejectionType} Rejection (info: {rejection.RejectionInfo}) ", out len)) goto grow;
                        dst = dst[len..];
                        break;

                    case ResponseTypes.Error:
                        if (!Append(ref dst, "Error ")) goto grow;
                        break;

                    case ResponseTypes.Status:
                        if (!Append(ref dst, "Status ")) goto grow;
                        break;
                }
            }

            if (!dst.TryWrite($"{Direction} [{SendingSilo} {SendingGrain}]->[{TargetSilo} {TargetGrain}]", out len)) goto grow;
            dst = dst[len..];

            if (BodyObject is { } request)
            {
                if (!dst.TryWrite($" {request}", out len)) goto grow;
                dst = dst[len..];
            }

            if (!dst.TryWrite($" #{Id}", out len)) goto grow;
            dst = dst[len..];

            if (ForwardCount > 0)
            {
                if (!dst.TryWrite($"[ForwardCount={ForwardCount}]", out len)) goto grow;
                dst = dst[len..];
            }

            charsWritten = (int)Unsafe.ByteOffset(ref origin, ref MemoryMarshal.GetReference(dst)) / sizeof(char);
            return true;

grow:
            charsWritten = 0;
            return false;

            static bool Append(ref Span<char> dst, ReadOnlySpan<char> value)
            {
                if (!value.TryCopyTo(dst))
                    return false;

                dst = dst[value.Length..];
                return true;
            }
        }

        internal bool IsPing() => _requestContextData?.TryGetValue(RequestContext.PING_APPLICATION_HEADER, out var value) == true && value is bool isPing && isPing;

        [Flags]
        internal enum MessageFlags : ushort
        {
            SystemMessage = 1 << 0,
            ReadOnly = 1 << 1,
            AlwaysInterleave = 1 << 2,
            Unordered = 1 << 3,

            HasRequestContextData = 1 << 4,
            HasInterfaceVersion = 1 << 5,
            HasCallChainId = 1 << 6,
            HasInterfaceType = 1 << 7,
            HasCacheInvalidationHeader = 1 << 8,
            HasTimeToLive = 1 << 9,

            // The most significant bit is reserved, possibly for use to indicate more data follows.
            Reserved = 1 << 15,
        }

        [StructLayout(LayoutKind.Explicit)]
        internal struct PackedHeaders
        {
            private const uint DirectionMask = 0x000F_0000;
            private const int DirectionShift = 16;
            private const uint ResponseTypeMask = 0x00F0_0000;
            private const int ResponseTypeShift = 20;

            public static implicit operator PackedHeaders(uint fields) => new() { _fields = fields };
            public static implicit operator uint(PackedHeaders value) => value._fields;

            // 32 bits: HHHH_HHHH RRRR_DDDD FFFF_FFFF FFFF_FFFF
            // F: 16 bits for MessageFlags
            // D: 4 bits for Direction
            // R: 4 bits for ResponseType
            // H: 8 bits for ForwardCount (hop count)
            [FieldOffset(0)]
            private uint _fields;

            [FieldOffset(0)]
            private MessageFlags _flags;

            [FieldOffset(24)]
            public byte ForwardCount;

            public Directions Direction
            {
                get => (Directions)((_fields & DirectionMask) >> DirectionShift);
                set => _fields = (_fields & ~DirectionMask) | (uint)value << DirectionShift;
            }

            public ResponseTypes ResponseType
            {
                get => (ResponseTypes)((_fields & ResponseTypeMask) >> ResponseTypeShift);
                set => _fields = (_fields & ~ResponseTypeMask) | (uint)value << ResponseTypeShift;
            }

            public bool HasFlag(MessageFlags flag) => _flags.HasFlag(flag);

            public void SetFlag(MessageFlags flag, bool value) => _flags = value switch
            {
                true => _flags | flag,
                _ => _flags & ~flag,
            };
        }
    }
}
