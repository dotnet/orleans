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
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;

using Orleans.Providers;
using Orleans.CodeGeneration;


namespace Orleans.Runtime
{
    internal class SiloAssemblyLoader
    {
        private readonly TraceLogger logger = TraceLogger.GetLogger("AssemblyLoader.Silo");
        private List<string> discoveredAssemblyLocations;

        public SiloAssemblyLoader()
        {
            LoadApplicationAssemblies();
        }

        private void LoadApplicationAssemblies()
        {
            var exeRoot = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location);
            var appRoot = Path.Combine(exeRoot, "Applications");
            var directories = new Dictionary<string, SearchOption>
                    {
                        { exeRoot, SearchOption.TopDirectoryOnly },
                        { appRoot, SearchOption.AllDirectories }
                    };

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
        }

        public IDictionary<string, GrainTypeData> GetGrainClassTypes(bool strict)
        {
            var result = new Dictionary<string, GrainTypeData>();
            IDictionary<string, Type> grainStateTypes = GetGrainStateTypes(strict);
            Type[] grainTypes = strict
                ? TypeUtils.GetTypes(TypeUtils.IsConcreteGrainClass).ToArray()
                : TypeUtils.GetTypes(discoveredAssemblyLocations, TypeUtils.IsConcreteGrainClass).ToArray();

            foreach (var grainType in grainTypes)
            {
                var className = TypeUtils.GetFullName(grainType);
                if (result.ContainsKey(className))
                    throw new InvalidOperationException(
                        string.Format("Precondition violated: GetLoadedGrainTypes should not return a duplicate type ({0})", className));
                
                var parameterizedName = grainType.Namespace + "." + TypeUtils.GetParameterizedTemplateName(grainType);
                Type grainStateType;
                grainStateTypes.TryGetValue(parameterizedName, out grainStateType);
                GrainTypeData typeData = GetTypeData(grainType, grainStateType);
                result.Add(className, typeData);
            }

            LogGrainTypesFound(logger, result);
            return result;
        }

        private IDictionary<string, Type> GetGrainStateTypes(bool strict)
        {
            var result = new Dictionary<string, Type>();
            Type[] types = strict
                ? TypeUtils.GetTypes(TypeUtils.IsGrainStateType).ToArray()
                : TypeUtils.GetTypes(discoveredAssemblyLocations, TypeUtils.IsGrainStateType).ToArray();

            foreach (var type in types)
            {
                var attr = (GrainStateAttribute)type.GetCustomAttributes(typeof(GrainStateAttribute), true).Single();
                if (result.ContainsKey(attr.ForGrainType))
                    throw new InvalidOperationException(
                        string.Format("Grain class {0} is already associated with grain state object type {1}", attr.ForGrainType, type.FullName));
            
                result.Add(attr.ForGrainType, type);
            }
            return result;
        }

        public IEnumerable<KeyValuePair<int, Type>> GetGrainMethodInvokerTypes(bool strict)
        {
            var result = new Dictionary<int, Type>();
            Type[] types = strict
                ? TypeUtils.GetTypes(TypeUtils.IsGrainMethodInvokerType).ToArray()
                : TypeUtils.GetTypes(discoveredAssemblyLocations, TypeUtils.IsGrainMethodInvokerType).ToArray();

            foreach (var type in types)
            {
                var attrib = (MethodInvokerAttribute)type.GetCustomAttributes(typeof(MethodInvokerAttribute), true).Single();
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
            return grainType.IsGenericTypeDefinition ? 
                new GenericGrainTypeData(grainType, stateObjectType) : 
                new GrainTypeData(grainType, stateObjectType);
        }

        private static void LogGrainTypesFound(TraceLogger logger, Dictionary<string, GrainTypeData> grainTypeData)
        {
            var sb = new StringBuilder();
            sb.AppendLine(String.Format("Loaded grain type summary for {0} types: ", grainTypeData.Count));

            foreach (var grainType in grainTypeData.Values.OrderBy(gtd => gtd.Type.Name))
            {
                // Skip system targets and Orleans grains
                var assemblyName = grainType.Type.Assembly.FullName.Split(',')[0];
                if (!typeof(ISystemTarget).IsAssignableFrom(grainType.Type))
                {
                    int grainClassTypeCode = CodeGeneration.GrainInterfaceData.GetGrainClassTypeCode(grainType.Type);
                    sb.AppendFormat("Grain class {0} [{1} (0x{2})] from {3}.dll implementing interfaces: ", 
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

                        if (CodeGeneration.GrainInterfaceData.IsGrainType(iface))
                        {
                            int ifaceTypeCode = CodeGeneration.GrainInterfaceData.GetGrainInterfaceId(iface);
                            sb.AppendFormat(" [{0} (0x{1})]", ifaceTypeCode, ifaceTypeCode.ToString("X"));
                        }
                        first = false;
                    }
                    sb.AppendLine();
                }
            }
            var report = sb.ToString();
            logger.LogWithoutBulkingAndTruncating(Logger.Severity.Info, ErrorCode.Loader_GrainTypeFullList, report);
        }
    }
}