#if !NETSTANDARD_TODO
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;

namespace Orleans.Runtime
{
    internal static class AssemblyLoaderCriteria
    {
        internal static readonly AssemblyLoaderPathNameCriterion ExcludeResourceAssemblies =
            AssemblyLoaderPathNameCriterion.NewCriterion(
                (string pathName, out IEnumerable<string> complaints) =>
                {
                    if (pathName.EndsWith(".resources.dll", StringComparison.OrdinalIgnoreCase))
                    {
                        complaints = new string[] {"Assembly filename indicates that it is a resource assembly."};
                        return false;                        
                    }
                    else
                    {
                        complaints = null;
                        return true;
                    }
                });

        internal static AssemblyLoaderReflectionCriterion LoadTypesAssignableFrom(Type[] requiredTypes)
        {
            // any types provided must be converted to reflection-only
            // types, or they aren't comparable with other reflection-only 
            // types.
            requiredTypes = requiredTypes.Select(TypeUtils.ToReflectionOnlyType).ToArray();
            string[] complaints = new string[requiredTypes.Length];
            for (var i = 0; i < requiredTypes.Length; ++i)
            {
                complaints[i] = String.Format("Assembly contains no types assignable from {0}.", requiredTypes[i].FullName);
            }  

            return
                AssemblyLoaderReflectionCriterion.NewCriterion(
                    (Type type, out IEnumerable<string> ignored) =>
                    {
                        ignored = null;
                        foreach (var requiredType in requiredTypes)
                        {
                            if (requiredType.IsAssignableFrom(type))
                            {
                                //  we found a match! load the assembly.
                                return true;
                            }
                        }
                        return false;  
                    },
                    complaints);
        }

        internal static AssemblyLoaderReflectionCriterion LoadTypesAssignableFrom(Type requiredType)
        {
            return LoadTypesAssignableFrom(new [] {requiredType});
        }

        internal static readonly string[] 
            SystemBinariesList = 
                new string[]
                    {
                        "Orleans.dll",
                        "OrleansRuntime.dll"
                    };

        internal static AssemblyLoaderPathNameCriterion ExcludeSystemBinaries()
        {
            return ExcludeFileNames(SystemBinariesList);
        }

        private static AssemblyLoaderPathNameCriterion GetFileNameCriterion(IEnumerable<string> list, bool includeList)
        {
            return
                AssemblyLoaderPathNameCriterion.NewCriterion(
                    (string pathName, out IEnumerable<string> complaints) =>
                    {
                        var fileName = Path.GetFileName(pathName);
                        foreach (var i in list)
                        {
                            if (String.Equals(fileName, i, StringComparison.OrdinalIgnoreCase) ^ includeList)
                            {
                                complaints = new string[] {"Assembly filename is excluded."};
                                return false;
                            }
                        }
                        complaints = null;
                        return true;
                    });
        }

        internal static AssemblyLoaderPathNameCriterion ExcludeFileNames(IEnumerable<string> list)
        {
            return GetFileNameCriterion(list, false);
        }

        internal static AssemblyLoaderPathNameCriterion IncludeFileNames(IEnumerable<string> list)
        {
            return GetFileNameCriterion(list, true);
        }
    }
}
#endif