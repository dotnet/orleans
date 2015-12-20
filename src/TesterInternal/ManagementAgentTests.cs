using Microsoft.VisualStudio.TestTools.UnitTesting;
using Orleans.Runtime;

namespace UnitTests
{
    [TestClass]
    public class ManagementAgentTests
    {
        [TestMethod, TestCategory("Functional"), TestCategory("Management")]
        public void SystemStatusEquals()
        {
            SystemStatus same1 = SystemStatus.Terminated;
            SystemStatus same2 = SystemStatus.Terminated;
            SystemStatus other = SystemStatus.Stopping;

            CheckEquals(same1, same2);
            CheckNotEquals(same1, other);
            CheckNotEquals(same2, other);
        }

        [TestMethod, TestCategory("Functional"), TestCategory("Management")]
        public void SystemStatusCurrent()
        {
            SystemStatus.Current = null;
            Assert.IsTrue(SystemStatus.Current == SystemStatus.Unknown);

            SystemStatus.Current = SystemStatus.Starting;
            Assert.IsTrue(SystemStatus.Current == SystemStatus.Starting);
            
            SystemStatus.Current = SystemStatus.Running;
            Assert.IsTrue(SystemStatus.Current == SystemStatus.Running);
        }

        //[TestMethod]
        //public void CheckEventChannelEnvSetup()
        //{
        //    bool found = ManagementBusConnector.CheckEventChannelEnvSetup();
        //    Console.WriteLine("CheckEventChannelEnvSetup=" + found);
        //}

#region Test support methods
        private static void CheckEquals(object obj, object other)
        {
            Assert.IsNotNull(obj);
            Assert.IsNotNull(other);
            Assert.AreEqual(obj, other);
            Assert.AreEqual(other, obj);
            Assert.AreEqual(obj.GetHashCode(), other.GetHashCode());
            Assert.IsTrue(obj.Equals(other));
            Assert.IsTrue(other.Equals(obj));
            Assert.IsTrue(obj == other);
            Assert.IsTrue(other == obj);
            Assert.IsFalse(obj != other);
            Assert.IsFalse(other != obj);
        }

        private static void CheckNotEquals(object obj, object other)
        {
            Assert.IsNotNull(obj);
            Assert.IsNotNull(other);
            Assert.AreNotEqual(obj, other);
            Assert.AreNotEqual(other, obj);
            Assert.AreNotEqual(obj.GetHashCode(), other.GetHashCode());
            Assert.IsFalse(obj.Equals(other));
            Assert.IsFalse(other.Equals(obj));
            Assert.IsFalse(obj == other);
            Assert.IsFalse(other == obj);
            Assert.IsTrue(obj != other);
            Assert.IsTrue(other != obj);
        }
#endregion
    }
}
