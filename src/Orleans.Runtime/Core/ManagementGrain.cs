using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System.Xml;
using Microsoft.Extensions.Logging;
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
        private readonly GrainTypeManager grainTypeManager;
        private readonly IVersionStore versionStore;
        private readonly MembershipTableManager membershipTableManager;
        private readonly ILogger logger;
        public ManagementGrain(
            IInternalGrainFactory internalGrainFactory,
            ISiloStatusOracle siloStatusOracle,
            GrainTypeManager grainTypeManager,
            IVersionStore versionStore,
            ILogger<ManagementGrain> logger,
            MembershipTableManager membershipTableManager)
        {
            this.membershipTableManager = membershipTableManager;
            this.internalGrainFactory = internalGrainFactory;
            this.siloStatusOracle = siloStatusOracle;
            this.grainTypeManager = grainTypeManager;
            this.versionStore = versionStore;
            this.logger = logger;
        }

        public async Task<Dictionary<SiloAddress, SiloStatus>> GetHosts(bool onlyActive = false)
        {
            await this.membershipTableManager.Refresh();
            return this.siloStatusOracle.GetApproximateSiloStatuses(onlyActive);
        }

        public async Task<MembershipEntry[]> GetDetailedHosts(bool onlyActive = false)
        {
            logger.Info("GetDetailedHosts onlyActive={0}", onlyActive);

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
            logger.Info("Forcing garbage collection on {0}", Utils.EnumerableToString(silos));
            var actionPromises = Array.ConvertAll(silos,
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
            logger.Info("Forcing runtime statistics collection on {0}", Utils.EnumerableToString(silos));
            var actionPromises = Array.ConvertAll(
                silos,
                s => GetSiloControlReference(s).ForceRuntimeStatisticsCollection());
            return Task.WhenAll(actionPromises);
        }

        public Task<SiloRuntimeStatistics[]> GetRuntimeStatistics(SiloAddress[] siloAddresses)
        {
            var silos = GetSiloAddresses(siloAddresses);
            if (logger.IsEnabled(LogLevel.Debug)) logger.Debug("GetRuntimeStatistics on {0}", Utils.EnumerableToString(silos));
            var promises = new List<Task<SiloRuntimeStatistics>>();
            foreach (SiloAddress siloAddress in silos)
                promises.Add(GetSiloControlReference(siloAddress).GetRuntimeStatistics());

            return Task.WhenAll(promises);
        }

        public async Task<SimpleGrainStatistic[]> GetSimpleGrainStatistics(SiloAddress[] hostsIds)
        {
            var all = GetSiloAddresses(hostsIds).Select(s =>
                GetSiloControlReference(s).GetSimpleGrainStatistics()).ToList();
            var res = await Task.WhenAll(all);
            return res.SelectMany(s => s).ToArray();
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
            var res = await Task.WhenAll(all);
            return res.SelectMany(s => s).ToArray();
        }

        public async Task<int> GetGrainActivationCount(GrainReference grainReference)
        {
            Dictionary<SiloAddress, SiloStatus> hosts = await GetHosts(true);
            List<SiloAddress> hostsIds = hosts.Keys.ToList();
            var tasks = new List<Task<DetailedGrainReport>>();
            foreach (var silo in hostsIds)
                tasks.Add(GetSiloControlReference(silo).GetDetailedGrainReport(grainReference.GrainId));

            var res = await Task.WhenAll(tasks);
            return res.Sum(r => r.LocalActivations.Count);
        }

        public async Task<string[]> GetActiveGrainTypes(SiloAddress[] hostsIds = null)
        {
            if (hostsIds == null)
            {
                Dictionary<SiloAddress, SiloStatus> hosts = await GetHosts(true);
                SiloAddress[] silos = hosts.Keys.ToArray();
            }
            var all = GetSiloAddresses(hostsIds).Select(s => GetSiloControlReference(s).GetGrainTypeList()).ToArray();
            var res = await Task.WhenAll(all);
            return res.SelectMany(s => s).Distinct().ToArray();

        }

        public Task SetCompatibilityStrategy(CompatibilityStrategy strategy)
        {
            return SetStrategy(
                store => store.SetCompatibilityStrategy(strategy),
                siloControl => siloControl.SetCompatibilityStrategy(strategy));
        }

        public Task SetSelectorStrategy(VersionSelectorStrategy strategy)
        {
            return SetStrategy(
                store => store.SetSelectorStrategy(strategy),
                siloControl => siloControl.SetSelectorStrategy(strategy));
        }

        public Task SetCompatibilityStrategy(int interfaceId, CompatibilityStrategy strategy)
        {
            CheckIfIsExistingInterface(interfaceId);
            return SetStrategy(
                store => store.SetCompatibilityStrategy(interfaceId, strategy),
                siloControl => siloControl.SetCompatibilityStrategy(interfaceId, strategy));
        }

        public Task SetSelectorStrategy(int interfaceId, VersionSelectorStrategy strategy)
        {
            CheckIfIsExistingInterface(interfaceId);
            return SetStrategy(
                store => store.SetSelectorStrategy(interfaceId, strategy),
                siloControl => siloControl.SetSelectorStrategy(interfaceId, strategy));
        }

        public async Task<int> GetTotalActivationCount()
        {
            Dictionary<SiloAddress, SiloStatus> hosts = await GetHosts(true);
            List<SiloAddress> silos = hosts.Keys.ToList();
            var tasks = new List<Task<int>>();
            foreach (var silo in silos)
                tasks.Add(GetSiloControlReference(silo).GetActivationCount());

            var res = await Task.WhenAll(tasks);
            return res.Sum();
        }

        public Task<object[]> SendControlCommandToProvider(string providerTypeFullName, string providerName, int command, object arg)
        {
            return ExecutePerSiloCall(isc => isc.SendControlCommandToProvider(providerTypeFullName, providerName, command, arg),
                String.Format("SendControlCommandToProvider of type {0} and name {1} command {2}.", providerTypeFullName, providerName, command));
        }

        private void CheckIfIsExistingInterface(int interfaceId)
        {
            Type unused;
            var interfaceMap = this.grainTypeManager.ClusterGrainInterfaceMap;
            if (!interfaceMap.TryGetServiceInterface(interfaceId, out unused))
            {
                throw new ArgumentException($"Interface code '{interfaceId} not found", nameof(interfaceId));
            }
        }

        private async Task SetStrategy(Func<IVersionStore, Task> storeFunc, Func<ISiloControl, Task> applyFunc)
        {
            await storeFunc(versionStore);
            var silos = GetSiloAddresses(null);
            var actionPromises = Array.ConvertAll(
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
                logger.Debug("Executing {0} against {1}", actionToLog, Utils.EnumerableToString(silos.Keys));
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
                       .GetApproximateSiloStatuses(true).Select(s => s.Key).ToArray();
        }

        private static void AddXPathValue(XmlNode xml, IEnumerable<string> path, string value)
        {
            if (path == null) return;

            var first = path.FirstOrDefault();
            if (first == null) return;

            if (first.StartsWith("@", StringComparison.Ordinal))
            {
                first = first.Substring(1);
                if (path.Count() != 1)
                    throw new ArgumentException("Attribute " + first + " must be last in path");
                var e = xml as XmlElement;
                if (e == null)
                    throw new ArgumentException("Attribute " + first + " must be on XML element");
                e.SetAttribute(first, value);
                return;
            }

            foreach (var child in xml.ChildNodes)
            {
                var e = child as XmlElement;
                if (e != null && e.LocalName == first)
                {
                    AddXPathValue(e, path.Skip(1), value);
                    return;
                }
            }

            var empty = (xml as XmlDocument ?? xml.OwnerDocument).CreateElement(first);
            xml.AppendChild(empty);
            AddXPathValue(empty, path.Skip(1), value);
        }

        private ISiloControl GetSiloControlReference(SiloAddress silo)
        {
            return this.internalGrainFactory.GetSystemTarget<ISiloControl>(Constants.SiloControlId, silo);
        }
    }
}
