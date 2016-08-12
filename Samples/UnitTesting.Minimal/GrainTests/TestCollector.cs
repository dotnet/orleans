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
            var scheduler = new util.LimitedConcurrencyLevelTaskScheduler(1);

            var workerMock = new Mock<IWorker>();
            workerMock.Setup(wor => wor.GetAnswer()).Returns(async delegate
            {
                await Task.Delay(10);
                return 1; //Return a 1 from all workers fro testing
            });
            var collector = Mock.Of<CollectorGrain>(coll => coll.GrainFactory.GetGrain<IWorker>(It.IsAny<long>(), It.IsAny<string>()) == workerMock.Object);

            var mockStream = Mock.Of<IAsyncStream<long>>(str => str.OnNextAsync(It.IsAny<long>(), null) == Task.Delay(10) && str.OnCompletedAsync() == Task.Delay(10));
            Mock.Get(collector).Protected().Setup<IStreamProvider>("GetStreamProvider", ItExpr.IsAny<string>()).Returns(Mock.Of<IStreamProvider>(sp => sp.GetStream<long>(It.IsAny<Guid>(), It.IsAny<string>()) == mockStream));
            Mock.Get(collector).Protected().Setup<Task>("WriteStateAsync").Returns(Task.CompletedTask);

            var testTask = await Task.Factory.StartNew(async delegate
            {
                
                var result = await collector.GetSum();
                Assert.Equal(10, result);

                Mock.Get(mockStream).Verify(str => str.OnNextAsync(It.IsAny<long>(), null), Times.Exactly(10));

            }, CancellationToken.None,
    TaskCreationOptions.None,
    scheduler);

            await testTask;
        }
    }
}
