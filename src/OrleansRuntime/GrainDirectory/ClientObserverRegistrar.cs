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
﻿using System.Linq.Expressions;
﻿using System.Threading.Tasks;
using Orleans.Runtime.Configuration;
using Orleans.Runtime.GrainDirectory;
using Orleans.Runtime.Messaging;
using Orleans.Runtime.Scheduler;

namespace Orleans.Runtime
{
    internal class ClientObserverRegistrar : SystemTarget, IClientObserverRegistrar
    {
        private readonly ILocalGrainDirectory grainDirectory;
        private readonly ISiloMessageCenter localMessageCenter;
        private readonly SiloAddress myAddress;
        private readonly OrleansTaskScheduler localScheduler;
        private readonly ClusterConfiguration orleansConfig;
        private readonly TraceLogger logger;
        private GrainTimer clientRefreshTimer;
        private Gateway gateway;
       

        internal ClientObserverRegistrar(SiloAddress myAddr, ISiloMessageCenter mc, ILocalGrainDirectory dir, OrleansTaskScheduler scheduler, ClusterConfiguration config)
            : base(Constants.ClientObserverRegistrarId, myAddr)
        {
            grainDirectory = dir;
            localMessageCenter = mc;
            myAddress = myAddr;
            localScheduler = scheduler;
            orleansConfig = config;
            logger = TraceLogger.GetLogger(typeof(ClientObserverRegistrar).Name);
        }

        internal void SetGateway(Gateway gateway)
        {
            this.gateway = gateway;
        }

        public Task Start()
        {
            var random = new SafeRandom();
            var randomOffset = random.NextTimeSpan(orleansConfig.Globals.ClientRegistrationRefresh);
            clientRefreshTimer = GrainTimer.FromTaskCallback(
                    OnClientRefreshTimer, 
                    null, 
                    randomOffset, 
                    orleansConfig.Globals.ClientRegistrationRefresh, 
                    "ClientObserverRegistrar.ClientRefreshTimer");
            clientRefreshTimer.Start();
            return TaskDone.Done;
        }

        internal void ClientAdded(GrainId clientId)
        {
            // Use a ActivationId that is hashed from clientId, and not random ActivationId.
            // That way, when we refresh it in the directiry, its the same one.
            var addr = GetClientActivationAddress(clientId);
            localScheduler.QueueTask(
                () => grainDirectory.RegisterAsync(addr), this.SchedulingContext)
                    .LogException(logger, ErrorCode.ClientRegistrarFailedToRegister, String.Format("Directory.RegisterAsync {0} failed.", addr))
                        .Ignore();
        }

        internal void ClientDropped(GrainId clientId)
        {
            var addr = GetClientActivationAddress(clientId);
            localScheduler.QueueTask(
                () => grainDirectory.UnregisterAsync(addr), this.SchedulingContext)
                    .LogException(logger, ErrorCode.ClientRegistrarFailedToUnregister_1, String.Format("Directory.UnregisterAsync {0} failed.", addr))
                        .Ignore();
        }

        private async Task OnClientRefreshTimer(object data)
        {
            try
            {
                ICollection<GrainId> clients = gateway.GetConnectedClients();
                List<Task> tasks = new List<Task>();
                foreach (GrainId clientId in clients)
                {
                    var addr = GetClientActivationAddress(clientId);
                    Task task = grainDirectory.RegisterAsync(addr).
                        LogException(logger, ErrorCode.ClientRegistrarFailedToUnregister_2,
                            String.Format("Directory.UnregisterAsync {0} failed.", addr));
                    task.Ignore();
                    tasks.Add(task);
                }
                await Task.WhenAll(tasks);
            }
            catch (Exception exc)
            {
                logger.Error(ErrorCode.ClientRegistrarTimerFailed, "OnClientRefreshTimer has thrown.", exc);
            }
        }

        private ActivationAddress GetClientActivationAddress(GrainId clientId)
        {
            return ActivationAddress.GetAddress(myAddress, clientId, ActivationId.GetActivationId(clientId));
        }
 

        #region IClientGateway Members

        /// <summary>
        /// Registers a client object on this gateway.
        /// </summary>
        public async Task<ActivationAddress> RegisterClientObserver(GrainId grainId, GrainId clientId)
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


