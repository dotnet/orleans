namespace Orleans.PlatformServices
{
    using System;
    using System.Reflection;
#if NETCORE
    using System.IO;
    using System.Runtime.Loader;
#endif

    public static class PlatformAssemblyLoader
    {
        public static Assembly LoadFromBytes(byte[] assembly, byte[] debugSymbols = null)
        {
            if (assembly == null)
            {
                throw new ArgumentNullException(nameof(assembly));
            }

#if NETCORE
            using (var assemblyStream = new MemoryStream(assembly))
            {
                if (debugSymbols != null)
                {
                    using (var debugSymbolStream = new MemoryStream(debugSymbols))
                    {
                        return AssemblyLoadContext.Default.LoadFromStream(assemblyStream, debugSymbolStream);
                    }
                }
                else
                {
                    return AssemblyLoadContext.Default.LoadFromStream(assemblyStream);
                }
            }
#elif NET46
            return Assembly.Load(assembly, debugSymbols);
#else
            throw new NotImplementedException();
#endif
        }

        public static Assembly LoadFromAssemblyPath(string assemblyPath)
        {
#if NETCORE
            return AssemblyLoadContext.Default.LoadFromAssemblyPath(assemblyPath);
#elif NET46
            return Assembly.LoadFrom(assemblyPath);
#else
            throw new NotImplementedException();
#endif
        }
    }
}