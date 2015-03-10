/*
Project Orleans Cloud Service SDK ver. 1.0
 
Copyright (c) Microsoft Corporation
 
All rights reserved.
 
MIT License

Permission is hereby granted, free of charge, to any person obtaining a copy of this software and 
associated documentation files (the ""Software""), to deal in the Software without restriction,
including without limitation the rights to use, copy, modify, merge, publish, distribute, sublicense,
and/or sell copies of the Software, and to permit persons to whom the Software is furnished to do so,
subject to the following conditions:

The above copyright notice and this permission notice shall be included in all copies or substantial portions of the Software.

THE SOFTWARE IS PROVIDED *AS IS*, WITHOUT WARRANTY OF ANY KIND, EXPRESS OR IMPLIED, INCLUDING BUT NOT LIMITED TO
THE WARRANTIES OF MERCHANTABILITY, FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE AUTHORS
OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER LIABILITY, WHETHER IN AN ACTION OF CONTRACT,
TORT OR OTHERWISE, ARISING FROM, OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE SOFTWARE.
*/

﻿using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Orleans.Runtime.Configuration;
using Orleans.Serialization;

namespace Orleans.Runtime
{
    [Serializable]
    internal class Message : IOutgoingMessage
    {
        public static class Header
        {
            public const string ALWAYS_INTERLEAVE = "#AI";
            public const string CACHE_INVALIDATION_HEADER = "#CIH";
            public const string CATEGORY = "#MT";
            public const string CORRELATION_ID = "#ID";
            public const string DEBUG_CONTEXT = "#CTX";
            public const string DIRECTION = "#ST";
            public const string EXPIRATION = "#EX";
            public const string FORWARD_COUNT = "#FC";
            public const string INTERFACE_ID = "#IID";
            public const string METHOD_ID = "#MID";
            public const string NEW_GRAIN_TYPE = "#NT";
            public const string GENERIC_GRAIN_TYPE = "#GGT";
            public const string RESULT = "#R";
            public const string REJECTION_INFO = "#RJI";
            public const string REJECTION_TYPE = "#RJT";
            public const string READ_ONLY = "#RO";
            public const string RESEND_COUNT = "#RS";
            public const string SENDING_ACTIVATION = "#SA";
            public const string SENDING_GRAIN = "#SG";
            public const string SENDING_SILO = "#SS";
            public const string IS_NEW_PLACEMENT = "#NP";

            public const string TARGET_ACTIVATION = "#TA";
            public const string TARGET_GRAIN = "#TG";
            public const string TARGET_SILO = "#TS";
            public const string TARGET_OBSERVER = "#TO";
            public const string TIMESTAMPS = "Times";
            public const string IS_UNORDERED = "#UO";

            public const char APPLICATION_HEADER_FLAG = '!';
            public const string PING_APPLICATION_HEADER = "Ping";
            public const string PRIOR_MESSAGE_ID = "#PMI";
            public const string PRIOR_MESSAGE_TIMES = "#PMT";
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

        private readonly Dictionary<string, object> headers;
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
        private static readonly Dictionary<string, TransitionStats[,]> lifecycleStatistics;

        internal static bool WriteMessagingTraces { get; set; }
        
        static Message()
        {
            lifecycleStatistics = new Dictionary<string, TransitionStats[,]>();
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

        public int MethodId
        {
            get { return GetScalarHeader<int>(Header.METHOD_ID); }
            set { SetHeader(Header.METHOD_ID, value); }
        }

        public int InterfaceId
        {
            get { return GetScalarHeader<int>(Header.INTERFACE_ID); }
            set { SetHeader(Header.INTERFACE_ID, value); }
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


        public object BodyObject
        {
            get
            {
                if (bodyObject != null)
                {
                    return bodyObject;
                }
                if (bodyBytes == null)
                {
                    return null;
                }
                try
                {
                    var stream = new BinaryTokenStreamReader(bodyBytes);
                    bodyObject = SerializationManager.Deserialize(stream);
                }
                catch (Exception ex)
                {
                    logger.Error(ErrorCode.Messaging_UnableToDeserializeBody, "Exception deserializing message body", ex);
                    throw;
                }
                finally
                {
                    BufferPool.GlobalPool.Release(bodyBytes);
                    bodyBytes = null;
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

        public Message()
        {
            headers = new Dictionary<string, object>();
            metadata = new Dictionary<string, object>();
            bodyObject = null;
            bodyBytes = null;
            headerBytes = null;
        }

        public Message(Categories type, Directions subtype)
            : this()
        {
            Category = type;
            Direction = subtype;
        }

        internal Message(byte[] header, byte[] body)
            : this(new List<ArraySegment<byte>> { new ArraySegment<byte>(header) },
                new List<ArraySegment<byte>> { new ArraySegment<byte>(body) })
        {
        }

        public Message(List<ArraySegment<byte>> header, List<ArraySegment<byte>> body)
        {
            metadata = new Dictionary<string, object>();

            var input = new BinaryTokenStreamReader(header);
            headers = SerializationManager.DeserializeMessageHeaders(input);
            BufferPool.GlobalPool.Release(header);

            bodyBytes = body;
            bodyObject = null;
            headerBytes = null;
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

            if (this.ContainsHeader(Header.TIMESTAMPS))
            {
                response.SetHeader(Header.TIMESTAMPS, this.GetHeader(Header.TIMESTAMPS));
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
            if (Message.WriteMessagingTraces) response.AddTimestamp(LifecycleTag.CreateResponse);

            RequestContext.ExportToMessage(response);

            return response;
        }

        public Message CreateRejectionResponse(RejectionTypes type, string info)
        {
            var response = CreateResponseMessage();
            response.Result = ResponseTypes.Rejection;
            response.RejectionType = type;
            response.RejectionInfo = info;
            if (logger.IsVerbose) logger.Verbose("Creating {0} rejection with info '{1}' for {2} at:" + Environment.NewLine + "{3}", type, info, this, new System.Diagnostics.StackTrace(true));
            return response;
        }

        public bool ContainsHeader(string tag)
        {
            return headers.ContainsKey(tag);
        }

        public void RemoveHeader(string tag)
        {
            lock (headers)
            {
                headers.Remove(tag);
                if (tag == Header.TARGET_ACTIVATION || tag == Header.TARGET_GRAIN | tag == Header.TARGET_SILO)
                    targetAddress = null;
            }
        }

        public void SetHeader(string tag, object value)
        {
            lock (headers)
            {
                headers[tag] = value;
            }
        }

        public object GetHeader(string tag)
        {
            object val;
            bool flag;
            lock (headers)
            {
                flag = headers.TryGetValue(tag, out val);
            }
            return flag ? val : null;
        }

        public string GetStringHeader(string tag)
        {
            object val;
            if (!headers.TryGetValue(tag, out val)) return String.Empty;

            var s = val as string;
            return s ?? String.Empty;
        }

        public T GetScalarHeader<T>(string tag)
        {
            object val;
            if (headers.TryGetValue(tag, out val))
            {
                return (T)val;
            }
            return default(T);
        }

        public T GetSimpleHeader<T>(string tag)
        {
            object val;
            if (!headers.TryGetValue(tag, out val) || val == null) return default(T);

            return val is T ? (T) val : default(T);
        }

        internal void SetApplicationHeaders(Dictionary<string, object> data)
        {
            lock (headers)
            {
                foreach (var item in data)
                {
                    string key = Header.APPLICATION_HEADER_FLAG + item.Key;
                    headers[key] = SerializationManager.DeepCopy(item.Value);
                }
            }
        }

        internal void GetApplicationHeaders(Dictionary<string, object> dict)
        {
            TryGetApplicationHeaders(ref dict);
        }

        private void TryGetApplicationHeaders(ref Dictionary<string, object> dict)
        {
            lock (headers)
            {
                foreach (var pair in headers)
                {
                    if (pair.Key[0] != Header.APPLICATION_HEADER_FLAG) continue;

                    if (dict == null)
                    {
                        dict = new Dictionary<string, object>();
                    }
                    dict[pair.Key.Substring(1)] = pair.Value;
                }
            }
        }

        public object GetApplicationHeader(string headerName)
        {
            lock (headers)
            {
                object obj;
                return headers.TryGetValue(Header.APPLICATION_HEADER_FLAG + headerName, out obj) ? obj : null;
            }
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

        #region Message timestamping

        private class TransitionStats
        {
            ulong count;
            TimeSpan totalTime;
            TimeSpan maxTime;

            public TransitionStats()
            {
                count = 0;
                totalTime = TimeSpan.Zero;
                maxTime = TimeSpan.Zero;
            }

            public void RecordTransition(TimeSpan time)
            {
                lock (this)
                {
                    count++;
                    totalTime += time;
                    if (time > maxTime)
                    {
                        maxTime = time;
                    }
                }
            }

            public override string ToString()
            {
                var sb = new StringBuilder();

                if (count > 0)
                {
                    sb.AppendFormat("{0}\t{1}\t{2}", count, totalTime.Divide(count), maxTime);
                }

                return sb.ToString();
            }
        }

        public void AddTimestamp(LifecycleTag tag)
        {
            if (logger.IsVerbose2)
            {
                if (LogVerbose(tag))
                    logger.Verbose("Message {0} {1}", tag, this);
                else if (logger.IsVerbose2)
                    logger.Verbose2("Message {0} {1}", tag, this);
            }

            if (WriteMessagingTraces)
            {
                var now = DateTime.UtcNow;
                var timestamp = new List<object> {tag, now};

                object val;
                List<object> list = null;
                if (headers.TryGetValue(Header.TIMESTAMPS, out val))
                {
                    list = val as List<object>;
                }
                if (list == null)
                {
                    list = new List<object>();
                    lock (headers)
                    {
                        headers[Header.TIMESTAMPS] = list;
                    }
                }
                else if (list.Count > 0)
                {
                    var last = list[list.Count - 1] as List<object>;
                    if (last != null)
                    {
                        var context = DebugContext;
                        if (String.IsNullOrEmpty(context))
                        {
                            context = "Unspecified";
                        }
                        TransitionStats[,] entry;
                        bool found;
                        lock (lifecycleStatistics)
                        {
                            found = lifecycleStatistics.TryGetValue(context, out entry);
                        }
                        if (!found)
                        {
                            var newEntry = new TransitionStats[32, 32];
                            for (int i = 0; i < 32; i++) for (int j = 0; j < 32; j++) newEntry[i, j] = new TransitionStats();
                            lock (lifecycleStatistics)
                            {
                                if (!lifecycleStatistics.TryGetValue(context, out entry))
                                {
                                    entry = newEntry;
                                    lifecycleStatistics.Add(context, entry);
                                }
                            }
                        }
                        int from = (int)(LifecycleTag)(last[0]);
                        int to = (int)tag;
                        entry[from, to].RecordTransition(now.Subtract((DateTime)last[1]));
                    }
                }
                list.Add(timestamp);
            }
            if (OnTrace != null)
                OnTrace(this, tag);
        }

        private static bool LogVerbose(LifecycleTag tag)
        {
            return tag == LifecycleTag.EnqueueOutgoing ||
                   tag == LifecycleTag.CreateNewPlacement ||
                   tag == LifecycleTag.EnqueueIncoming ||
                   tag == LifecycleTag.InvokeIncoming;
        }

        public List<Tuple<string, DateTime>> GetTimestamps()
        {
            var result = new List<Tuple<string, DateTime>>();

            object val;
            List<object> list = null;
            if (headers.TryGetValue(Header.TIMESTAMPS, out val))
            {
                list = val as List<object>;
            }
            if (list == null) return result;

            foreach (object item in list)
            {
                var stamp = item as List<object>;
                if ((stamp != null) && (stamp.Count == 2) && (stamp[0] is string) && (stamp[1] is DateTime))
                {
                    result.Add(new Tuple<string, DateTime>(stamp[0] as string, (DateTime)stamp[1]));
                }
            }
            return result;
        }

        public string GetTimestampString(bool singleLine = true, bool includeTimes = true, int indent = 0)
        {
            var sb = new StringBuilder();

            object val;
            List<object> list = null;
            if (headers.TryGetValue(Header.TIMESTAMPS, out val))
            {
                list = val as List<object>;
            }
            if (list == null) return sb.ToString();

            bool firstItem = true;
            var indentString = new string(' ', indent);
            foreach (object item in list)
            {
                var stamp = item as List<object>;
                if ((stamp == null) || (stamp.Count != 2) || (!(stamp[0] is string)) || (!(stamp[1] is DateTime)))
                    continue;

                if (!firstItem && singleLine)
                {
                    sb.Append(", ");
                }
                else if (!singleLine && (indent > 0))
                {
                    sb.Append(indentString);
                }
                sb.Append(stamp[0]);
                if (includeTimes)
                {
                    sb.Append(" ==> ");
                    var when = (DateTime)stamp[1];
                    sb.Append(when.ToString("HH:mm:ss.ffffff"));
                }
                if (!singleLine)
                {
                    sb.AppendLine();
                }
                firstItem = false;
            }
            return sb.ToString();
        }

        #endregion

        #region Serialization

        internal List<ArraySegment<byte>> Serialize()
        {
            int dummy1;
            int dummy2;
            return Serialize_Impl(false, out dummy1, out dummy2);
        }

        public List<ArraySegment<byte>> Serialize(out int headerLength)
        {
            int dummy;
            return Serialize_Impl(false, out headerLength, out dummy);
        }

        public List<ArraySegment<byte>> SerializeForBatching(out int headerLength, out int bodyLength)
        {
            return Serialize_Impl(true, out headerLength, out bodyLength);
        }

        private List<ArraySegment<byte>> Serialize_Impl(bool batching, out int headerLengthOut, out int bodyLengthOut)
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
            if (!batching)
            {
                bytes.Add(new ArraySegment<byte>(BitConverter.GetBytes(headerLength)));
                bytes.Add(new ArraySegment<byte>(BitConverter.GetBytes(bodyLength)));
            }
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
                }
            }
            string times = this.GetStringHeader(Header.TIMESTAMPS);
            return String.Format("{0}{1}{2}{3}{4} {5}->{6} #{7}{8}{9}{10}: {11}",
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
                string.IsNullOrEmpty(times) ? "" : "[" + times + "]", //10
                DebugContext); //11
        }

        /// <summary>
        /// Tags used to identify points in the message processing lifecycle for logging.
        /// Should be fewer than 32 since bit flags are used for filtering events.
        /// </summary>
        public enum LifecycleTag
        {
            Create = 0,
            EnqueueOutgoing,
            StartRouting,
            AsyncRouting,
            DoneRouting,
            SendOutgoing,
            ReceiveIncoming,
            RerouteIncoming,
            EnqueueForRerouting,
            EnqueueForForwarding,
            EnqueueIncoming,
            DequeueIncoming,
            CreateNewPlacement,
            TaskIncoming,
            TaskRedirect,
            EnqueueWaiting,
            EnqueueReady,
            EnqueueWorkItem,
            DequeueWorkItem,
            InvokeIncoming,
            CreateResponse,
        }

        /// <summary>
        /// Global function that is set to monitor message lifecycle events
        /// </summary>
        internal static Action<Message, LifecycleTag> OnTrace { private get; set; }

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
            if (ContainsHeader(Message.Header.TARGET_SILO))
            {
                history.Append(TargetSilo).Append(":");
            }
            if (ContainsHeader(Message.Header.TARGET_GRAIN))
            {
                history.Append(TargetGrain).Append(":");
            }
            if (ContainsHeader(Message.Header.TARGET_ACTIVATION))
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
