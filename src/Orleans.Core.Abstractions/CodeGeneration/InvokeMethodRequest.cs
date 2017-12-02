using System;
using System.Collections;
using System.Collections.Generic;

namespace Orleans.CodeGeneration
{
    /// <summary>
    /// Data object holding metadata associated with a grain Invoke request.
    /// </summary>
    [Serializable]
    public abstract class InvokeMethodRequest
    {
        /// <summary> InterfaceId for this Invoke request. </summary>
        public int InterfaceId { get; private set; }
        public ushort InterfaceVersion { get; private set; }
        /// <summary> MethodId for this Invoke request. </summary>
        public int MethodId { get; private set; }
        /// <summary> Arguments for this Invoke request. </summary>
        public IGrainCallArguments Arguments => (IGrainCallArguments)this;

        internal InvokeMethodRequest(int interfaceId, ushort interfaceVersion, int methodId)
        {
            InterfaceId = interfaceId;
            InterfaceVersion = interfaceVersion;
            MethodId = methodId;
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

    [Serializable]
    public sealed class InvokeMethodRequest<TArgs> : InvokeMethodRequest, IGrainCallArguments
        where TArgs : struct, IGrainCallArguments
    {
        /// <summary> Arguments for this Invoke request. </summary>
        public new TArgs Arguments;

        internal InvokeMethodRequest(int interfaceId, ushort interfaceVersion, int methodId)
            : base(interfaceId, interfaceVersion, methodId)
        {
        }

        internal InvokeMethodRequest(int interfaceId, ushort interfaceVersion, int methodId, ref TArgs arguments)
            : base(interfaceId, interfaceVersion, methodId)
        {
            Arguments = arguments;
        }

        object IGrainCallArguments.this[int index]
        {
            get => Arguments[index];
            set => Arguments[index] = value;
        }

        object IReadOnlyList<object>.this[int index] => Arguments[index];

        int IGrainCallArguments.Length => Arguments.Length;

        int IReadOnlyCollection<object>.Count => Arguments.Count;

        IEnumerator<object> IEnumerable<object>.GetEnumerator() => Arguments.GetEnumerator();

        IEnumerator IEnumerable.GetEnumerator() => Arguments.GetEnumerator();

        void IGrainCallArguments.Visit<TContext>(IGrainCallArgumentVisitor<TContext> vistor, TContext context) =>
            Arguments.Visit(vistor, context);
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

        // Transactional method options. 
        // NOTE: keep in sync with TransactionOption enum.
        TransactionRequired = 0x200,
        TransactionRequiresNew = 0x400,
    }
}
