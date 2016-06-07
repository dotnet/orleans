using System;

namespace Orleans.Runtime.Startup
{
    /// <summary>
    /// Interface for dynamic loading of ConfigureStartupBuilder
    /// </summary>
    public interface IStartupBuilder
    {
        /// <summary>
        /// Configure dependency injection for startup of this silo.
        /// </summary>
        /// <param name="startupTypeName"></param>
        /// <returns></returns>
        IServiceProvider ConfigureStartup(string startupTypeName);
    }
}
