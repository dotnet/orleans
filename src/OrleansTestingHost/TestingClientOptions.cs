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

using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using Orleans.Runtime.Configuration;

namespace Orleans.TestingHost
{
    public class TestingClientOptions
    {
        public const string DEFAULT_CLIENT_CONFIG_FILE = "ClientConfigurationForTesting.xml";

        public FileInfo ClientConfigFile { get; set; }
        public TimeSpan ResponseTimeout { get; set; }
        public bool ProxiedGateway { get; set; }
        public List<IPEndPoint> Gateways { get; set; }
        public int PreferedGatewayIndex { get; set; }
        public bool PropagateActivityId { get; set; }
        public Action<ClientConfiguration> ConfigMutator { get; set; }

        public TestingClientOptions()
        {
            // all defaults except:
            ResponseTimeout = TimeSpan.FromMilliseconds(-1);
            PreferedGatewayIndex = -1;
            ClientConfigFile = new FileInfo(DEFAULT_CLIENT_CONFIG_FILE);
            ConfigMutator = configuration => { };
        }

        public TestingClientOptions Copy()
        {
            return new TestingClientOptions
            {
                ResponseTimeout = ResponseTimeout,
                ProxiedGateway = ProxiedGateway,
                Gateways = Gateways,
                PreferedGatewayIndex = PreferedGatewayIndex,
                PropagateActivityId = PropagateActivityId,
                ClientConfigFile = ClientConfigFile,
                ConfigMutator = ConfigMutator,
            };
        }
    }
}