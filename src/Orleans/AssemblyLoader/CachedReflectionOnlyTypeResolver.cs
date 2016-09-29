#if !NETSTANDARD
using System;
using System.IO;
using System.Reflection;

namespace Orleans.Runtime
{
    internal class CachedReflectionOnlyTypeResolver : CachedTypeResolver
    {
        private static readonly Logger logger;

        static CachedReflectionOnlyTypeResolver()
        {
            Instance = new CachedReflectionOnlyTypeResolver();
            logger = LogManager.GetLogger("AssemblyLoader.CachedReflectionOnlyTypeResolver");
        }

        public static new CachedReflectionOnlyTypeResolver Instance { get; private set; }

        protected override bool TryPerformUncachedTypeResolution(string name, out Type type)
        {
            AppDomain.CurrentDomain.ReflectionOnlyAssemblyResolve += OnReflectionOnlyAssemblyResolve;
            try
            {
                type = Type.ReflectionOnlyGetType(name, false, false);
                return type != null;
            }
            finally
            {
                AppDomain.CurrentDomain.ReflectionOnlyAssemblyResolve -= OnReflectionOnlyAssemblyResolve;
            }
        }

        public static Assembly OnReflectionOnlyAssemblyResolve(object sender, ResolveEventArgs args)
        {
            // loading into the reflection-only context doesn't resolve dependencies automatically.
            // we're faced with the choice of ignoring arguably false dependency-missing exceptions or
            // loading the dependent assemblies into the reflection-only context. 
            //
            // i opted to load dependencies (by implementing OnReflectionOnlyAssemblyResolve)
            // because it's an opportunity to quickly identify assemblies that wouldn't load under
            // normal circumstances.

            try
            {
                var name = AppDomain.CurrentDomain.ApplyPolicy(args.Name);
                return Assembly.ReflectionOnlyLoad(name);
            }
            catch (IOException)
            {
                if (logger.IsVerbose2)
                {
                    logger.Verbose2(FormatReflectionOnlyAssemblyResolveFailureMessage(sender, args));
                }

                var dirName = Path.GetDirectoryName(args.RequestingAssembly.Location);
                var assemblyName = new AssemblyName(args.Name);
                var fileName = string.Format("{0}.dll", assemblyName.Name);
                var pathName = Path.Combine(dirName, fileName);
                if (logger.IsVerbose2)
                {
                    logger.Verbose2("failed to find assembly {0} in {1}; searching for {2} instead.",
                        assemblyName.FullName, dirName, pathName);
                }

                try
                {
                    return Assembly.ReflectionOnlyLoadFrom(pathName);
                }
                catch (FileNotFoundException)
                {
                    if (logger.IsVerbose2)
                    {
                        logger.Verbose(FormatReflectionOnlyAssemblyResolveFailureMessage(sender, args));
                    }
                    throw;
                }
            }
        }

        private static string FormatReflectionOnlyAssemblyResolveFailureMessage(object sender, ResolveEventArgs args)
        {
            const string unavailable = "*unavailable*";

            string reqAsmName = unavailable;
            string reqAsmLoc = unavailable;

            if (args.RequestingAssembly == null)
                return string.Format( "failed to resolve assembly in reflection-only context: args.Name={0}, args.RequestingAssembly.FullName={1}, args.RequestingAssembly.Location={2}",
                    args.Name ?? unavailable, reqAsmName, reqAsmLoc);

            reqAsmName = args.RequestingAssembly.FullName;
            reqAsmLoc = args.RequestingAssembly.Location;

            return
                string.Format( "failed to resolve assembly in reflection-only context: args.Name={0}, args.RequestingAssembly.FullName={1}, args.RequestingAssembly.Location={2}",
                    args.Name ?? unavailable, reqAsmName, reqAsmLoc);
        }
    }
}
#endif