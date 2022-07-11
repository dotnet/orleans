using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Orleans.Metadata;
using Orleans.Runtime.GrainDirectory;
using Orleans.Runtime.MembershipService;
using Orleans.Versions;
using Orleans.Versions.Compatibility;
using Orleans.Versions.Selector;

namespace Orleans.Runtime.Management
{
    /// <summary>
    /// Implementation class for the Orleans management grain.
    /// </summary>
    internal class ManagementGrain : Grain, IManagementGrain
    {
        private readonly IInternalGrainFactory internalGrainFactory;
        private readonly ISiloStatusOracle siloStatusOracle;
        private readonly IVersionStore versionStore;
        private readonly MembershipTableManager membershipTableManager;
        private readonly GrainManifest siloManifest;
        private readonly ClusterManifest clusterManifest;
        private readonly ILogger logger;
        private readonly Catalog catalog;
        private readonly GrainLocator grainLocator;

        public ManagementGrain(
            IInternalGrainFactory internalGrainFactory,
            ISiloStatusOracle siloStatusOracle,
            IVersionStore versionStore,
            ILogger<ManagementGrain> logger,
            MembershipTableManager membershipTableManager,
            IClusterManifestProvider clusterManifestProvider,
            Catalog catalog,
            GrainLocator grainLocator)
        {
            this.membershipTableManager = membershipTableManager;
            this.siloManifest = clusterManifestProvider.LocalGrainManifest;
            this.clusterManifest = clusterManifestProvider.Current;
            this.internalGrainFactory = internalGrainFactory;
            this.siloStatusOracle = siloStatusOracle;
            this.versionStore = versionStore;
            this.logger = logger;
            this.catalog = catalog;
            this.grainLocator = grainLocator;
        }

        public async Task<Dictionary<SiloAddress, SiloStatus>> GetHosts(bool onlyActive = false)
        {
            await this.membershipTableManager.Refresh();
            return this.siloStatusOracle.GetApproximateSiloStatuses(onlyActive);
        }

        public async Task<MembershipEntry[]> GetDetailedHosts(bool onlyActive = false)
        {
            logger.LogInformation("GetDetailedHosts OnlyActive={OnlyActive}", onlyActive);

            await this.membershipTableManager.Refresh();

            var table = this.membershipTableManager.MembershipTableSnapshot;

            MembershipEntry[] result;
            if (onlyActive)
            {
                result = table.Entries
                    .Where(item => item.Value.Status == SiloStatus.Active)
                    .Select(x => x.Value)
                    .ToArray();
            }
            else
            {
                result = table.Entries
                    .Select(x => x.Value)
                    .ToArray();
            }

            return result;
        }

        public Task ForceGarbageCollection(SiloAddress[] siloAddresses)
        {
            var silos = GetSiloAddresses(siloAddresses);
            logger.LogInformation("Forcing garbage collection on {SiloAddresses}", Utils.EnumerableToString(silos));
            List<Task> actionPromises = PerformPerSiloAction(silos,
                s => GetSiloControlReference(s).ForceGarbageCollection());
            return Task.WhenAll(actionPromises);
        }

        public Task ForceActivationCollection(SiloAddress[] siloAddresses, TimeSpan ageLimit)
        {
            var silos = GetSiloAddresses(siloAddresses);
            return Task.WhenAll(GetSiloAddresses(silos).Select(s =>
                GetSiloControlReference(s).ForceActivationCollection(ageLimit)));
        }

        public async Task ForceActivationCollection(TimeSpan ageLimit)
        {
            Dictionary<SiloAddress, SiloStatus> hosts = await GetHosts(true);
            SiloAddress[] silos = hosts.Keys.ToArray();
            await ForceActivationCollection(silos, ageLimit);
        }

        public Task ForceRuntimeStatisticsCollection(SiloAddress[] siloAddresses)
        {
            var silos = GetSiloAddresses(siloAddresses);
            logger.LogInformation("Forcing runtime statistics collection on {SiloAddresses}", Utils.EnumerableToString(silos));
            List<Task> actionPromises = PerformPerSiloAction(
                silos,
                s => GetSiloControlReference(s).ForceRuntimeStatisticsCollection());
            return Task.WhenAll(actionPromises);
        }

        public Task<SiloRuntimeStatistics[]> GetRuntimeStatistics(SiloAddress[] siloAddresses)
        {
            var silos = GetSiloAddresses(siloAddresses);
            if (logger.IsEnabled(LogLevel.Debug)) logger.LogDebug("GetRuntimeStatistics on {SiloAddresses}", Utils.EnumerableToString(silos));
            var promises = new List<Task<SiloRuntimeStatistics>>();
            foreach (SiloAddress siloAddress in silos)
                promises.Add(GetSiloControlReference(siloAddress).GetRuntimeStatistics());

            return Task.WhenAll(promises);
        }

        public async Task<SimpleGrainStatistic[]> GetSimpleGrainStatistics(SiloAddress[] hostsIds)

        {
            var all = GetSiloAddresses(hostsIds).Select(s =>
                GetSiloControlReference(s).GetSimpleGrainStatistics()).ToList();
            await Task.WhenAll(all);
            return all.SelectMany(s => s.Result).ToArray();
        }

        public async Task<SimpleGrainStatistic[]> GetSimpleGrainStatistics()
        {
            Dictionary<SiloAddress, SiloStatus> hosts = await GetHosts(true);
            SiloAddress[] silos = hosts.Keys.ToArray();
            return await GetSimpleGrainStatistics(silos);
        }

        public async Task<DetailedGrainStatistic[]> GetDetailedGrainStatistics(string[] types = null, SiloAddress[] hostsIds = null)
        {
            if (hostsIds == null)
            {
                Dictionary<SiloAddress, SiloStatus> hosts = await GetHosts(true);
                hostsIds = hosts.Keys.ToArray();
            }

            var all = GetSiloAddresses(hostsIds).Select(s =>
              GetSiloControlReference(s).GetDetailedGrainStatistics(types)).ToList();
            await Task.WhenAll(all);
            return all.SelectMany(s => s.Result).ToArray();
        }

        public async Task<int> GetGrainActivationCount(GrainReference grainReference)
        {
            Dictionary<SiloAddress, SiloStatus> hosts = await GetHosts(true);
            List<SiloAddress> hostsIds = hosts.Keys.ToList();
            var tasks = new List<Task<DetailedGrainReport>>();
            foreach (var silo in hostsIds)
                tasks.Add(GetSiloControlReference(silo).GetDetailedGrainReport(grainReference.GrainId));

            await Task.WhenAll(tasks);
            return tasks.Select(s => s.Result).Select(CountActivations).Sum();
            static int CountActivations(DetailedGrainReport report) => report.LocalActivation is { Length: > 0 } ? 1 : 0;
        }

        public async Task SetCompatibilityStrategy(CompatibilityStrategy strategy)
        {
            await SetStrategy(
                store => store.SetCompatibilityStrategy(strategy),
                siloControl => siloControl.SetCompatibilityStrategy(strategy));
        }

        public async Task SetSelectorStrategy(VersionSelectorStrategy strategy)
        {
            await SetStrategy(
                store => store.SetSelectorStrategy(strategy),
                siloControl => siloControl.SetSelectorStrategy(strategy));
        }

        public async Task SetCompatibilityStrategy(GrainInterfaceType interfaceType, CompatibilityStrategy strategy)
        {
            CheckIfIsExistingInterface(interfaceType);
            await SetStrategy(
                store => store.SetCompatibilityStrategy(interfaceType, strategy),
                siloControl => siloControl.SetCompatibilityStrategy(interfaceType, strategy));
        }

        public async Task SetSelectorStrategy(GrainInterfaceType interfaceType, VersionSelectorStrategy strategy)
        {
            CheckIfIsExistingInterface(interfaceType);
            await SetStrategy(
                store => store.SetSelectorStrategy(interfaceType, strategy),
                siloControl => siloControl.SetSelectorStrategy(interfaceType, strategy));
        }

        public async Task<int> GetTotalActivationCount()
        {
            Dictionary<SiloAddress, SiloStatus> hosts = await GetHosts(true);
            List<SiloAddress> silos = hosts.Keys.ToList();
            var tasks = new List<Task<int>>();
            foreach (var silo in silos)
                tasks.Add(GetSiloControlReference(silo).GetActivationCount());

            await Task.WhenAll(tasks);
            int sum = 0;
            foreach (Task<int> task in tasks)
                sum += task.Result;

            return sum;
        }

        public Task<object[]> SendControlCommandToProvider(string providerTypeFullName, string providerName, int command, object arg)
        {
            return ExecutePerSiloCall(isc => isc.SendControlCommandToProvider(providerTypeFullName, providerName, command, arg),
                $"SendControlCommandToProvider of type {providerTypeFullName} and name {providerName} command {command}.");
        }

        public ValueTask<SiloAddress> GetActivationAddress(IAddressable reference)
        {
            var grainReference = reference as GrainReference;
            var grainId = grainReference.GrainId;

            GrainProperties grainProperties = default;
            if (!siloManifest.Grains.TryGetValue(grainId.Type, out grainProperties))
            {
                var grainManifest = clusterManifest.AllGrainManifests
                    .SelectMany(m => m.Grains.Where(g => g.Key == grainId.Type))
                    .FirstOrDefault();
                if (grainManifest.Value != null)
                {
                    grainProperties = grainManifest.Value;
                }
                else
                {
                    throw new ArgumentException($"Unable to find Grain type '{grainId.Type}'. Make sure it is added to the Application Parts Manager at the Silo configuration.");
                }
            }

            if (grainProperties != default &&
                grainProperties.Properties.TryGetValue(WellKnownGrainTypeProperties.PlacementStrategy, out string placementStrategy))
            {
                if (placementStrategy == nameof(StatelessWorkerPlacement))
                {
                    throw new InvalidOperationException(
                        $"Grain '{grainReference.ToString()}' is a Stateless Worker. This type of grain can't be looked up by this method"
                    );
                }
            }

            if (grainLocator.TryLookupInCache(grainId, out var result))
            {
                return new ValueTask<SiloAddress>(result?.SiloAddress);
            }

            return LookupAsync(grainId, grainLocator);

            static async ValueTask<SiloAddress> LookupAsync(GrainId grainId, GrainLocator grainLocator)
            {
                var result = await grainLocator.Lookup(grainId);
                return result?.SiloAddress;
            }
        }

        private void CheckIfIsExistingInterface(GrainInterfaceType interfaceType)
        {
            GrainInterfaceType lookupId;
            if (GenericGrainInterfaceType.TryParse(interfaceType, out var generic))
            {
                lookupId = generic.Value;
            }
            else
            {
                lookupId = interfaceType;
            }

            if (!this.siloManifest.Interfaces.TryGetValue(lookupId, out _))
            {
                throw new ArgumentException($"Interface '{interfaceType} not found", nameof(interfaceType));
            }
        }

        private async Task SetStrategy(Func<IVersionStore, Task> storeFunc, Func<ISiloControl, Task> applyFunc)
        {
            await storeFunc(versionStore);
            var silos = GetSiloAddresses(null);
            var actionPromises = PerformPerSiloAction(
                silos,
                s => applyFunc(GetSiloControlReference(s)));
            try
            {
                await Task.WhenAll(actionPromises);
            }
            catch (Exception)
            {
                // ignored: silos that failed to set the new strategy will reload it from the storage
                // in the future.
            }
        }

        private async Task<object[]> ExecutePerSiloCall(Func<ISiloControl, Task<object>> action, string actionToLog)
        {
            var silos = await GetHosts(true);

            if (logger.IsEnabled(LogLevel.Debug))
            {
                logger.LogDebug("Executing {Action} against {SiloAddresses}", actionToLog, Utils.EnumerableToString(silos.Keys));
            }

            var actionPromises = new List<Task<object>>();
            foreach (SiloAddress siloAddress in silos.Keys.ToArray())
                actionPromises.Add(action(GetSiloControlReference(siloAddress)));

            return await Task.WhenAll(actionPromises);
        }

        private SiloAddress[] GetSiloAddresses(SiloAddress[] silos)
        {
            if (silos != null && silos.Length > 0)
                return silos;

            return this.siloStatusOracle
                       .GetApproximateSiloStatuses(true).Keys.ToArray();
        }

        /// <summary>
        /// Perform an action for each silo.
        /// </summary>
        /// <remarks>
        /// Because SiloControl contains a reference to a system target, each method call using that reference
        /// will get routed either locally or remotely to the appropriate silo instance auto-magically.
        /// </remarks>
        /// <param name="siloAddresses">List of silos to perform the action for</param>
        /// <param name="perSiloAction">The action function to be performed for each silo</param>
        /// <returns>Array containing one Task for each silo the action was performed for</returns>
        private List<Task> PerformPerSiloAction(SiloAddress[] siloAddresses, Func<SiloAddress, Task> perSiloAction)
        {
            var requestsToSilos = new List<Task>();
            foreach (SiloAddress siloAddress in siloAddresses)
                requestsToSilos.Add(perSiloAction(siloAddress));

            return requestsToSilos;
        }

        private ISiloControl GetSiloControlReference(SiloAddress silo)
        {
            return this.internalGrainFactory.GetSystemTarget<ISiloControl>(Constants.SiloControlType, silo);
        }

        public async ValueTask<List<GrainId>> GetActiveGrains(GrainType grainType)
        {
            var hosts = await GetHosts(true);
            var tasks = new List<Task<List<GrainId>>>();
            foreach (var siloAddress in hosts.Keys)
            {
                tasks.Add(GetSiloControlReference(siloAddress).GetActiveGrains(grainType));
            }

            await Task.WhenAll(tasks);
            var results = new List<GrainId>();
            foreach (var promise in tasks)
            {
                results.AddRange(await promise);
            }

            return results;
        }
    }
}
