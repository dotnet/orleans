using System;
using System.Text;
using System.Collections.Generic;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace UnitTests
{
    /// <summary>
    /// Summary description for UnitTestExecutionOrder
    /// </summary>
    [TestClass]
    public class UnitTestExecutionOrder
    {
        static UnitTestExecutionOrder()
        {
            Console.WriteLine("Inside static constructor");
        }

        public UnitTestExecutionOrder()
        {
            Console.WriteLine("Inside instance constructor. Instance={0}", this);
        }

        /// <summary>
        ///Gets or sets the test context which provides
        ///information about and functionality for the current test run.
        ///</summary>
        public TestContext TestContext
        {
            get
            {
                return testContextInstance;
            }
            set
            {
                testContextInstance = value;
                Console.WriteLine("Inside TextContext setter TestName={0}", testContextInstance.TestName);
            }
        }
        private static TestContext testContextInstance;

#if DEBUG
        [AssemblyInitialize]
        public static void AssemblyInit(TestContext testContext)
        {
            testContextInstance = testContext;
            testContextInstance.WriteLine("Inside AssemblyInit TestName={0}", testContext.TestName);
        }
        [AssemblyCleanup]
        public static void AssemblyCleanup()
        {
            testContextInstance.WriteLine("Inside AssemblyCleanup");
        }
#endif

        // Use ClassInitialize to run code before running the first test in the class
        [ClassInitialize]
        public static void ClassInitialize(TestContext testContext)
        {
            testContextInstance = testContext;
            testContextInstance.WriteLine("Inside ClassInitialize TestName={0}", testContext.TestName);
        }

        // Use ClassCleanup to run code after all tests in a class have run
        [ClassCleanup]
        public static void ClassCleanup()
        {
            testContextInstance.WriteLine("Inside ClassCleanup");
        }

        // Use TestInitialize to run code before running each test 
        [TestInitialize]
        public void TestInitialize()
        {
            Console.WriteLine("Inside TestInitialize TestName={0}", testContextInstance.TestName);
        }

        // Use TestCleanup to run code after each test has run
        [TestCleanup]
        public void TestCleanup()
        {
            Console.WriteLine("Inside TestCleanup TestName={0}", testContextInstance.TestName);
        }

        [TestMethod, TestCategory("UnitTestExecutionOrder")]
        public void TestMethod1()
        {
            Console.WriteLine("Inside TestMethod1 TestName={0}", testContextInstance.TestName);
        }

        [TestMethod, TestCategory("UnitTestExecutionOrder")]
        public void TestMethod2()
        {
            Console.WriteLine("Inside TestMethod2 TestName={0}", testContextInstance.TestName);
        }

        public override string ToString()
        {
            return this.GetType().Name + "#" + this.GetHashCode();
        }
    }
}
