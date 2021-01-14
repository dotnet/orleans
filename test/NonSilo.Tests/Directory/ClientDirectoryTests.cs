using System;
using System.Collections.Generic;
using System.Text;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using Orleans.Configuration;
using Orleans.Runtime;
using Orleans.Runtime.GrainDirectory;
using Xunit;

namespace NonSilo.Tests.Directory
{
    [TestCategory("BVT"), TestCategory("Directory")]
    public class ClientDirectoryTests
    {
        private readonly ILocalSiloDetails _localSiloDetails;
        private readonly SiloAddress _localSilo;
        private readonly IOptions<SiloMessagingOptions> _messagingOptions;
        private readonly ILoggerFactory _loggerFactory;

        public ClientDirectoryTests()
        {
            _localSiloDetails = Substitute.For<ILocalSiloDetails>();
            _localSilo = Silo("127.0.0.1:100@100");
            _localSiloDetails.SiloAddress.Returns(_localSilo);
            _localSiloDetails.DnsHostName.Returns("MyServer11");
            _localSiloDetails.Name.Returns(Guid.NewGuid().ToString("N"));

            _messagingOptions = Options.Create(new SiloMessagingOptions());
            _loggerFactory = NullLoggerFactory.Instance;
        }

        [Fact]
        public void TestOne()
        {
            var directory = new ClientDirectory(
                grainFactory: null,
                siloDetails: _localSiloDetails,
                messagingOptions: _messagingOptions,
                loggerFactory: _loggerFactory,
                clusterMembershipService: null,
                timerFactory: null,
                connectedClients: null);
            var testAccessor = new ClientDirectory.TestAccessor(directory);
            _ = testAccessor;
        }

        private static SiloAddress Silo(string value) => SiloAddress.FromParsableString(value);
    }
}
