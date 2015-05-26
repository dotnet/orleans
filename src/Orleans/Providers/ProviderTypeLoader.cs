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
using System.Reflection;

using Orleans.Runtime;

namespace Orleans.Providers
{
    internal class ProviderTypeLoader
    {
        private readonly Func<Type, bool> condition;
        private readonly Action<Type> callback;
        private readonly HashSet<Type> alreadyProcessed;
        public bool IsActive { get; set; }

        private static readonly List<ProviderTypeLoader> managers;

        private static readonly TraceLogger logger = TraceLogger.GetLogger("ProviderTypeLoader", TraceLogger.LoggerType.Runtime);

        static ProviderTypeLoader()
        {
            managers = new List<ProviderTypeLoader>();
            AppDomain.CurrentDomain.AssemblyLoad += ProcessNewAssembly;
        }

        public ProviderTypeLoader(Func<Type, bool> condition, Action<Type> action)
        {
            this.condition = condition;
            callback = action;
            alreadyProcessed = new HashSet<Type>();
            IsActive = true;
         }


        public static void AddProviderTypeManager(Func<Type, bool> condition, Action<Type> action)
        {
            var manager = new ProviderTypeLoader(condition, action);

            lock (managers)
            {
                managers.Add(manager);
            }

            manager.ProcessLoadedAssemblies();
        }

        private void ProcessLoadedAssemblies()
        {
            lock (managers)
            {
                // Walk through already-loaded assemblies. 
                // We do this under the lock to avoid race conditions when an assembly is added 
                // while a type manager is initializing.
                foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
                {
                    ProcessAssemblyLocally(assembly);
                }
            }
        }

        private void ProcessType(Type type)
        {
            if (alreadyProcessed.Contains(type) || type.IsInterface || type.IsAbstract || !condition(type)) return;

            alreadyProcessed.Add(type);
            callback(type);
        }

        [System.Diagnostics.CodeAnalysis.SuppressMessage("Microsoft.Design", "CA1031:DoNotCatchGeneralExceptionTypes")]
        private void ProcessAssemblyLocally(Assembly assembly)
        {
            if (!IsActive) return;

            try
            {
                foreach (var type in assembly.DefinedTypes)
                {
                    ProcessType(type);
                }
            }
            catch (Exception exc)
            {
                logger.Warn(ErrorCode.Provider_AssemblyLoadError,
                    "Error searching for providers in assembly {0} -- ignoring this assembly. Error = {1}", assembly.FullName, exc);
            }
        }

        private static void ProcessNewAssembly(object sender, AssemblyLoadEventArgs args)
        {
            // We do this under the lock to avoid race conditions when an assembly is added 
            // while a type manager is initializing.
            lock (managers)
            {
                // We assume that it's better to fetch and iterate through the list of types once,
                // and the list of TypeManagers many times, rather than the other way around.
                // Certainly it can't be *less* efficient to do it this way.
                foreach (var type in args.LoadedAssembly.DefinedTypes)
                {
                    foreach (var mgr in managers)
                    {
                        if (mgr.IsActive)
                        {
                            mgr.ProcessType(type);
                        }
                    }
                }
            }
        }
    }
}
