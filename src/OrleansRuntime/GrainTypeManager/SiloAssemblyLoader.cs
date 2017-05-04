using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using Orleans.CodeGeneration;
using Orleans.Serialization;
using Orleans.LogConsistency;
using Orleans.Providers;
using Orleans.Runtime.Configuration;


namespace Orleans.Runtime
{
    internal class SiloAssemblyLoader
    {
        private readonly List<string> excludedGrains;
        private readonly LoggerImpl logger = LogManager.GetLogger("AssemblyLoader.Silo");
        private List<string> discoveredAssemblyLocations;
        private Dictionary<string, SearchOption> directories;

        public SiloAssemblyLoader(NodeConfiguration nodeConfig)
        {
            IDictionary<string, SearchOption> additionalDirectories = nodeConfig.AdditionalAssemblyDirectories;
            this.excludedGrains = nodeConfig.ExcludedGrainTypes != null
                ? new List<string>(nodeConfig.ExcludedGrainTypes)
                : new List<string>();
            var exeRoot = Path.GetDirectoryName(typeof(SiloAssemblyLoader).GetTypeInfo().Assembly.Location);
            var appRoot = Path.Combine(exeRoot, "Applications");
            var cwd = Directory.GetCurrentDirectory();

            directories = new Dictionary<string, SearchOption>
                    {
                        { exeRoot, SearchOption.TopDirectoryOnly },
                        { appRoot, SearchOption.AllDirectories }
                    };

            foreach (var kvp in additionalDirectories)
            {
                // Make sure the path is clean (get rid of ..\'s)
                directories[new DirectoryInfo(kvp.Key).FullName] = kvp.Value;
            }


            if (!directories.ContainsKey(cwd))
            {
                directories.Add(cwd, SearchOption.TopDirectoryOnly);
            }

            LoadApplicationAssemblies();
        }

        private void LoadApplicationAssemblies()
        {
#if !NETSTANDARD_TODO
            AssemblyLoaderPathNameCriterion[] excludeCriteria =
                {
                    AssemblyLoaderCriteria.ExcludeResourceAssemblies,
                    AssemblyLoaderCriteria.ExcludeSystemBinaries()
                };
            AssemblyLoaderReflectionCriterion[] loadCriteria =
                {
                    AssemblyLoaderReflectionCriterion.NewCriterion(
                        TypeUtils.IsConcreteGrainClass,
                        "Assembly does not contain any acceptable grain types."),
                    AssemblyLoaderCriteria.LoadTypesAssignableFrom(
                        typeof(IProvider))
                };

            discoveredAssemblyLocations = AssemblyLoader.LoadAssemblies(directories, excludeCriteria, loadCriteria, logger);
#endif
        }

        public IDictionary<string, GrainTypeData> GetGrainClassTypes(bool strict)
        {
            var result = new Dictionary<string, GrainTypeData>();
            Type[] grainTypes = strict
                ? TypeUtils.GetTypes(TypeUtils.IsConcreteGrainClass, logger).ToArray()
                : TypeUtils.GetTypes(discoveredAssemblyLocations, TypeUtils.IsConcreteGrainClass, logger).ToArray();

            foreach (var grainType in grainTypes)
            {
                var className = TypeUtils.GetFullName(grainType);
                if (excludedGrains.Contains(className))
                    continue;

                if (result.ContainsKey(className))
                    throw new InvalidOperationException(
                        string.Format("Precondition violated: GetLoadedGrainTypes should not return a duplicate type ({0})", className));

                Type grainStateType = null;

                // check if grainType derives from Grain<T> where T is a concrete class

                var parentType = grainType.GetTypeInfo().BaseType;
                while (parentType != typeof(Grain) && parentType != typeof(object))
                {
                    TypeInfo parentTypeInfo = parentType.GetTypeInfo();
                    if (parentTypeInfo.IsGenericType)
                    {
                        var definition = parentTypeInfo.GetGenericTypeDefinition();
                        if (definition == typeof(Grain<>) || definition == typeof(LogConsistentGrainBase<>))
                        {
                            var stateArg = parentType.GetGenericArguments()[0];
                            if (stateArg.GetTypeInfo().IsClass || stateArg.GetTypeInfo().IsValueType)
                            {
                                grainStateType = stateArg;
                                break;
                            }
                        }
                    }

                    parentType = parentTypeInfo.BaseType;
                }

                GrainTypeData typeData = GetTypeData(grainType, grainStateType);
                result.Add(className, typeData);
            }

            LogGrainTypesFound(logger, result);
            return result;
        }

        public IEnumerable<KeyValuePair<int, Type>> GetGrainMethodInvokerTypes(bool strict)
        {
            var result = new Dictionary<int, Type>();
            Type[] types = strict
                ? TypeUtils.GetTypes(TypeUtils.IsGrainMethodInvokerType, logger).ToArray()
                : TypeUtils.GetTypes(discoveredAssemblyLocations, TypeUtils.IsGrainMethodInvokerType, logger).ToArray();

            foreach (var type in types)
            {
                var attrib = type.GetTypeInfo().GetCustomAttribute<MethodInvokerAttribute>(true);
                int ifaceId = attrib.InterfaceId;

                if (result.ContainsKey(ifaceId))
                    throw new InvalidOperationException(string.Format("Grain method invoker classes {0} and {1} use the same interface id {2}", result[ifaceId].FullName, type.FullName, ifaceId));

                result[ifaceId] = type;
            }
            return result;
        }

        /// <summary>
        /// Get type data for the given grain type
        /// </summary>
        private static GrainTypeData GetTypeData(Type grainType, Type stateObjectType)
        {
            return grainType.GetTypeInfo().IsGenericTypeDefinition ?
                new GenericGrainTypeData(grainType, stateObjectType) :
                new GrainTypeData(grainType, stateObjectType);
        }

        private static void LogGrainTypesFound(LoggerImpl logger, Dictionary<string, GrainTypeData> grainTypeData)
        {
            var sb = new StringBuilder();
            sb.AppendLine(String.Format("Loaded grain type summary for {0} types: ", grainTypeData.Count));

            foreach (var grainType in grainTypeData.Values.OrderBy(gtd => gtd.Type.FullName))
            {
                // Skip system targets and Orleans grains
                var assemblyName = grainType.Type.GetTypeInfo().Assembly.FullName.Split(',')[0];
                if (!typeof(ISystemTarget).IsAssignableFrom(grainType.Type))
                {
                    int grainClassTypeCode = CodeGeneration.GrainInterfaceUtils.GetGrainClassTypeCode(grainType.Type);
                    sb.AppendFormat("Grain class {0}.{1} [{2} (0x{3})] from {4}.dll implementing interfaces: ",
                        grainType.Type.Namespace,
                        TypeUtils.GetTemplatedName(grainType.Type),
                        grainClassTypeCode,
                        grainClassTypeCode.ToString("X"),
                        assemblyName);
                    var first = true;

                    foreach (var iface in grainType.RemoteInterfaceTypes)
                    {
                        if (!first)
                            sb.Append(", ");

                        sb.Append(iface.Namespace).Append(".").Append(TypeUtils.GetTemplatedName(iface));

                        if (CodeGeneration.GrainInterfaceUtils.IsGrainType(iface))
                        {
                            int ifaceTypeCode = CodeGeneration.GrainInterfaceUtils.GetGrainInterfaceId(iface);
                            sb.AppendFormat(" [{0} (0x{1})]", ifaceTypeCode, ifaceTypeCode.ToString("X"));
                        }
                        first = false;
                    }
                    sb.AppendLine();
                }
            }
            var report = sb.ToString();
            logger.LogWithoutBulkingAndTruncating(Severity.Info, ErrorCode.Loader_GrainTypeFullList, report);
        }
    }
}
