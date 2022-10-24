using System.Diagnostics;
using System.Reflection;

namespace Orleans.Runtime
{
    internal static class RuntimeVersion
    {
        /// <summary>
        /// The full version string of the Orleans runtime, eg: '2012.5.9.51607 Build:12345 Timestamp: 20120509-185359'
        /// </summary>
        public static string Current
        {
            get
            {
                Assembly thisProg = typeof(RuntimeVersion).Assembly;
                var ApiVersion = thisProg.GetName().Version.ToString();
                if (string.IsNullOrWhiteSpace(thisProg.Location))
                {
                    return ApiVersion;
                }
                FileVersionInfo progVersionInfo = FileVersionInfo.GetVersionInfo(thisProg.Location);
                bool isDebug = IsAssemblyDebugBuild(thisProg);
                string productVersion = progVersionInfo.ProductVersion + (isDebug ? " (Debug)." : " (Release)."); // progVersionInfo.IsDebug; does not work
                return string.IsNullOrEmpty(productVersion) ? ApiVersion : productVersion;
            }
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
