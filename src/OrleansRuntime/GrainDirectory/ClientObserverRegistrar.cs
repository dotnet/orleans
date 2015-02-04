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
using System.Threading.Tasks;

using Orleans.Runtime.GrainDirectory;
using Orleans.Runtime.Messaging;

namespace Orleans.Runtime
{
    internal class ClientObserverRegistrar : SystemTarget, IClientObserverRegistrar
    {
        private readonly ILocalGrainDirectory grainDirectory;
        private readonly ISiloMessageCenter localMessageCenter;
        private readonly SiloAddress myAddress;

        internal ClientObserverRegistrar(SiloAddress myAddr, ISiloMessageCenter mc, ILocalGrainDirectory dir)
            : base(Constants.ClientObserverRegistrarId, myAddr)
        {
            grainDirectory = dir;
            localMessageCenter = mc;
            myAddress = myAddr;
        }

        #region IClientGateway Members

        /// <summary>
        /// Registers a client object on this gateway.
        /// </summary>
        public async Task<ActivationAddress> RegisterClientObserver(GrainId grainId, Guid clientId)
        {
            localMessageCenter.RecordProxiedGrain(grainId, clientId);
            var addr = ActivationAddress.NewActivationAddress(myAddress, grainId);
            await grainDirectory.RegisterAsync(addr);
            return addr;
        }

        /// <summary>
        /// Unregisters client object from all gateways.
        /// </summary>
        public async Task UnregisterClientObserver(GrainId target)
        {
            if (localMessageCenter.IsProxying)
            {
                localMessageCenter.RecordUnproxiedGrain(target);
            }
            await grainDirectory.DeleteGrain(target);
        }

        #endregion
    }
}


