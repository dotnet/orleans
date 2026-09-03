using System.Diagnostics;
using System.Reflection;

namespace Orleans.Runtime
{
    internal static class RuntimeVersion
    {
        /// <summary>
        /// The informational version of the Orleans runtime and its build configuration,
        /// or the assembly version when informational version metadata is unavailable.
        /// </summary>
        public static string Current => GetVersion(typeof(RuntimeVersion).Assembly);

        internal static string GetVersion(Assembly assembly)
        {
            if (assembly is null)
            {
                throw new ArgumentNullException(nameof(assembly));
            }

            var apiVersion = assembly.GetName().Version!.ToString();
            var productVersion = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
            return string.IsNullOrWhiteSpace(productVersion)
                ? apiVersion
                : productVersion + (IsAssemblyDebugBuild(assembly) ? " (Debug)." : " (Release).");
        }

        /// <summary>
        /// Returns a value indicating whether the provided <paramref name="assembly"/> was built in debug mode.
        /// </summary>
        /// <param name="assembly">
        /// The assembly to check.
        /// </param>
        /// <returns>
        /// A value indicating whether the provided assembly was built in debug mode.
        /// </returns>
        internal static bool IsAssemblyDebugBuild(Assembly assembly)
        {
            foreach (var debuggableAttribute in assembly.GetCustomAttributes<DebuggableAttribute>())
            {
                return debuggableAttribute.IsJITTrackingEnabled;
            }
            return false;
        }
    }
}
