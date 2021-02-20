using System;
using Orleans.Runtime;
using Xunit;

namespace UnitTests.General
{
    public class MessageTests
    {
        [Fact, TestCategory("Functional")]
        public void Message_ElapsedZeroIfNeverStarted()
        {
            var m = new Message();
            Assert.Equal(TimeSpan.Zero, m.Elapsed);
        }
    }
}
