using System;
using System.Net;
using Orleans.Runtime;
using Orleans.Runtime.Host;
using Xunit;

// ReSharper disable ConvertToConstant.Local

namespace UnitTests.Management
{
    public class OrleansHostProgTests
    {
        readonly string hostname;

        public OrleansHostProgTests()
        {
            this.hostname = Dns.GetHostName();
        }

        [Fact, TestCategory("Functional"), TestCategory("Host"), TestCategory("CmdLineArgs")]
        public void OrleansHostParseNoArgs()
        {
            var expectedSiloName = this.hostname;
            var expectedSiloType = Silo.SiloType.Secondary;
            WindowsServerHost prog = new WindowsServerHost();
            Assert.True(prog.ParseArguments(new string[] { }));
            Assert.Equal(expectedSiloType, prog.SiloHost.Type);
            Assert.Equal(expectedSiloName, prog.SiloHost.Name);
        }

        [Fact, TestCategory("Functional"), TestCategory("Host"), TestCategory("CmdLineArgs")]
        public void OrleansHostParseUsageArg()
        {
            WindowsServerHost prog = new WindowsServerHost();
            Assert.False(prog.ParseArguments(new string[] { "/?" }));
            Assert.False(prog.ParseArguments(new string[] { "-?" }));
            Assert.False(prog.ParseArguments(new string[] { "/help" }));
        }

        [Fact, TestCategory("Functional"), TestCategory("Host"), TestCategory("CmdLineArgs")]
        public void OrleansHostParseUsageArgWithOtherArgs()
        {
            WindowsServerHost prog = new WindowsServerHost();
            Assert.False(prog.ParseArguments(new string[] { "/?", "SiloName", "CfgFile.xml" }));
            Assert.False(prog.ParseArguments(new string[] { "SiloName", "CfgFile.xml", "/?" }));
        }

        [Fact, TestCategory("Functional"), TestCategory("Host"), TestCategory("CmdLineArgs")]
        public void OrleansHostParseBadArguments()
        {
            WindowsServerHost prog = new WindowsServerHost();
            Assert.False(prog.ParseArguments(new string[] { "/xyz" }));
            Assert.False(prog.ParseArguments(new string[] { "/xyz", "/abc" }));
            Assert.False(prog.ParseArguments(new string[] { "/xyz", "/abc", "/123" }));
            Assert.False(prog.ParseArguments(new string[] { "/xyz", "/abc", "/123", "/456" }));
            Assert.False(prog.ParseArguments(new string[] { "DeploymentId=" }));
            Assert.False(prog.ParseArguments(new string[] { "DeploymentGroup=" }));
        }

        [Fact, TestCategory("Functional"), TestCategory("Host"), TestCategory("CmdLineArgs")]
        public void OrleansHostParseSiloNameArg()
        {
            var expectedSiloName = "MySilo";
            var expectedSiloType = Silo.SiloType.Secondary;
            WindowsServerHost prog = new WindowsServerHost();
            Assert.True(prog.ParseArguments(new string[] { expectedSiloName }));
            Assert.Equal(expectedSiloType, prog.SiloHost.Type);
            Assert.Equal(expectedSiloName, prog.SiloHost.Name);
        }

        [Fact, TestCategory("Functional"), TestCategory("Host"), TestCategory("CmdLineArgs")]
        public void OrleansHostParsePrimarySiloNameArg()
        {
            var expectedSiloName = "Primary";
            var expectedSiloType = Silo.SiloType.Primary;
            WindowsServerHost prog = new WindowsServerHost();
            Assert.True(prog.ParseArguments(new string[] { expectedSiloName }));
            prog.Init();
            Assert.Equal(expectedSiloType, prog.SiloHost.Type);
            Assert.Equal(expectedSiloName, prog.SiloHost.Name);
        }

        [Fact, TestCategory("Functional"), TestCategory("Host"), TestCategory("CmdLineArgs")]
        public void OrleansHostParseConfigFileArg()
        {
            var expectedSiloName = "MySilo";
            var expectedSiloType = Silo.SiloType.Secondary;
            var expectedConfigFileName = @"OrleansConfiguration.xml";
            WindowsServerHost prog = new WindowsServerHost();
            Assert.True(prog.ParseArguments(new string[] { expectedSiloName, expectedConfigFileName }));
            Assert.Equal(expectedSiloType, prog.SiloHost.Type);
            Assert.Equal(expectedSiloName, prog.SiloHost.Name);
            Assert.Equal(expectedConfigFileName, prog.SiloHost.ConfigFileName);
        }

        [Fact, TestCategory("Functional"), TestCategory("Host"), TestCategory("CmdLineArgs")]
        public void OrleansHostParseDeploymentIdArg()
        {
            var expectedSiloName = this.hostname;
            var expectedDeploymentId = Guid.NewGuid().ToString("D");
            WindowsServerHost prog = new WindowsServerHost();
            Assert.True(prog.ParseArguments(new string[] { "DeploymentId=" + expectedDeploymentId }));
            Assert.Equal(expectedSiloName, prog.SiloHost.Name);
            Assert.Equal(expectedDeploymentId, prog.SiloHost.DeploymentId);
        }

        [Fact, TestCategory("Functional"), TestCategory("Host"), TestCategory("CmdLineArgs")]
        public void OrleansHostParseDeploymentGroupArg()
        {
            var expectedSiloName = this.hostname;
            var expectedDeploymentId = Guid.NewGuid().ToString("D");
            WindowsServerHost prog = new WindowsServerHost();
            Assert.True(prog.ParseArguments(new string[] { "DeploymentGroup=" + expectedDeploymentId }));
            Assert.Equal(expectedSiloName, prog.SiloHost.Name);
           Assert.Null(prog.SiloHost.DeploymentId);
        }

        [Fact, TestCategory("Functional"), TestCategory("Host"), TestCategory("CmdLineArgs")]
        public void OrleansHostParseDeploymentGroupArgFormats()
        {
            var expectedDeploymentId = Guid.NewGuid().ToString("N");
            WindowsServerHost prog = new WindowsServerHost();
            Assert.True(prog.ParseArguments(new string[] { "DeploymentId=" + expectedDeploymentId }));
            Assert.Equal(expectedDeploymentId, prog.SiloHost.DeploymentId);

            prog = new WindowsServerHost();
            expectedDeploymentId = Guid.NewGuid().ToString("D");
            Assert.True(prog.ParseArguments(new string[] { "DeploymentId=" + expectedDeploymentId }));
            Assert.Equal(expectedDeploymentId, prog.SiloHost.DeploymentId);

            prog = new WindowsServerHost();
            expectedDeploymentId = Guid.NewGuid().ToString("B");
            Assert.True(prog.ParseArguments(new string[] { "DeploymentId=" + expectedDeploymentId }));
            Assert.Equal(expectedDeploymentId, prog.SiloHost.DeploymentId);

            prog = new WindowsServerHost();
            expectedDeploymentId = Guid.NewGuid().ToString("P");
            Assert.True(prog.ParseArguments(new string[] { "DeploymentId=" + expectedDeploymentId }));
            Assert.Equal(expectedDeploymentId, prog.SiloHost.DeploymentId);

            prog = new WindowsServerHost();
            expectedDeploymentId = Guid.NewGuid().ToString("X");
            Assert.True(prog.ParseArguments(new string[] { "DeploymentId=" + expectedDeploymentId }));
            Assert.Equal(expectedDeploymentId, prog.SiloHost.DeploymentId);

            prog = new WindowsServerHost();
            expectedDeploymentId = Guid.NewGuid().ToString("");
            Assert.True(prog.ParseArguments(new string[] { "DeploymentId=" + expectedDeploymentId }));
            Assert.Equal(expectedDeploymentId, prog.SiloHost.DeploymentId);

            prog = new WindowsServerHost();
            expectedDeploymentId = Guid.NewGuid().ToString();
            Assert.True(prog.ParseArguments(new string[] { "DeploymentId=" + expectedDeploymentId }));
            Assert.Equal(expectedDeploymentId, prog.SiloHost.DeploymentId);
        }

        [Fact, TestCategory("Functional"), TestCategory("Host"), TestCategory("CmdLineArgs")]
        public void OrleansHostParseDeploymentGroupLastArgWins()
        {
            var expectedDeploymentId1 = Guid.NewGuid();
            var expectedDeploymentId2 = Guid.NewGuid();
            WindowsServerHost prog = new WindowsServerHost();
            Assert.True(prog.ParseArguments(new string[] { 
                "DeploymentId=" + expectedDeploymentId1,
                "DeploymentId=" + expectedDeploymentId2,
                "DeploymentGroup=" + expectedDeploymentId1,
                "DeploymentGroup=" + expectedDeploymentId2 
            }));
            Assert.Equal(expectedDeploymentId2.ToString(), prog.SiloHost.DeploymentId);
        }

        [Fact, TestCategory("Functional"), TestCategory("Host"), TestCategory("CmdLineArgs")]
        public void OrleansHostParseMultipleArgs()
        {
            var expectedSiloName = "MySilo";
            var expectedConfigFileName = @"OrleansConfiguration.xml";
            var expectedDeploymentId = Guid.NewGuid();
            WindowsServerHost prog = new WindowsServerHost();
            Assert.True(prog.ParseArguments(new string[] { 
                expectedSiloName, 
                expectedConfigFileName, 
                "DeploymentId=" + expectedDeploymentId
            }));
            Assert.Equal(expectedSiloName, prog.SiloHost.Name);
            Assert.Equal(expectedConfigFileName, prog.SiloHost.ConfigFileName);
            Assert.Equal(expectedDeploymentId.ToString(), prog.SiloHost.DeploymentId);
        }
    }
}

// ReSharper restore ConvertToConstant.Local
