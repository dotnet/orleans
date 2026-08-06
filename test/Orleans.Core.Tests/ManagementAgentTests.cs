
using Orleans.Runtime;
using Xunit;

namespace UnitTests
{
    /// <summary>
    /// Tests for Orleans management and monitoring components, particularly the SystemStatus enumeration.
    /// SystemStatus is used throughout Orleans to track the lifecycle state of silos and other runtime components.
    /// </summary>
    public class ManagementAgentTests
    {
        /// <summary>
        /// Tests the equality implementation of SystemStatus values to ensure proper comparison behavior.
        /// This is important for state management and monitoring throughout the Orleans runtime.
        /// </summary>
        [Fact, TestCategory("Functional"), TestCategory("Management")]
        public void SystemStatusEquals()
        {
            SystemStatus same1 = SystemStatus.Terminated;
            SystemStatus same2 = SystemStatus.Terminated;
            SystemStatus other = SystemStatus.Stopping;

            CheckEquals(same1, same2);
            CheckNotEquals(same1, other);
            CheckNotEquals(same2, other);
        }

        //[Fact]
        //public void CheckEventChannelEnvSetup()
        //{
        //    bool found = ManagementBusConnector.CheckEventChannelEnvSetup();
        //    Console.WriteLine("CheckEventChannelEnvSetup=" + found);
        //}

        private static void CheckEquals(object obj, object other)
        {
            Assert.NotNull(obj);
            Assert.NotNull(other);
            Assert.Equal(obj, other);
            Assert.Equal(other, obj);
            Assert.Equal(obj.GetHashCode(), other.GetHashCode());
            Assert.True(obj.Equals(other));
            Assert.True(other.Equals(obj));
            Assert.True(obj == other);
            Assert.True(other == obj);
            Assert.False(obj != other);
            Assert.False(other != obj);
        }

        private static void CheckNotEquals(object obj, object other)
        {
            Assert.NotNull(obj);
            Assert.NotNull(other);
            Assert.NotEqual(obj, other);
            Assert.NotEqual(other, obj);
            Assert.NotEqual(obj.GetHashCode(), other.GetHashCode());
            Assert.False(obj.Equals(other));
            Assert.False(other.Equals(obj));
            Assert.False(obj == other);
            Assert.False(other == obj);
            Assert.True(obj != other);
            Assert.True(other != obj);
        }
    }
}
