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

using System;
using System.Collections.Generic;
using System.Runtime.Serialization;

namespace Orleans.Runtime
{
    /// <summary>
    /// An exception class used by the Orleans runtime for reporting errors.
    /// </summary>
    /// <remarks>
    /// This is also the base class for any more specific exceptions 
    /// raised by the Orleans runtime.
    /// </remarks>
    [Serializable]
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Microsoft.Design", "CA1058:TypesShouldNotExtendCertainBaseTypes")]
    public class OrleansException : ApplicationException
    {
        public OrleansException() : base("Unexpected error.") { }

        public OrleansException(string message) : base(message) { }

        public OrleansException(string message, Exception innerException) : base(message, innerException) { }

        protected OrleansException(SerializationInfo info, StreamingContext context)
            : base(info, context)
        {
        }
    }

    /// <summary>
    /// Signifies that a gateway silo is currently in overloaded / load shedding state 
    /// and is unable to currently accept this message being sent.
    /// </summary>
    /// <remarks>
    /// This situation is usaully a transient condition.
    /// The message is likely to be accepted by this or another gateway if it is retransmitted at a later time.
    /// </remarks>
    [Serializable]
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Microsoft.Design", "CA1032:ImplementStandardExceptionConstructors")]
    public class GatewayTooBusyException : OrleansException
    {
        public GatewayTooBusyException() : base("Gateway too busy") { }

        protected GatewayTooBusyException(SerializationInfo info, StreamingContext context)
            : base(info, context)
        {
        }
    }

    /// <summary>
    /// Signifies that a silo is in an overloaded state where some 
    /// runtime limit setting is currently being exceeded, 
    /// and so that silo is unable to currently accept this message being sent.
    /// </summary>
    /// <remarks>
    /// This situation is often a transient condition.
    /// The message is likely to be accepted by this or another silo if it is retransmitted at a later time.
    /// </remarks>
    [Serializable]
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Microsoft.Design", "CA1032:ImplementStandardExceptionConstructors")]
    public class LimitExceededException : OrleansException
    {
        public LimitExceededException() : base("Limit exceeded") { }

        public LimitExceededException(string limitName, int current, int threshold, object extraInfo) 
            : base(string.Format("Limit exceeded {0} Current={1} Threshold={2} {3}", limitName, current, threshold, extraInfo)) { }

        public LimitExceededException(SerializationInfo info, StreamingContext context)
            : base(info, context)
        {
        }
    }

    /// <summary>
    /// Signifies that a silo has detected a deadlock / loop in a call graph.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Deadlock detection is not enabled by default in Orleans silos, 
    /// because it introduces some extra overhead in call handling.
    /// </para>
    /// <para>
    /// There are some constraints on the types of deadlock that can currently be detected 
    /// by Orleans silos.
    /// </para>
    /// </remarks>
    [Serializable]
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Microsoft.Design", "CA1032:ImplementStandardExceptionConstructors")]
    public class DeadlockException : OrleansException
    {
        internal IEnumerable<Tuple<GrainId, int, int>> CallChain { get; private set; }

        public DeadlockException() : base("Deadlock between grain calls") {}

        internal DeadlockException(IEnumerable<Tuple<GrainId, int, int>> callChain)
            : base(String.Format("Deadlock Exception for grain call chain {0}.", Utils.EnumerableToString(callChain, 
                            elem => String.Format("{0}.{1}.{2}", elem.Item1, elem.Item2, elem.Item3)))) 
        {
            CallChain = callChain;
        }

        protected DeadlockException(SerializationInfo info, StreamingContext context)
            : base(info, context)
        {
            if (info != null)
            {
                this.CallChain = (IEnumerable<Tuple<GrainId, int, int>>)info.GetValue("CallChain", typeof(IEnumerable<Tuple<GrainId, int, int>>));
            }
        }

        public override void GetObjectData(SerializationInfo info, StreamingContext context)
        {
            if (info != null)
            {
                info.AddValue("CallChain", this.CallChain, typeof(IEnumerable<Tuple<GrainId, int, int>>));
            }

            base.GetObjectData(info, context);
        }
    }

    /// <summary>
    /// Signifies that an attempt was made to invoke a grain extension method on a grain where that extension was not installed.
    /// </summary>
    [Serializable]
    public class GrainExtensionNotInstalledException : OrleansException
    {
        public GrainExtensionNotInstalledException() : base("GrainExtensionNotInstalledException") { }
        public GrainExtensionNotInstalledException(string msg) : base(msg) { }

        public GrainExtensionNotInstalledException(SerializationInfo info, StreamingContext context)
            : base(info, context)
        { }
    }
}

