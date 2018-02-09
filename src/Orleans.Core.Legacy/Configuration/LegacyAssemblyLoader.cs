using System;
using System.Linq;
using System.Reflection;

using Orleans.Runtime;

namespace Orleans.Core.Legacy
{
    internal static class LegacyAssemblyLoader
    {
        /// <summary>
        /// Loads the provided assembly and attempts to create an instance of the first type assignable to <typeparamref name="T"/> found within.
        /// </summary>
        /// <typeparam name="T">The type which the result implements.</typeparam>
        /// <param name="assemblyName">The assembly to load and search.</param>
        /// <returns>The created instance of <typeparamref name="T"/>.</returns>
        public static T LoadAndCreateInstance<T>(string assemblyName) where T : class
        {
            var assembly = Assembly.Load(new AssemblyName(assemblyName));
            var foundType = TypeUtils.GetTypes(assembly, type => typeof(T).IsAssignableFrom(type), null).FirstOrDefault();
            if (foundType == null)
            {
                throw new InvalidOperationException($"No type assignable to {typeof(T)} was found in assembly {assembly}.");
            }

            return (T)Activator.CreateInstance(foundType);
        }
    }
}