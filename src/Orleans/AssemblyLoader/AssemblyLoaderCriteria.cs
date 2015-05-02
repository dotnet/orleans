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

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

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

        internal static AssemblyLoaderPathNameCriterion ExcludeFileNames(IEnumerable<string> list)
        {
            return
                AssemblyLoaderPathNameCriterion.NewCriterion(
                    (string pathName, out IEnumerable<string> complaints) =>
                    {
                        var fileName = Path.GetFileName(pathName);
                        foreach (var i in list)
                        {
                            if (String.Equals(fileName, i, StringComparison.OrdinalIgnoreCase))
                            {
                                complaints = new string[] {"Assembly filename is excluded."};
                                return false;
                            }
                        }
                        complaints = null;
                        return true;
                    });
        }
    }
}
