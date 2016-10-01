using GrainCollection;
using GrainInterfaces;
using Moq;
using Moq.Protected;
using Orleans.Streams;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Xunit;

namespace GrainTests
{
    public class TestCollector
    {
        [Fact]
        public async Task mockResultShouldBe10()
        {
           

            //This is the mock of our collector grain under test 
            var collectorMock = new Mock<CollectorGrain>();
         

            //Provide a mock worker when a call is made to GetGrain<IWorker>
            var workerMock = new Mock<IWorker>();
            workerMock.Setup(wor => wor.GetAnswer()).Returns(async delegate
            {
                await Task.Delay(1000);
                return 1; //Return a 1 from all workers fro testing
            });
            collectorMock.Setup(coll => coll.GrainFactory.GetGrain<IWorker>(It.IsAny<long>(), It.IsAny<string>())).Returns(workerMock.Object);
            
            //Provide a mock stream implementation when GetStreamProvider is called
            var mockStream = Mock.Of<IAsyncStream<long>>(str => str.OnNextAsync(It.IsAny<long>(), null) == Task.Delay(10) && str.OnCompletedAsync() == Task.Delay(10));
            collectorMock.Protected().Setup<IStreamProvider>("GetStreamProvider", ItExpr.IsAny<string>()).Returns(Mock.Of<IStreamProvider>(sp => sp.GetStream<long>(It.IsAny<Guid>(), It.IsAny<string>()) == mockStream));

            //Provide a mock for persisting state
            collectorMock.Protected().Setup<Task>("WriteStateAsync").Returns(Task.CompletedTask);

            //Now get the actual object
            var collector = collectorMock.Object;

            //Create a new task scheduler that forces tasks to all start in a single thread.
            //This emulates the turn based concurrency model supplied by Orleans
            var scheduler = new util.LimitedConcurrencyLevelTaskScheduler(1);

            //Create a task and start it under the single thread taskscheduler
            var testTask = await Task.Factory.StartNew(async delegate
            {
                // run the tests and make assertions
                var result = await collector.GetSum();
                Assert.Equal(10, result);

                Mock.Get(mockStream).Verify(str => str.OnNextAsync(It.IsAny<long>(), null), Times.Exactly(10));

            }, CancellationToken.None,
    TaskCreationOptions.None,
    scheduler);

            await testTask; //need to await the testTask to ensure that all the sub tasks have run and that errors are propagated to the test framework
        }
    }
}
