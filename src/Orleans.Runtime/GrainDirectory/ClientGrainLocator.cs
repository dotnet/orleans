using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Orleans.GrainDirectory;
using Orleans.Internal;
using Orleans.Runtime.Messaging;

namespace Orleans.Runtime.GrainDirectory
{
    /// <summary>
    /// Implementation of <see cref="IGrainLocator"/> that uses the in memory distributed directory of Orleans
    /// </summary>
    internal class ClientGrainLocator : IGrainLocator
    {
        private readonly SafeRandom _random = new SafeRandom();
        private readonly ILocalClientDirectory _clientDirectory;
        private readonly ILogger<ClientGrainLocator> _logger;

        public ClientGrainLocator(ILocalClientDirectory clientDirectory, ILogger<ClientGrainLocator> logger)
        {
            _clientDirectory = clientDirectory;
            _logger = logger;
        }

        public Task<List<ActivationAddress>> Lookup(GrainId clientGrainId)
        {
            var table = _clientDirectory.GetRoutingTable();
            if (table.Routes.TryGetValue(clientGrainId, out var clientRoutes) && clientRoutes.Count > 0)
            {
                return Task.FromResult(clientRoutes);
            }

            return LookupClientAsync(clientGrainId, table.RemoteDirectories);

            async Task<List<ActivationAddress>> LookupClientAsync(GrainId clientGrainId, IRemoteClientDirectory[] remoteDirectories)
            {
                var seed = _random.Next();
                var attemptsRemaining = 5;
                while (attemptsRemaining-- > 0 && remoteDirectories.Length > 0)
                {
                    try
                    {
                        // Cycle through remote directories.
                        var remoteDirectory = remoteDirectories[(ushort)seed++ % remoteDirectories.Length];

                        var response = await remoteDirectory.GetClientRoutes(clientGrainId);
                        if (response is object && response.Count > 0)
                        {
                            var result = new List<ActivationAddress>(response.Count);
                            foreach (var route in response)
                            {
                                result.Add(Gateway.GetClientActivationAddress(clientGrainId, route));
                            }
                        }
                    }
                    catch (Exception exception) when (attemptsRemaining > 0)
                    {
                        _logger.LogError(exception, "Exception calling remote client directory");
                    }

                    var table = _clientDirectory.GetRoutingTable();
                    remoteDirectories = table.RemoteDirectories;
                }

                return new List<ActivationAddress>(0);
            }
        }

        public bool TryLocalLookup(GrainId grainId, out List<ActivationAddress> addresses)
        {
            if (!ClientGrainId.TryParse(grainId, out _))
            {
                ThrowNotClientGrainId(grainId);
            }

            var table = _clientDirectory.GetRoutingTable();
            if (table.Routes.TryGetValue(grainId, out var clientRoutes) && clientRoutes.Count > 0)
            {
                addresses = clientRoutes;
                return true;
            }

            addresses = null;
            return false;
        }

        public Task<ActivationAddress> Register(ActivationAddress address) => throw new InvalidOperationException($"Cannot register client grain explicitly");

        public Task Unregister(ActivationAddress address, UnregistrationCause cause) => throw new InvalidOperationException($"Cannot unregister client grain explicitly");

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static GrainId ThrowNotClientGrainId(GrainId grainId) => throw new InvalidOperationException($"{grainId} is not a client id");
    }
}
