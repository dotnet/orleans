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

        private Lazy<Assembly[]> assembliesList = new Lazy<Assembly[]>(() =>
        {
            string path = ".";
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
                    Console.WriteLine($"Failed to load assembly '{assemblyName}'. Skipping. {ex}");
                }
            }
            assemblies.Add(typeof(Exception).GetTypeInfo().Assembly);
            return assemblies.ToArray();
        });

        public Assembly[] GetAssemblies()
        {
            // TODO: very naive approach to be replaced with IAssemblyCatalog or something like that.
            return assembliesList.Value;
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