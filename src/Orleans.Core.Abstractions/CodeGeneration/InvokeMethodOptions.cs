using System;


namespace Orleans.CodeGeneration
{
    /// <summary>
    /// Invoke options for an <c>InvokeMethodRequest</c>
    /// </summary>
    /// <remarks>
    /// These flag values are used in Orleans generated invoker code, and should not be altered. </remarks>
    [Flags]
    [GenerateSerializer]
    public enum InvokeMethodOptions
    {
        /// <summary>No options defined.</summary>
        None = 0,

        /// <summary>Invocation is one-way with no feedback on whether the call succeeds or fails.</summary>
        OneWay = 1 << 0,

        /// <summary>Invocation is read-only and can interleave with other read-only invocations.</summary>
        ReadOnly = 1 << 1,

        /// <summary>The invocation can interleave with any other request type, including write requests.</summary>
        AlwaysInterleave = 1 << 2,

        /// <summary>Invocation does not care about ordering and can consequently be optimized.</summary>
        Unordered = 1 << 3,
    }
}
