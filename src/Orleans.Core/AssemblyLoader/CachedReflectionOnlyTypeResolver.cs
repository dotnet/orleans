using System;
using System.IO;
using System.Reflection;

namespace Orleans.Runtime
{
    internal class CachedReflectionOnlyTypeResolver : CachedTypeResolver
    {
        /// <inheritdoc />
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
                var dirName = Path.GetDirectoryName(args.RequestingAssembly.Location);
                var assemblyName = new AssemblyName(args.Name);
                var fileName = string.Format("{0}.dll", assemblyName.Name);
                var pathName = Path.Combine(dirName, fileName);
                return Assembly.ReflectionOnlyLoadFrom(pathName);
            }
        }
    }
}
