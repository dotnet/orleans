using System.Diagnostics;
using System.Reflection;


namespace Orleans.Runtime
{
    internal class RuntimeVersion
    {
        /// <summary>
        /// The full version string of the Orleans runtime, eg: '2012.5.9.51607 Build:12345 Timestamp: 20120509-185359'
        /// </summary>
        public static string Current
        {
            get
            {
                Assembly thisProg = typeof(RuntimeVersion).Assembly;
                FileVersionInfo progVersionInfo = FileVersionInfo.GetVersionInfo(thisProg.Location);
                bool isDebug = IsAssemblyDebugBuild(thisProg);
                string productVersion = progVersionInfo.ProductVersion + (isDebug ? " (Debug)." : " (Release)."); // progVersionInfo.IsDebug; does not work
                return string.IsNullOrEmpty(productVersion) ? ApiVersion : productVersion;
            }
        }

        /// <summary>
        /// The ApiVersion of the Orleans runtime, eg: '1.0.0.0'
        /// </summary>
        public static string ApiVersion
        {
            get
            {
                AssemblyName libraryInfo = typeof(RuntimeVersion).Assembly.GetName();
                return libraryInfo.Version.ToString();
            }
        }

        /// <summary>
        /// The FileVersion of the Orleans runtime, eg: '2012.5.9.51607'
        /// </summary>
        public static string FileVersion
        {
            get
            {
                Assembly thisProg = typeof(RuntimeVersion).Assembly;
                FileVersionInfo progVersionInfo = FileVersionInfo.GetVersionInfo(thisProg.Location);
                string fileVersion = progVersionInfo.FileVersion;
                return string.IsNullOrEmpty(fileVersion) ? ApiVersion : fileVersion;

            }
        }

        /// <summary>
        /// The program name string for the Orleans runtime, eg: 'OrleansHost'
        /// </summary>
        public static string ProgramName
        {
            get
            {
                Assembly thisProg = Assembly.GetEntryAssembly() ?? typeof(RuntimeVersion).Assembly;
                AssemblyName progInfo = thisProg.GetName();
                return progInfo.Name;
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
