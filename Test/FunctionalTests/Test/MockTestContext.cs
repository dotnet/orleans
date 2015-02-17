using System;
using System.Collections;
using System.Collections.Generic;
using System.Data;
using System.Data.Common;
using System.IO;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Orleans.Runtime;
using Orleans.Runtime.Configuration;

namespace UnitTests
{
    public class MockTestContext : TestContext
    {
        private readonly IDictionary props;

        public MockTestContext()
        {
            props = new Dictionary<object, object>
            {
                { "DeploymentDirectory", Path.GetFullPath(@"..\..\..\UnitTests") },
                { "TestName", Path.GetFullPath(@"Standaline-Debug-Harness") }
            };

            ClientConfiguration cfg = ClientConfiguration.StandardLoad();
            TraceLogger.Initialize(cfg);

            TraceLogger.AddTraceLevelOverride("Storage", Logger.Severity.Verbose3);
            TraceLogger.AddTraceLevelOverride("Membership", Logger.Severity.Verbose3);
            TraceLogger.AddTraceLevelOverride("Reminder", Logger.Severity.Verbose3);
        }

        #region TestContext methods
        public override void WriteLine(string format, params object[] args)
        {
            Console.WriteLine(format, args);
        }

        public override void AddResultFile(string fileName)
        {
            WarnIgnoringCall("AddResultFile");
        }

        public override void BeginTimer(string timerName)
        {
            WarnIgnoringCall("BeginTimer");
        }

        public override void EndTimer(string timerName)
        {
            WarnIgnoringCall("EndTimer");
        }

        public override IDictionary Properties
        {
            get { return props; }
        }

        public override DataRow DataRow
        {
            get { throw new NotImplementedException("DataRow"); }
        }

        public override DbConnection DataConnection
        {
            get { throw new NotImplementedException("DataConnection"); }
        }

#pragma warning disable 809
        [Obsolete(@"Deprecated. Use TestContext.TestRunDirectory instead.")]
        public override string TestDir
        {
            get { throw new InvalidOperationException(@"Deprecated. Use TestContext.TestRunDirectory instead."); }
        }
        [Obsolete(@"Deprecated. Use TestContext.DeploymentDirectory instead.")]
        public override string TestDeploymentDir
        {
            get { throw new InvalidOperationException(@"Deprecated. Use TestContext.DeploymentDirectory instead."); }
        }
        [Obsolete(@"Deprecated. Use TestContext.TestRunResultsDirectory instead.")]
        public override string TestLogsDir
        {
            get { throw new InvalidOperationException(@"Deprecated. Use TestContext.TestRunResultsDirectory instead."); }
        }
#pragma warning restore 809

        #endregion

        private void WarnIgnoringCall(string name)
        {
            Console.WriteLine("Warning: Ignoring call {0} to TestContext", name);
        }
    }
}
