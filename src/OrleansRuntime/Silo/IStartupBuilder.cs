using System;

namespace Orleans.Runtime.Startup
{
    /// <summary>
    /// Interface for dynamic loading of ConfigureStartupBuilder
    /// </summary>
    public interface IStartupBuilder
    {
        IServiceProvider ConfigureStartup(string startupTypeName);
    }
}
