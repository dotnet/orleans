using System;


namespace Orleans.CodeGeneration
{
    /// <summary>
    /// Data object holding metadata associated with a grain Invoke request.
    /// </summary>
    [Serializable]
    public sealed class InvokeMethodRequest
    {
        internal static IInvokeMethodRequestLoggingHelper Helper { get; set; }

        /// <summary> InterfaceId for this Invoke request. </summary>
        public int InterfaceTypeCode { get; private set; }
        /// <summary> MethodId for this Invoke request. </summary>
        public int MethodId { get; private set; }
        /// <summary> Arguments for this Invoke request. </summary>
        public object[] Arguments { get; private set; }

        internal InvokeMethodRequest(int interfaceTypeCode, int methodId, object[] arguments)
        {
            InterfaceTypeCode = interfaceTypeCode;
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
            if (Helper != null)
            {
                Helper.GetInterfaceAndMethodName(this.InterfaceTypeCode, this.MethodId, out var interfaceName, out var methodName);
                return $"InvokeMethodRequest [{interfaceName}:{methodName}]";
            }
            else
            {
                return $"InvokeMethodRequest [{this.InterfaceTypeCode}:{this.MethodId}]";
            }
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

        // Transactional method options.  
        // NOTE: keep in sync with TransactionOption enum.
        // We use a mask to define a set of bits we use for transaction options.
        TransactionMask = 0xE00,
        TransactionSuppress = 0x200,
        TransactionCreateOrJoin = 0x400,
        TransactionCreate = 0x600,
        TransactionJoin = 0x800,
        TransactionSupported = 0xA00,
        TransactionNotAllowed = 0xC00,
    }

    public static class InvokeMethodOptionsExtensions
    {
        public static bool IsTransactional(this InvokeMethodOptions options)
        {
            return (options & InvokeMethodOptions.TransactionMask) != 0;
        }

        public static bool IsTransactionOption(this InvokeMethodOptions options, InvokeMethodOptions test)
        {
            return (options & InvokeMethodOptions.TransactionMask) == test;
        }
    }
}
