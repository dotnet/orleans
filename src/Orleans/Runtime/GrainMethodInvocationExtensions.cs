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
        public static void InvokeOneWay<T>(this T grainReference, Func<T, Task> grainMethodInvocation) where T : class, IGrain
        {
            // double cast is safe, as it can be invoked by user only on grain references
            var oneWayGrainReferenceCopy = new GrainReference((GrainReference) (object)grainReference, InvokeMethodOptions.OneWay).Cast<T>();

            // Task always completed at this point
            grainMethodInvocation(oneWayGrainReferenceCopy).Ignore();
        }
    }
}
