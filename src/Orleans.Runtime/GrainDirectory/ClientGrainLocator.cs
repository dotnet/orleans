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
        private readonly IInternalGrainFactory _grainFactory;
        private readonly IClusterMembershipService _clusterMembershipService;
        private readonly SiloAddress _localSilo;
        private readonly ILogger<ClientGrainLocator> _logger;
        private readonly object _lockObj = new object();
        private MembershipVersion _observedMembershipVersion = MembershipVersion.MinValue;
        private IRemoteClientDirectory[] _remoteDirectories = Array.Empty<IRemoteClientDirectory>();

        public ClientGrainLocator(
            ILocalClientDirectory clientDirectory,
            ILogger<ClientGrainLocator> logger,
            IInternalGrainFactory grainFactory,
            IClusterMembershipService clusterMembershipService,
            ILocalSiloDetails localSiloDetails)
        {
            _clientDirectory = clientDirectory;
            _logger = logger;
            _grainFactory = grainFactory;
            _clusterMembershipService = clusterMembershipService;
            _localSilo = localSiloDetails.SiloAddress;
        }

        public Task<List<ActivationAddress>> Lookup(GrainId clientGrainId)
        {
            var table = _clientDirectory.GetRoutingTable();
            if (table.TryGetValue(clientGrainId, out var clientRoutes) && clientRoutes.Count > 0)
            {
                return Task.FromResult(clientRoutes);
            }

            return LookupClientAsync(clientGrainId);

            async Task<List<ActivationAddress>> LookupClientAsync(GrainId clientGrainId)
            {
                var seed = _random.Next();
                var attemptsRemaining = 5;
                List<ActivationAddress> result;
                while (attemptsRemaining-- > 0 && GetRemoteClientDirectories() is { Length: > 0 } remoteDirectories) 
                {
                    try
                    {
                        // Cycle through remote directories.
                        var remoteDirectory = remoteDirectories[(ushort)seed++ % remoteDirectories.Length];

                        var response = await remoteDirectory.GetClientRoutes(clientGrainId);
                        if (response is object && response.Count > 0)
                        {
                            result = new List<ActivationAddress>(response.Count);
                            foreach (var route in response)
                            {
                                result.Add(Gateway.GetClientActivationAddress(clientGrainId, route));
                            }

                            return result;
                        }
                    }
                    catch (Exception exception) when (attemptsRemaining > 0)
                    {
                        _logger.LogError(exception, "Exception calling remote client directory");
                    }

                    if (TryLocalLookup(clientGrainId, out result) && result.Count > 0)
                    {
                        return result;
                    }
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
            if (table.TryGetValue(grainId, out var clientRoutes) && clientRoutes.Count > 0)
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

        private IRemoteClientDirectory[] GetRemoteClientDirectories()
        {
            var membershipSnapshot = _clusterMembershipService.CurrentSnapshot;
            if (membershipSnapshot.Version == _observedMembershipVersion)
            {
                return _remoteDirectories;
            }

            lock (_lockObj)
            {
                membershipSnapshot = _clusterMembershipService.CurrentSnapshot;
                if (membershipSnapshot.Version == _observedMembershipVersion)
                {
                    return _remoteDirectories;
                }

                var remotesBuilder = new List<IRemoteClientDirectory>(membershipSnapshot.Members.Count);
                foreach (var member in membershipSnapshot.Members.Values)
                {
                    if (member.SiloAddress.Equals(_localSilo)) continue;
                    if (member.Status != SiloStatus.Active) continue;

                    remotesBuilder.Add(_grainFactory.GetSystemTarget<IRemoteClientDirectory>(Constants.ClientDirectoryType, member.SiloAddress));
                }

                _observedMembershipVersion = membershipSnapshot.Version;
                return _remoteDirectories = remotesBuilder.ToArray();
            }
        }
    }
}
