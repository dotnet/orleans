using System;
using System.Diagnostics;
using System.Configuration.Install;
using System.ComponentModel;

namespace Orleans.Runtime.Counters
{
    /// <summary>
    /// Providers installer hooks for registering Orleans custom performance counters.
    /// </summary>
    [RunInstaller(true)]
    public class OrleansPerformanceCounterInstaller : Installer
    {
        /// <summary>
        /// Constructors -- Registers Orleans system performance counters, 
        /// plus any grain-specific activation conters that can be detected when this installer is run.
        /// </summary>
        public OrleansPerformanceCounterInstaller()
        {
            try
            {
                using (var myPerformanceCounterInstaller = new PerformanceCounterInstaller())
                {
                    myPerformanceCounterInstaller.CategoryName = OrleansPerfCounterManager.CATEGORY_NAME;
                    myPerformanceCounterInstaller.CategoryType = PerformanceCounterCategoryType.MultiInstance;
                    myPerformanceCounterInstaller.Counters.AddRange(OrleansPerfCounterManager.GetCounterCreationData());
                    Installers.Add(myPerformanceCounterInstaller);
                }
            }
            catch (Exception exc)
            {
                Context.LogMessage("Failed to install performance counters: " + exc.Message);
            }
        }
    }
}
