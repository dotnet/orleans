using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;

namespace Orleans.Runtime
{
    public class AssemblyLoader
    {
        public IAssemblyCatalog AssemblyCatalog { get; private set; }
        private readonly Logger logger;

        private AssemblyLoader(IAssemblyCatalog catalog)
        {
            AssemblyCatalog = catalog;

            AssemblyProcessor.ProcessAssemblies(catalog.GetAssemblies());
        }

        internal static AssemblyLoader NewAssemblyLoader(IAssemblyCatalog catalog)
        {
            if (catalog == null) throw new ArgumentNullException(nameof(catalog));

            return new AssemblyLoader(catalog);
        }

        public static T TryLoadAndCreateInstance<T>(string assemblyName, Logger logger) where T : class
        {
            try
            {
                var assembly = Assembly.Load(new AssemblyName(assemblyName));
                var foundType =
                    TypeUtils.GetTypes(
                        assembly,
                        type =>
                        typeof(T).IsAssignableFrom(type) && !type.GetTypeInfo().IsInterface
                        && type.GetTypeInfo().GetConstructor(Type.EmptyTypes) != null, logger).FirstOrDefault();
                if (foundType == null)
                {
                    return null;
                }

                return (T)Activator.CreateInstance(foundType, true);
            }
            catch (FileNotFoundException exception)
            {
                logger.Warn(ErrorCode.Loader_TryLoadAndCreateInstance_Failure, exception.Message, exception);
                return null;
            }
            catch (Exception exc)
            {
                logger.Error(ErrorCode.Loader_TryLoadAndCreateInstance_Failure, exc.Message, exc);
                throw;
            }
        }

        public static T LoadAndCreateInstance<T>(string assemblyName, Logger logger) where T : class
        {
            try
            {
                var assembly = Assembly.Load(new AssemblyName(assemblyName));
                var foundType = TypeUtils.GetTypes(assembly, type => typeof(T).IsAssignableFrom(type), logger).First();

                return (T)Activator.CreateInstance(foundType, true);
            }
            catch (Exception exc)
            {
                logger.Error(ErrorCode.Loader_LoadAndCreateInstance_Failure, exc.Message, exc);
                throw;
            }
        }

        private static Assembly MatchWithLoadedAssembly(AssemblyName searchFor, IEnumerable<Assembly> assemblies)
        {
            foreach (var assembly in assemblies)
            {
                var searchForFullName = searchFor.FullName;
                var candidateFullName = assembly.FullName;
                if (String.Equals(candidateFullName, searchForFullName, StringComparison.OrdinalIgnoreCase))
                {
                    return assembly;
                }
            }
            return null;
        }

        private static Assembly MatchWithLoadedAssembly(AssemblyName searchFor, AppDomain appDomain)
        {
            return
                MatchWithLoadedAssembly(searchFor, appDomain.GetAssemblies()) ??
                MatchWithLoadedAssembly(searchFor, appDomain.ReflectionOnlyGetAssemblies());
        }

        private static Assembly MatchWithLoadedAssembly(AssemblyName searchFor)
        {
            return MatchWithLoadedAssembly(searchFor, AppDomain.CurrentDomain);
        }
    }
}
