using System;
using System.IO;
using System.Text;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Orleans.TestingHost;

namespace UnitTests.Tester
{
    /// <summary>
    /// Keep this class as a bridge to the OrleansTestingSilo package, 
    /// because it gives a convenient place to declare all the additional
    /// deployment items required by tests 
    /// - such as the TestGrain assemblies, the client and server config files.
    /// </summary>
    [DeploymentItem("OrleansConfigurationForTesting.xml")]
    [DeploymentItem("ClientConfigurationForTesting.xml")]
    [DeploymentItem("TestGrainInterfaces.dll")]
    [DeploymentItem("TestGrains.dll")]
    [DeploymentItem("OrleansCodeGenerator.dll")]
    [DeploymentItem("TestInternalGrainInterfaces.dll")]
    [DeploymentItem("TestInternalGrains.dll")]
    public class UnitTestSiloHost : TestingSiloHost
    {
        public UnitTestSiloHost() // : base()
        {
        }
        public UnitTestSiloHost(TestingSiloOptions siloOptions)
            : base(siloOptions)
        {
        }
        public UnitTestSiloHost(TestingSiloOptions siloOptions, TestingClientOptions clientOptions)
            : base(siloOptions, clientOptions)
        {
        }

        public static void CheckForAzureStorage()
        {
            bool usingLocalWAS = StorageTestConstants.UsingAzureLocalStorageEmulator;

            if (!usingLocalWAS)
            {
                string msg = "Tests are using Azure Cloud Storage, not local WAS emulator.";
                Console.WriteLine(msg);
                return;
            }

            //Starts the storage emulator if not started already and it exists (i.e. is installed).
            if (!StorageEmulator.TryStart())
            {
                string errorMsg = "Azure Storage Emulator could not be started.";
                Console.WriteLine(errorMsg);
                Assert.Inconclusive(errorMsg);
            }
        }

        protected static string DumpTestContext(TestContext context)
        {
            StringBuilder sb = new StringBuilder();
            sb.AppendFormat(@"TestName={0}", context.TestName).AppendLine();
            sb.AppendFormat(@"FullyQualifiedTestClassName={0}", context.FullyQualifiedTestClassName).AppendLine();
            sb.AppendFormat(@"CurrentTestOutcome={0}", context.CurrentTestOutcome).AppendLine();
            sb.AppendFormat(@"DeploymentDirectory={0}", context.DeploymentDirectory).AppendLine();
            sb.AppendFormat(@"TestRunDirectory={0}", context.TestRunDirectory).AppendLine();
            sb.AppendFormat(@"TestResultsDirectory={0}", context.TestResultsDirectory).AppendLine();
            sb.AppendFormat(@"ResultsDirectory={0}", context.ResultsDirectory).AppendLine();
            sb.AppendFormat(@"TestRunResultsDirectory={0}", context.TestRunResultsDirectory).AppendLine();
            sb.AppendFormat(@"Properties=[ ");
            foreach (var key in context.Properties.Keys)
            {
                sb.AppendFormat(@"{0}={1} ", key, context.Properties[key]);
            }
            sb.AppendFormat(@" ]").AppendLine();
            return sb.ToString();
        }

        protected static int GetRandomGrainId()
        {
            return random.Next();
        }
    }
}
