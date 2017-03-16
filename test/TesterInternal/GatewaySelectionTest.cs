﻿//#define USE_SQL_SERVER
#if !NETSTANDARD_TODO
using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Threading.Tasks;
using Orleans;
using Orleans.Messaging;
using Orleans.Runtime;
using Orleans.Runtime.Configuration;
using Xunit;
using Xunit.Abstractions;

namespace UnitTests.MessageCenterTests
{
    public class GatewaySelectionTest
    {
        protected readonly ITestOutputHelper output;

        protected static readonly List<Uri> gatewayAddressUris = new[]
        {
            new Uri("gwy.tcp://127.0.0.1:1/0"),
            new Uri("gwy.tcp://127.0.0.1:2/0"),
            new Uri("gwy.tcp://127.0.0.1:3/0"),
            new Uri("gwy.tcp://127.0.0.1:4/0")
        }.ToList();
        
        public GatewaySelectionTest(ITestOutputHelper output)
        {
            this.output = output;
        }

        [Fact, TestCategory("BVT"), TestCategory("Functional"), TestCategory("Gateway")]
        public void GatewaySelection()
        {
            var listProvider = new TestListProvider(gatewayAddressUris);
            Test_GatewaySelection(listProvider);
        }

        [Fact, TestCategory("SlowBVT"), TestCategory("Functional"), TestCategory("Gateway")]
        public void GatewaySelection_ClientInit_EmptyList()
        {
            var cfg = new ClientConfiguration();
            cfg.Gateways = null;
            bool failed = false;
            IDisposable client = null;
            try
            {
                new ClientBuilder().UseConfiguration(cfg).Build();
            }
            catch (Exception exc)
            {
                output.WriteLine(exc.ToString());
                failed = true;
            }
            finally
            {
                client?.Dispose();
            }
            Assert.True(failed, "GatewaySelection_EmptyList failed as GatewayManager did not throw on empty Gateway list.");

            // Note: This part of the test case requires a silo local to be running in order to work successfully.

            //var listProvider = new TestListProvider(gatewayAddressUris);
            //cfg.Gateways = listProvider.GetGateways().Select(uri =>
            //{
            //    return new IPEndPoint(IPAddress.Parse(uri.Host), uri.Port);
            //}).ToList();
            //Client.Initialize(cfg);
        }

        protected void Test_GatewaySelection(IGatewayListProvider listProvider)
        {
            IList<Uri> gatewayUris = listProvider.GetGateways().GetResult();
            Assert.True(gatewayUris.Count > 0, $"Found some gateways. Data = {Utils.EnumerableToString(gatewayUris)}");

            var gatewayEndpoints = gatewayUris.Select(uri =>
            {
                return new IPEndPoint(IPAddress.Parse(uri.Host), uri.Port);
            }).ToList();

            var cfg = new ClientConfiguration
            {
                Gateways = gatewayEndpoints
            };
            var gatewayManager = new GatewayManager(cfg, listProvider);

            var counts = new int[4];

            for (int i = 0; i < 2300; i++)
            {
                var ip = gatewayManager.GetLiveGateway();
                var addr = IPAddress.Parse(ip.Host);
                Assert.Equal(IPAddress.Loopback, addr);  // "Incorrect IP address returned for gateway"
                Assert.True((0 < ip.Port) && (ip.Port < 5), "Incorrect IP port returned for gateway");
                counts[ip.Port - 1]++;
            }

            // The following needed to be changed as the gateway manager now round-robins through the available gateways, rather than
            // selecting randomly based on load numbers.
            //Assert.True((500 < counts[0]) && (counts[0] < 1500), "Gateway selection is incorrectly skewed");
            //Assert.True((500 < counts[1]) && (counts[1] < 1500), "Gateway selection is incorrectly skewed");
            //Assert.True((125 < counts[2]) && (counts[2] < 375), "Gateway selection is incorrectly skewed");
            //Assert.True((25 < counts[3]) && (counts[3] < 75), "Gateway selection is incorrectly skewed");
            //Assert.True((287 < counts[0]) && (counts[0] < 1150), "Gateway selection is incorrectly skewed");
            //Assert.True((287 < counts[1]) && (counts[1] < 1150), "Gateway selection is incorrectly skewed");
            //Assert.True((287 < counts[2]) && (counts[2] < 1150), "Gateway selection is incorrectly skewed");
            //Assert.True((287 < counts[3]) && (counts[3] < 1150), "Gateway selection is incorrectly skewed");

            int low = 2300 / 4;
            int up = 2300 / 4;
            Assert.True((low <= counts[0]) && (counts[0] <= up), "Gateway selection is incorrectly skewed. " + counts[0]);
            Assert.True((low <= counts[1]) && (counts[1] <= up), "Gateway selection is incorrectly skewed. " + counts[1]);
            Assert.True((low <= counts[2]) && (counts[2] <= up), "Gateway selection is incorrectly skewed. " + counts[2]);
            Assert.True((low <= counts[3]) && (counts[3] <= up), "Gateway selection is incorrectly skewed. " + counts[3]);
        }

        private class TestListProvider : IGatewayListProvider
        {
            private readonly IList<Uri> list;

            public TestListProvider(List<Uri> gatewayUris)
            {
                list = gatewayUris;
            }

#region Implementation of IGatewayListProvider

            public Task<IList<Uri>> GetGateways()
            {
                return Task.FromResult(list);
            }

            public TimeSpan MaxStaleness
            {
                get { return TimeSpan.FromMinutes(1); }
            }

            public bool IsUpdatable
            {
                get { return false; }
            }
            public Task InitializeGatewayListProvider(ClientConfiguration clientConfiguration, Logger logger)
            {
                return TaskDone.Done;
            }

#endregion


        }
    }
}
#endif