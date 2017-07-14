using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Orleans.CodeGeneration;
using Orleans.Concurrency;

namespace Orleans.Runtime.GrainMethodInvocationExtensions
{
    public static class GrainMethodInvocationExtensions
    {
        /// <summary>
        /// Invokes a method of a grain interface is one-way fashion so that no response message will be sent to the caller.
        /// </summary>
        /// <typeparam name="T">Grain interface</typeparam>
        /// <param name="grainReference">Grain reference which will be copied and then a call executed on it</param>
        /// <param name="grainMethodInvocation">Function that should invoke grain method and return resulting task</param>
        public static void InvokeOneWay<T>(this T grainReference, Func<T, Task> grainMethodInvocation) where T : class, IAddressable
        {
            var oneWayGrainReferenceCopy = new GrainReference(grainReference.AsWeaklyTypedReference(), InvokeMethodOptions.OneWay).Cast<T>();

            // Task always completed at this point. Should also help catching situation of mistakenly calling the method on original grain reference
            var invokationResult = grainMethodInvocation(oneWayGrainReferenceCopy);
            if (!invokationResult.IsCompleted)
            {
                throw new InvalidOperationException("Invoking of methods with one way flag must result in completed task");
            }
        }
    }
}
