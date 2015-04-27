/*
Project Orleans Cloud Service SDK ver. 1.0
 
Copyright (c) Microsoft Corporation
 
All rights reserved.
 
MIT License

Permission is hereby granted, free of charge, to any person obtaining a copy of this software and 
associated documentation files (the ""Software""), to deal in the Software without restriction,
including without limitation the rights to use, copy, modify, merge, publish, distribute, sublicense,
and/or sell copies of the Software, and to permit persons to whom the Software is furnished to do so,
subject to the following conditions:

The above copyright notice and this permission notice shall be included in all copies or substantial portions of the Software.

THE SOFTWARE IS PROVIDED *AS IS*, WITHOUT WARRANTY OF ANY KIND, EXPRESS OR IMPLIED, INCLUDING BUT NOT LIMITED TO
THE WARRANTIES OF MERCHANTABILITY, FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE AUTHORS
OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER LIABILITY, WHETHER IN AN ACTION OF CONTRACT,
TORT OR OTHERWISE, ARISING FROM, OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE SOFTWARE.
*/

﻿using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Net;
using Orleans.Runtime;
using Orleans.Runtime.Configuration;

namespace UnitTests.Tester
{
    [Serializable]
    public class SiloHandle
    {
        public Silo Silo { get; set; }
        public AppDomain AppDomain { get; set; }
        public UnitTestSiloOptions Options { get; set; }
        public string Name { get; set; }
        public Process Process { get; set; }
        public string MachineName { get; set; }
        public IPEndPoint Endpoint { get; set; }

        public override string ToString()
        {
            return String.Format("SiloHandle:{0}", Endpoint);
        }
    }

    public class UnitTestSiloOptions
    {
        public bool StartFreshOrleans { get; set; }
        public bool StartPrimary { get; set; }
        public bool StartSecondary { get; set; }
        public bool StartClient { get; set; }

        public FileInfo SiloConfigFile { get; set; }

        public bool PickNewDeploymentId { get; set; }
        public bool PropagateActivityId { get; set; }
        public int BasePort { get; set; }
        public string MachineName { get; set; }
        public int LargeMessageWarningThreshold { get; set; }
        public GlobalConfiguration.LivenessProviderType LivenessType { get; set; }
        public bool ParallelStart { get; set; }

        public UnitTestSiloOptions()
        {
            // all defaults except:
            StartFreshOrleans = true;
            StartPrimary = true;
            StartSecondary = true;
            StartClient = true;
            PickNewDeploymentId = true;
            BasePort = -1; // use default from configuration file
            MachineName = ".";
            LivenessType = GlobalConfiguration.LivenessProviderType.MembershipTableGrain;
            SiloConfigFile = new FileInfo("OrleansConfigurationForUnitTests.xml");
            ParallelStart = false;
        }

        public UnitTestSiloOptions Copy()
        {
            return new UnitTestSiloOptions
            {
                StartFreshOrleans = StartFreshOrleans,
                StartPrimary = StartPrimary,
                StartSecondary = StartSecondary,
                StartClient = StartClient,
                SiloConfigFile = SiloConfigFile,
                PickNewDeploymentId = PickNewDeploymentId,
                BasePort = BasePort,
                MachineName = MachineName,
                LargeMessageWarningThreshold = LargeMessageWarningThreshold,
                PropagateActivityId = PropagateActivityId,
                LivenessType = LivenessType,
                ParallelStart = ParallelStart
            };
        }
    }

    public class UnitTestClientOptions
    {
        public FileInfo ClientConfigFile { get; set; }
        public TimeSpan ResponseTimeout { get; set; }
        public bool ProxiedGateway { get; set; }
        public List<IPEndPoint> Gateways { get; set; }
        public int PreferedGatewayIndex { get; set; }
        public bool PropagateActivityId { get; set; }

        public UnitTestClientOptions()
        {
            // all defaults except:
            ResponseTimeout = TimeSpan.FromMilliseconds(-1);
            PreferedGatewayIndex = -1;
            ClientConfigFile = new FileInfo("ClientConfigurationForUnitTests.xml");
        }

        public UnitTestClientOptions Copy()
        {
            return new UnitTestClientOptions
            {
                ResponseTimeout = ResponseTimeout,
                ProxiedGateway = ProxiedGateway,
                Gateways = Gateways,
                PreferedGatewayIndex = PreferedGatewayIndex,
                PropagateActivityId = PropagateActivityId,
                ClientConfigFile = ClientConfigFile,
            };
        }
    }
}
