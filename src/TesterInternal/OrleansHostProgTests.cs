using System;
using System.Net;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Orleans.Runtime;
using Orleans.Runtime.Host;

// ReSharper disable ConvertToConstant.Local

namespace UnitTests.Management
{
    [TestClass]
    [DeploymentItem("OrleansConfiguration.xml")]
    [DeploymentItem("ClientConfiguration.xml")]
    public class OrleansHostProgTests
    {
        readonly string hostname;

        public OrleansHostProgTests()
        {
            this.hostname = Dns.GetHostName();
        }

        [TestMethod, TestCategory("Functional"), TestCategory("Host"), TestCategory("CmdLineArgs")]
        public void OrleansHostParseNoArgs()
        {
            var expectedSiloName = this.hostname;
            var expectedSiloType = Silo.SiloType.Secondary;
            WindowsServerHost prog = new WindowsServerHost();
            Assert.IsTrue(prog.ParseArguments(new string[] { }));
            Assert.AreEqual(expectedSiloType, prog.SiloHost.Type);
            Assert.AreEqual(expectedSiloName, prog.SiloHost.Name);
        }

        [TestMethod, TestCategory("Functional"), TestCategory("Host"), TestCategory("CmdLineArgs")]
        public void OrleansHostParseUsageArg()
        {
            WindowsServerHost prog = new WindowsServerHost();
            Assert.IsFalse(prog.ParseArguments(new string[] { "/?" }));
            Assert.IsFalse(prog.ParseArguments(new string[] { "-?" }));
            Assert.IsFalse(prog.ParseArguments(new string[] { "/help" }));
        }

        [TestMethod, TestCategory("Functional"), TestCategory("Host"), TestCategory("CmdLineArgs")]
        public void OrleansHostParseUsageArgWithOtherArgs()
        {
            WindowsServerHost prog = new WindowsServerHost();
            Assert.IsFalse(prog.ParseArguments(new string[] { "/?", "SiloName", "CfgFile.xml" }));
            Assert.IsFalse(prog.ParseArguments(new string[] { "SiloName", "CfgFile.xml", "/?" }));
        }

        [TestMethod, TestCategory("Functional"), TestCategory("Host"), TestCategory("CmdLineArgs")]
        public void OrleansHostParseBadArguments()
        {
            WindowsServerHost prog = new WindowsServerHost();
            Assert.IsFalse(prog.ParseArguments(new string[] { "/xyz" }));
            Assert.IsFalse(prog.ParseArguments(new string[] { "/xyz", "/abc" }));
            Assert.IsFalse(prog.ParseArguments(new string[] { "/xyz", "/abc", "/123" }));
            Assert.IsFalse(prog.ParseArguments(new string[] { "/xyz", "/abc", "/123", "/456" }));
            Assert.IsFalse(prog.ParseArguments(new string[] { "DeploymentId=" }));
            Assert.IsFalse(prog.ParseArguments(new string[] { "DeploymentGroup=" }));
        }

        [TestMethod, TestCategory("Functional"), TestCategory("Host"), TestCategory("CmdLineArgs")]
        public void OrleansHostParseSiloNameArg()
        {
            var expectedSiloName = "MySilo";
            var expectedSiloType = Silo.SiloType.Secondary;
            WindowsServerHost prog = new WindowsServerHost();
            Assert.IsTrue(prog.ParseArguments(new string[] { expectedSiloName }));
            Assert.AreEqual(expectedSiloType, prog.SiloHost.Type);
            Assert.AreEqual(expectedSiloName, prog.SiloHost.Name);
        }

        [TestMethod, TestCategory("Functional"), TestCategory("Host"), TestCategory("CmdLineArgs")]
        public void OrleansHostParsePrimarySiloNameArg()
        {
            var expectedSiloName = "Primary";
            var expectedSiloType = Silo.SiloType.Primary;
            WindowsServerHost prog = new WindowsServerHost();
            Assert.IsTrue(prog.ParseArguments(new string[] { expectedSiloName }));
            prog.Init();
            Assert.AreEqual(expectedSiloType, prog.SiloHost.Type);
            Assert.AreEqual(expectedSiloName, prog.SiloHost.Name);
        }

        [TestMethod, TestCategory("Functional"), TestCategory("Host"), TestCategory("CmdLineArgs")]
        public void OrleansHostParseConfigFileArg()
        {
            var expectedSiloName = "MySilo";
            var expectedSiloType = Silo.SiloType.Secondary;
            var expectedConfigFileName = @"OrleansConfiguration.xml";
            WindowsServerHost prog = new WindowsServerHost();
            Assert.IsTrue(prog.ParseArguments(new string[] { expectedSiloName, expectedConfigFileName }));
            Assert.AreEqual(expectedSiloType, prog.SiloHost.Type);
            Assert.AreEqual(expectedSiloName, prog.SiloHost.Name);
            Assert.AreEqual(expectedConfigFileName, prog.SiloHost.ConfigFileName);
        }

        [TestMethod, TestCategory("Functional"), TestCategory("Host"), TestCategory("CmdLineArgs")]
        public void OrleansHostParseDeploymentIdArg()
        {
            var expectedSiloName = this.hostname;
            var expectedDeploymentId = Guid.NewGuid().ToString("D");
            WindowsServerHost prog = new WindowsServerHost();
            Assert.IsTrue(prog.ParseArguments(new string[] { "DeploymentId=" + expectedDeploymentId }));
            Assert.AreEqual(expectedSiloName, prog.SiloHost.Name);
            Assert.AreEqual(expectedDeploymentId, prog.SiloHost.DeploymentId);
        }

        [TestMethod, TestCategory("Functional"), TestCategory("Host"), TestCategory("CmdLineArgs")]
        public void OrleansHostParseDeploymentGroupArg()
        {
            var expectedSiloName = this.hostname;
            var expectedDeploymentId = Guid.NewGuid().ToString("D");
            WindowsServerHost prog = new WindowsServerHost();
            Assert.IsTrue(prog.ParseArguments(new string[] { "DeploymentGroup=" + expectedDeploymentId }));
            Assert.AreEqual(expectedSiloName, prog.SiloHost.Name);
            Assert.IsNull(prog.SiloHost.DeploymentId);
        }

        [TestMethod, TestCategory("Functional"), TestCategory("Host"), TestCategory("CmdLineArgs")]
        public void OrleansHostParseDeploymentGroupArgFormats()
        {
            var expectedDeploymentId = Guid.NewGuid().ToString("N");
            WindowsServerHost prog = new WindowsServerHost();
            Assert.IsTrue(prog.ParseArguments(new string[] { "DeploymentId=" + expectedDeploymentId }));
            Assert.AreEqual(expectedDeploymentId, prog.SiloHost.DeploymentId);

            prog = new WindowsServerHost();
            expectedDeploymentId = Guid.NewGuid().ToString("D");
            Assert.IsTrue(prog.ParseArguments(new string[] { "DeploymentId=" + expectedDeploymentId }));
            Assert.AreEqual(expectedDeploymentId, prog.SiloHost.DeploymentId);

            prog = new WindowsServerHost();
            expectedDeploymentId = Guid.NewGuid().ToString("B");
            Assert.IsTrue(prog.ParseArguments(new string[] { "DeploymentId=" + expectedDeploymentId }));
            Assert.AreEqual(expectedDeploymentId, prog.SiloHost.DeploymentId);

            prog = new WindowsServerHost();
            expectedDeploymentId = Guid.NewGuid().ToString("P");
            Assert.IsTrue(prog.ParseArguments(new string[] { "DeploymentId=" + expectedDeploymentId }));
            Assert.AreEqual(expectedDeploymentId, prog.SiloHost.DeploymentId);

            prog = new WindowsServerHost();
            expectedDeploymentId = Guid.NewGuid().ToString("X");
            Assert.IsTrue(prog.ParseArguments(new string[] { "DeploymentId=" + expectedDeploymentId }));
            Assert.AreEqual(expectedDeploymentId, prog.SiloHost.DeploymentId);

            prog = new WindowsServerHost();
            expectedDeploymentId = Guid.NewGuid().ToString("");
            Assert.IsTrue(prog.ParseArguments(new string[] { "DeploymentId=" + expectedDeploymentId }));
            Assert.AreEqual(expectedDeploymentId, prog.SiloHost.DeploymentId);

            prog = new WindowsServerHost();
            expectedDeploymentId = Guid.NewGuid().ToString();
            Assert.IsTrue(prog.ParseArguments(new string[] { "DeploymentId=" + expectedDeploymentId }));
            Assert.AreEqual(expectedDeploymentId, prog.SiloHost.DeploymentId);
        }

        [TestMethod, TestCategory("Functional"), TestCategory("Host"), TestCategory("CmdLineArgs")]
        public void OrleansHostParseDeploymentGroupLastArgWins()
        {
            var expectedDeploymentId1 = Guid.NewGuid();
            var expectedDeploymentId2 = Guid.NewGuid();
            WindowsServerHost prog = new WindowsServerHost();
            Assert.IsTrue(prog.ParseArguments(new string[] { 
                "DeploymentId=" + expectedDeploymentId1,
                "DeploymentId=" + expectedDeploymentId2,
                "DeploymentGroup=" + expectedDeploymentId1,
                "DeploymentGroup=" + expectedDeploymentId2 
            }));
            Assert.AreEqual(expectedDeploymentId2.ToString(), prog.SiloHost.DeploymentId);
        }

        [TestMethod, TestCategory("Functional"), TestCategory("Host"), TestCategory("CmdLineArgs")]
        public void OrleansHostParseMultipleArgs()
        {
            var expectedSiloName = "MySilo";
            var expectedConfigFileName = @"OrleansConfiguration.xml";
            var expectedDeploymentId = Guid.NewGuid();
            WindowsServerHost prog = new WindowsServerHost();
            Assert.IsTrue(prog.ParseArguments(new string[] { 
                expectedSiloName, 
                expectedConfigFileName, 
                "DeploymentId=" + expectedDeploymentId
            }));
            Assert.AreEqual(expectedSiloName, prog.SiloHost.Name);
            Assert.AreEqual(expectedConfigFileName, prog.SiloHost.ConfigFileName);
            Assert.AreEqual(expectedDeploymentId.ToString(), prog.SiloHost.DeploymentId);
        }
    }
}

// ReSharper restore ConvertToConstant.Local
