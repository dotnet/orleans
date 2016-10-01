using System;


namespace Orleans.CodeGeneration
{
    /// <summary>
    /// Data object holding metadata associated with a grain Invoke request.
    /// </summary>
    [Serializable]
    public sealed class InvokeMethodRequest
    {
        /// <summary> InterfaceId for this Invoke request. </summary>
        public int InterfaceId { get; private set; }
        /// <summary> MethodId for this Invoke request. </summary>
        public int MethodId { get; private set; }
        /// <summary> Arguments for this Invoke request. </summary>
        public object[] Arguments { get; private set; }

        internal InvokeMethodRequest(int interfaceId, int methodId, object[] arguments)
        {
            InterfaceId = interfaceId;
            MethodId = methodId;
            Arguments = arguments;
        }

        /// <summary> 
        /// String representation for this Invoke request. 
        /// </summary>
        /// <remarks>
        /// Note: This is not the serialized wire form of this Invoke request.
        /// </remarks>
        public override string ToString()
        {
            return String.Format("InvokeMethodRequest {0}:{1}", InterfaceId, MethodId);
        }
    }

    /// <summary>
    /// Invoke options for an <c>InvokeMethodRequest</c>
    /// </summary>
    /// <remarks>
    /// These flag values are used in Orleans generated invoker code, and should not be altered. </remarks>
    [Flags]
    public enum InvokeMethodOptions
    {
        /// <summary>No options defined.</summary>
        None = 0,

        /// <summary>Invocation is one-way with no feedback on whether the call succeeds or fails.</summary>
        OneWay = 0x04,

        /// <summary>Invocation is read-only and can interleave with other read-only invocations.</summary>
        ReadOnly = 0x08,

        /// <summary>Invocation does not care about ordering and can consequently be optimized.</summary>
        Unordered = 0x10,


        /// <summary>Obsolete field.</summary>
        [Obsolete]
        DelayForConsistency = 0x20,

        /// <summary>The invocation can interleave with any other request type, including write requests.</summary>
        AlwaysInterleave = 0x100,
    }
}
