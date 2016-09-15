#if NETSTANDARD_TODO
using System;
using System.Reflection;
using Orleans.Runtime;

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

        public Assembly[] GetAssemblies()
        {
            // TODO: replace with IAssemblyCatalog or something like that.
            Assembly[] assemblies =
            {
                typeof(Exception).GetTypeInfo().Assembly,
                typeof(AssemblyProcessor).GetTypeInfo().Assembly,
                Assembly.GetEntryAssembly(),
            };
            return assemblies;
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