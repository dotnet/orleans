#if NETSTANDARD_TODO
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
#pragma warning disable 67

namespace Orleans
{
    internal sealed class AppDomain
    {
        private readonly object assemblyLoadLock = new object();
        private Assembly[] loadedAssembliesArray;
        private HashSet<Assembly> loadedAssembliesHashSet;

        public delegate void AssemblyLoadEventHandler(object sender, AssemblyLoadEventArgs args);

        public delegate void UnhandledExceptionEventHandler(object sender, UnhandledExceptionEventArgs e);

        public static AppDomain CurrentDomain { get; private set; }
        public event AssemblyLoadEventHandler AssemblyLoad;
        public event EventHandler DomainUnload;
        public event EventHandler ProcessExit;
        public event UnhandledExceptionEventHandler UnhandledException;

        static AppDomain()
        {
            CurrentDomain = new AppDomain();
        }

        private AppDomain()
        {
            // this does not work in .NET Standard, only CoreCLR: https://github.com/dotnet/corefx/issues/8453
            //System.Runtime.Loader.AssemblyLoadContext.Default.Unloading += _ =>
            //{
            //    DomainUnload?.Invoke(this, EventArgs.Empty);
            //    ProcessExit?.Invoke(this, EventArgs.Empty);
            //};
        }

        public Assembly[] GetAssemblies()
        {
            lock (assemblyLoadLock)
            {
                if (loadedAssembliesHashSet == null)
                {
                    loadedAssembliesHashSet = GetAssembliesList();
                    loadedAssembliesArray = loadedAssembliesHashSet.ToArray();
                }

                // TODO: very naive approach to be replaced with IAssemblyCatalog or something like that.
                return loadedAssembliesArray;
            }
        }

        public void AddAssembly(Assembly asm)
        {
            lock (assemblyLoadLock)
            {
                if (loadedAssembliesHashSet == null)
                {
                    loadedAssembliesHashSet = GetAssembliesList();
                }

                if (loadedAssembliesHashSet.Add(asm) || loadedAssembliesArray == null)
                {
                    loadedAssembliesArray = loadedAssembliesHashSet.ToArray();
                }
            }

            var handler = this.AssemblyLoad;
            if (handler != null)
            { 
                handler(this, new AssemblyLoadEventArgs(asm));
            }
        }

        private static HashSet<Assembly> GetAssembliesList()
        {
            string path = AppContext.BaseDirectory;
            var assemblyFiles = Directory.EnumerateFiles(path, "*.dll");
            var assemblyNames = assemblyFiles.Select(Path.GetFileNameWithoutExtension).ToList();
            var assemblies = new HashSet<Assembly>();
            foreach (var assemblyName in assemblyNames)
            {
                try
                {
                    assemblies.Add(Assembly.Load(new AssemblyName(assemblyName)));
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"WARNING: Failed to load assembly '{assemblyName}'. Skipping. {ex}");
                }
            }

            assemblies.Add(typeof(Exception).GetTypeInfo().Assembly);
            foreach (var assembly in assemblies.ToList())
            {
                LoadDependencies(assemblies, assembly);
            }

            return assemblies;
        }

        private static void LoadDependencies(HashSet<Assembly> loadedAssemblies, Assembly fromAssembly)
        {
            foreach (var reference in fromAssembly.GetReferencedAssemblies())
            {
                try
                {
                    var asm = Assembly.Load(reference);
                    if (loadedAssemblies.Add(asm))
                    {
                        LoadDependencies(loadedAssemblies, asm);
                    }
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"WARNING: Unable to load assembly {reference.FullName} referenced by {fromAssembly.FullName}. Skipping. {ex}");
                }
            }
        }
    }

    internal class AssemblyLoadEventArgs : EventArgs
    {
        public Assembly LoadedAssembly { get; }

        public AssemblyLoadEventArgs(Assembly loadedAssembly)
        {
            LoadedAssembly = loadedAssembly;
        }
    }

    internal class UnhandledExceptionEventArgs : EventArgs
    {
        public UnhandledExceptionEventArgs(Object exception, bool isTerminating)
        {
            this.ExceptionObject = exception;
            this.IsTerminating = isTerminating;
        }
        public Object ExceptionObject { get; }
        public bool IsTerminating { get; }
    }
}
#endif