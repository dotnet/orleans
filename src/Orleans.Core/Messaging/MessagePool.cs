#nullable enable
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;

namespace Orleans.Runtime
{
    /// <summary>
    /// A thread-local object pool for <see cref="Message"/> instances.
    /// </summary>
    internal static class MessagePool
    {
        private static readonly ThreadLocal<Stack<Message>> _messages = new(() => new());

#if DEBUG
        /// <summary>
        /// Tracks all messages that have been allocated but not returned to the pool.
        /// Only available in DEBUG builds. Must be enabled via <see cref="EnableLeakTracking"/>.
        /// </summary>
        private static readonly ConcurrentDictionary<Message, MessageAllocationInfo> _outstandingMessages = new();

        /// <summary>
        /// When true, tracks all message allocations for leak detection.
        /// Only available in DEBUG builds.
        /// </summary>
        public static bool EnableLeakTracking { get; set; }

        /// <summary>
        /// Gets all messages that have been allocated but not returned to the pool.
        /// Only available in DEBUG builds and when <see cref="EnableLeakTracking"/> is true.
        /// </summary>
        public static IReadOnlyCollection<MessageAllocationInfo> GetOutstandingMessages()
        {
            return _outstandingMessages.Values.ToArray();
        }

        /// <summary>
        /// Clears the outstanding messages tracking. Call this at the start of a test.
        /// </summary>
        public static void ClearLeakTracking()
        {
            _outstandingMessages.Clear();
        }

        /// <summary>
        /// Information about a message allocation for leak tracking.
        /// </summary>
        public sealed class MessageAllocationInfo
        {
            public Message Message { get; }
            public string AllocationStack { get; }
            public DateTime AllocationTime { get; }

            public MessageAllocationInfo(Message message, string allocationStack)
            {
                Message = message;
                AllocationStack = allocationStack;
                AllocationTime = DateTime.UtcNow;
            }

            public override string ToString() =>
                $"Message allocated at {AllocationTime:HH:mm:ss.fff}, Direction={Message.Direction}, Id={Message.Id}\nStack:\n{AllocationStack}";
        }
#endif

        /// <summary>
        /// The maximum number of messages to keep per thread.
        /// </summary>
        public static int MaxPoolSizePerThread { get; set; } = 128;

        /// <summary>
        /// Gets a message from the pool, or creates a new one if the pool is empty.
        /// </summary>
        public static Message Get()
        {
            var stack = _messages.Value!;
            if (!stack.TryPop(out var message))
            {
                message = new Message();
            }

            message.InitializeRefCount();

#if DEBUG
            if (EnableLeakTracking)
            {
                var info = new MessageAllocationInfo(message, Environment.StackTrace);
                _outstandingMessages[message] = info;
            }
#endif

            return message;
        }

        /// <summary>
        /// Returns a message to the pool after resetting it.
        /// </summary>
        public static void Return(Message message) => message.Release();

        internal static void ReturnCore(Message message)
        {
#if DEBUG
            if (EnableLeakTracking)
            {
                _outstandingMessages.TryRemove(message, out _);
            }
#endif

            message.Reset();

            var stack = _messages.Value!;
            if (stack.Count < MaxPoolSizePerThread)
            {
                stack.Push(message);
            }
        }
    }
}
