namespace Orleans.PlatformServices
{
    using System;
    using System.Reflection;

    public static class PlatformAssemblyLoader
    {
        public static Assembly LoadFromBytes(byte[] assembly, byte[] debugSymbols = null)
        {
            if (assembly == null)
            {
                throw new ArgumentNullException(nameof(assembly));
            }

#if NETSTANDARD2_0
            return Assembly.Load(assembly, debugSymbols);
#else
            throw new NotImplementedException();
#endif
        }

        public static Assembly LoadFromAssemblyPath(string assemblyPath)
        {
#if NETSTANDARD2_0
            return Assembly.LoadFrom(assemblyPath);
#else
            throw new NotImplementedException();
#endif
        }
    }
}