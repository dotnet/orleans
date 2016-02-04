using System.Threading.Tasks;
using UnitTests.GrainInterfaces;
using UnitTests.Tester;
using Assert = Microsoft.VisualStudio.TestTools.UnitTesting.Assert;
using System;
using Xunit;
using Tester;

namespace UnitTests.General
{
    /// <summary>
    /// Tests that exceptions are correctly propagated.
    /// </summary>
    public class ExceptionPropagationTests : HostedTestClusterEnsureDefaultStarted
    {
        [Fact, TestCategory("BVT"), TestCategory("Functional")]
        public async Task TaskCancelationPropagation()
        {
            IExceptionGrain grain = GrainFactory.GetGrain<IExceptionGrain>(GetRandomGrainId());
            var actualException = default(Exception);
            try
            {
                await grain.Cancelled();
            }
            catch (Exception exception)
            {
                actualException = exception;
            }

            Assert.IsNotNull(actualException, "Expected grain call to throw a cancellation exception.");
            Assert.IsTrue(actualException is AggregateException);
            Assert.AreEqual(
                typeof(TaskCanceledException),
                ((AggregateException)actualException).InnerException.GetType());
        }
    }
}
