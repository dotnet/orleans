using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Xml;
using Orleans.MultiCluster;
using Orleans.Runtime.Configuration;
using Orleans.Runtime.MembershipService;
using Orleans.Runtime.MultiClusterNetwork;

namespace Orleans.Runtime.Management
{
    /// <summary>
    /// Implementation class for the Orleans management grain.
    /// </summary>
    [OneInstancePerCluster]
    internal class ManagementGrain : Grain, IManagementGrain
    {
        private readonly GlobalConfiguration globalConfig;
        private readonly IMultiClusterOracle multiClusterOracle;
        private readonly IInternalGrainFactory internalGrainFactory;
        private readonly ISiloStatusOracle siloStatusOracle;
        private readonly MembershipTableFactory membershipTableFactory;
        private Logger logger;

        public ManagementGrain(
            GlobalConfiguration globalConfig,
            IMultiClusterOracle multiClusterOracle,
            IInternalGrainFactory internalGrainFactory,
            ISiloStatusOracle siloStatusOracle,
            MembershipTableFactory membershipTableFactory)
        {
            this.globalConfig = globalConfig;
            this.multiClusterOracle = multiClusterOracle;
            this.internalGrainFactory = internalGrainFactory;
            this.siloStatusOracle = siloStatusOracle;
            this.membershipTableFactory = membershipTableFactory;
        }

        public override Task OnActivateAsync()
        {
            logger = LogManager.GetLogger("ManagementGrain", LoggerType.Runtime);
            return TaskDone.Done;
        }

        public async Task<Dictionary<SiloAddress, SiloStatus>> GetHosts(bool onlyActive = false)
        {
            // If the status oracle isn't MembershipOracle, then it is assumed that it does not use IMembershipTable.
            // In that event, return the approximate silo statuses from the status oracle.
            if (!(this.siloStatusOracle is MembershipOracle)) return this.siloStatusOracle.GetApproximateSiloStatuses(onlyActive);

            // Explicitly read the membership table and return the results.
            var table = await GetMembershipTable();
            var members = await table.ReadAll();
            var results = onlyActive
                ? members.Members.Where(item => item.Item1.Status == SiloStatus.Active)
                : members.Members;
            return results.ToDictionary(item => item.Item1.SiloAddress, item => item.Item1.Status);
        }

        public async Task<MembershipEntry[]> GetDetailedHosts(bool onlyActive = false)
        {
            logger.Info("GetDetailedHosts onlyActive={0}", onlyActive);

            var mTable = await GetMembershipTable();
            var table = await mTable.ReadAll();

            if (onlyActive)
            {
                return table.Members
                    .Where(item => item.Item1.Status == SiloStatus.Active)
                    .Select(x => x.Item1)
                    .ToArray();
            }

            return table.Members
                .Select(x => x.Item1)
                .ToArray();
        }

        public Task SetSystemLogLevel(SiloAddress[] siloAddresses, int traceLevel)
        {
            var silos = GetSiloAddresses(siloAddresses);
            logger.Info("SetSystemTraceLevel={1} {0}", Utils.EnumerableToString(silos), traceLevel);

            List<Task> actionPromises = PerformPerSiloAction(silos,
                s => GetSiloControlReference(s).SetSystemLogLevel(traceLevel));

            return Task.WhenAll(actionPromises);
        }

        public Task SetAppLogLevel(SiloAddress[] siloAddresses, int traceLevel)
        {
            var silos = GetSiloAddresses(siloAddresses);
            logger.Info("SetAppTraceLevel={1} {0}", Utils.EnumerableToString(silos), traceLevel);

            List<Task> actionPromises = PerformPerSiloAction(silos,
                s => GetSiloControlReference(s).SetAppLogLevel(traceLevel));

            return Task.WhenAll(actionPromises);
        }

        public Task SetLogLevel(SiloAddress[] siloAddresses, string logName, int traceLevel)
        {
            var silos = GetSiloAddresses(siloAddresses);
            logger.Info("SetLogLevel[{1}]={2} {0}", Utils.EnumerableToString(silos), logName, traceLevel);

            List<Task> actionPromises = PerformPerSiloAction(silos,
                s => GetSiloControlReference(s).SetLogLevel(logName, traceLevel));

            return Task.WhenAll(actionPromises);
        }

        public Task ForceGarbageCollection(SiloAddress[] siloAddresses)
        {
            var silos = GetSiloAddresses(siloAddresses);
            logger.Info("Forcing garbage collection on {0}", Utils.EnumerableToString(silos));
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
            logger.Info("Forcing runtime statistics collection on {0}", Utils.EnumerableToString(silos));
            List<Task> actionPromises = PerformPerSiloAction(
                silos,
                s => GetSiloControlReference(s).ForceRuntimeStatisticsCollection());
            return Task.WhenAll(actionPromises);
        }
        
        public Task<SiloRuntimeStatistics[]> GetRuntimeStatistics(SiloAddress[] siloAddresses)
        {
            var silos = GetSiloAddresses(siloAddresses);
            if (logger.IsVerbose) logger.Verbose("GetRuntimeStatistics on {0}", Utils.EnumerableToString(silos));
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
            return tasks.Select(s => s.Result).Select(r => r.LocalActivations.Count).Sum();
        }

        public async Task UpdateConfiguration(SiloAddress[] hostIds, Dictionary<string, string> configuration, Dictionary<string, string> tracing)
        {
            var global = new[] { "Globals/", "/Globals/", "OrleansConfiguration/Globals/", "/OrleansConfiguration/Globals/" };
            if (hostIds != null && configuration.Keys.Any(k => global.Any(k.StartsWith)))
                throw new ArgumentException("Must update global configuration settings on all silos");

            var silos = GetSiloAddresses(hostIds);
            if (silos.Length == 0) return;

            var document = XPathValuesToXml(configuration);
            if (tracing != null)
            {
                AddXPathValue(document, new[] { "OrleansConfiguration", "Defaults", "Tracing" }, null);
                var parent = document["OrleansConfiguration"]["Defaults"]["Tracing"];
                foreach (var trace in tracing)
                {
                    var child = document.CreateElement("TraceLevelOverride");
                    child.SetAttribute("LogPrefix", trace.Key);
                    child.SetAttribute("TraceLevel", trace.Value);
                    parent.AppendChild(child);
                }
            }
            
            using(var sw = new StringWriter())
            { 
                using(var xw = XmlWriter.Create(sw))
                { 
                    document.WriteTo(xw);
                    xw.Flush();
                    var xml = sw.ToString();
                    // do first one, then all the rest to avoid spamming all the silos in case of a parameter error
                    await GetSiloControlReference(silos[0]).UpdateConfiguration(xml);
                    await Task.WhenAll(silos.Skip(1).Select(s => GetSiloControlReference(s).UpdateConfiguration(xml)));
                }
            }
        }

        public async Task UpdateStreamProviders(SiloAddress[] hostIds, IDictionary<string, ProviderCategoryConfiguration> streamProviderConfigurations)
        {
            SiloAddress[] silos = GetSiloAddresses(hostIds);
            List<Task> actionPromises = PerformPerSiloAction(silos,
                s => GetSiloControlReference(s).UpdateStreamProviders(streamProviderConfigurations));
            await Task.WhenAll(actionPromises);
        }

        public async Task<string[]> GetActiveGrainTypes(SiloAddress[] hostsIds=null)
        {
            if (hostsIds == null)
            {
                Dictionary<SiloAddress, SiloStatus> hosts = await GetHosts(true);
                SiloAddress[] silos = hosts.Keys.ToArray();
            }
            var all = GetSiloAddresses(hostsIds).Select(s => GetSiloControlReference(s).GetGrainTypeList()).ToArray();
            await Task.WhenAll(all);
            return all.SelectMany(s => s.Result).Distinct().ToArray();

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
                String.Format("SendControlCommandToProvider of type {0} and name {1} command {2}.", providerTypeFullName, providerName, command));
        }

        private async Task<object[]> ExecutePerSiloCall(Func<ISiloControl, Task<object>> action, string actionToLog)
        {
            var silos = await GetHosts(true);

            if(logger.IsVerbose) {
                logger.Verbose("Executing {0} against {1}", actionToLog, Utils.EnumerableToString(silos.Keys));
            }

            var actionPromises = new List<Task<object>>();
            foreach (SiloAddress siloAddress in silos.Keys.ToArray())
                actionPromises.Add(action(GetSiloControlReference(siloAddress)));

            return await Task.WhenAll(actionPromises);
        }

        private Task<IMembershipTable> GetMembershipTable()
        {
            var membershipOracle = this.siloStatusOracle as MembershipOracle;
            if (!(this.siloStatusOracle is MembershipOracle)) throw new InvalidOperationException("The current membership oracle does not support detailed silo status reporting.");
            return this.membershipTableFactory.GetMembershipTable();
        }

        private SiloAddress[] GetSiloAddresses(SiloAddress[] silos)
        {
            if (silos != null && silos.Length > 0)
                return silos;

            return this.siloStatusOracle
                       .GetApproximateSiloStatuses(true).Select(s => s.Key).ToArray();
        }

        /// <summary>
        /// Perform an action for each silo.
        /// </summary>
        /// <remarks>
        /// Because SiloControl contains a reference to a system target, each method call using that reference 
        /// will get routed either locally or remotely to the appropriate silo instance auto-magically.
        /// </remarks>
        /// <param name="siloAddresses">List of silos to perform the action for</param>
        /// <param name="perSiloAction">The action functiona to be performed for each silo</param>
        /// <returns>Array containing one Task for each silo the action was performed for</returns>
        private List<Task> PerformPerSiloAction(SiloAddress[] siloAddresses, Func<SiloAddress, Task> perSiloAction)
        {
            var requestsToSilos = new List<Task>();
            foreach (SiloAddress siloAddress in siloAddresses)
                requestsToSilos.Add( perSiloAction(siloAddress) );
            
            return requestsToSilos;
        }

        private static XmlDocument XPathValuesToXml(Dictionary<string,string> values)
        {
            var doc = new XmlDocument();
            if (values == null) return doc;

            foreach (var p in values)
            {
                var path = p.Key.Split('/').ToList();
                if (path[0] == "")
                    path.RemoveAt(0);
                if (path[0] != "OrleansConfiguration")
                    path.Insert(0, "OrleansConfiguration");
                if (!path[path.Count - 1].StartsWith("@"))
                    throw new ArgumentException("XPath " + p.Key + " must end with @attribute");
                AddXPathValue(doc, path, p.Value);
            }
            return doc;
        }

        private static void AddXPathValue(XmlNode xml, IEnumerable<string> path, string value)
        {
            if (path == null) return;

            var first = path.FirstOrDefault();
            if (first == null) return;

            if (first.StartsWith("@"))
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

        #region MultiCluster

        private IMultiClusterOracle GetMultiClusterOracle()
        {
            if (!this.globalConfig.HasMultiClusterNetwork)
                throw new OrleansException("No multicluster network configured");
            return this.multiClusterOracle;
        }

        public Task<List<IMultiClusterGatewayInfo>> GetMultiClusterGateways()
        {
            return Task.FromResult(GetMultiClusterOracle().GetGateways().Cast<IMultiClusterGatewayInfo>().ToList());
        }

        public Task<MultiClusterConfiguration> GetMultiClusterConfiguration()
        {
            return Task.FromResult(GetMultiClusterOracle().GetMultiClusterConfiguration());
        }

        public async Task<MultiClusterConfiguration> InjectMultiClusterConfiguration(IEnumerable<string> clusters, string comment = "", bool checkForLaggingSilosFirst = true)
        {
            var multiClusterOracle = GetMultiClusterOracle();

            var configuration = new MultiClusterConfiguration(DateTime.UtcNow, clusters.ToList(), comment);

            if (!MultiClusterConfiguration.OlderThan(multiClusterOracle.GetMultiClusterConfiguration(), configuration))
                throw new OrleansException("Could not inject multi-cluster configuration: current configuration is newer than clock");

            if (checkForLaggingSilosFirst)
            {
                try
                {
                    var laggingSilos = await multiClusterOracle.FindLaggingSilos(multiClusterOracle.GetMultiClusterConfiguration());

                    if (laggingSilos.Count > 0)
                    {
                        var msg = string.Format("Found unstable silos {0}", string.Join(",", laggingSilos));
                        throw new OrleansException(msg);
                    }
                }
                catch (Exception e)
                {
                    throw new OrleansException("Could not inject multi-cluster configuration: stability check failed", e);
                }
            }

            await multiClusterOracle.InjectMultiClusterConfiguration(configuration);

            return configuration;
        }

        public Task<List<SiloAddress>> FindLaggingSilos()
        {
            var multiClusterOracle = GetMultiClusterOracle();
            var expected = multiClusterOracle.GetMultiClusterConfiguration();
            return multiClusterOracle.FindLaggingSilos(expected);
        }

        #endregion


    }
}
