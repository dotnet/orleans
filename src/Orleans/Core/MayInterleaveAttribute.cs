using System;

namespace Orleans.Concurrency
{
    /// <summary>
    /// The MayInterleaveAttribute attribute is used to mark classes 
    /// that want to control request interleaving via supplied method callback.
    /// </summary>
    /// <remarks>
    /// The callback method name should point to a public static function declared on the same class 
    /// and having the following signature: <c>public static bool MayInterleave(InvokeMethodRequest req)</c>
    /// </remarks>
    [AttributeUsage(AttributeTargets.Class)]
    public sealed class MayInterleaveAttribute : Attribute
    {
        /// <summary>
        /// The name of the callback method
        /// </summary>
        internal string CallbackMethodName { get; private set; }

        public MayInterleaveAttribute(string callbackMethodName)
        {
            CallbackMethodName = callbackMethodName;
        }
    }
}