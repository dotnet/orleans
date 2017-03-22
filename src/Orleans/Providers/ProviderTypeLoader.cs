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

        private static readonly Logger logger = LogManager.GetLogger("ProviderTypeLoader", LoggerType.Runtime);

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

        private void ProcessType(TypeInfo typeInfo)
        {
            var type = typeInfo.AsType();
            if (alreadyProcessed.Contains(type) || typeInfo.IsInterface || typeInfo.IsAbstract || !condition(type)) return;

            alreadyProcessed.Add(type);
            callback(type);
        }

        [System.Diagnostics.CodeAnalysis.SuppressMessage("Microsoft.Design", "CA1031:DoNotCatchGeneralExceptionTypes")]
        private void ProcessAssemblyLocally(Assembly assembly)
        {
            if (!IsActive) return;

            try
            {
                foreach (var type in TypeUtils.GetDefinedTypes(assembly, logger))
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
#if !NETSTANDARD
            // If the assembly is loaded for reflection only avoid processing it.
            if (args.LoadedAssembly.ReflectionOnly)
            {
                return;
            }
#endif

            // We do this under the lock to avoid race conditions when an assembly is added 
            // while a type manager is initializing.
            lock (managers)
            {
                // We assume that it's better to fetch and iterate through the list of types once,
                // and the list of TypeManagers many times, rather than the other way around.
                // Certainly it can't be *less* efficient to do it this way.
                foreach (var type in TypeUtils.GetDefinedTypes(args.LoadedAssembly, logger))
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
