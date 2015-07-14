/*
Project Orleans Cloud Service SDK ver. 1.0
 
Copyright (c) Microsoft Corporation
 
All rights reserved.
 
MIT License

Permission is hereby granted, free of charge, to any person obtaining a copy of this software and 
associated documentation files (the ""Software""), to deal in the Software without restriction,
including without limitation the rights to use, copy, modify, merge, publish, distribute, sublicense,
and/or sell copies of the Software, and to permit persons to whom the Software is furnished to do so,
subject to the following conditions:

The above copyright notice and this permission notice shall be included in all copies or substantial portions of the Software.

THE SOFTWARE IS PROVIDED *AS IS*, WITHOUT WARRANTY OF ANY KIND, EXPRESS OR IMPLIED, INCLUDING BUT NOT LIMITED TO
THE WARRANTIES OF MERCHANTABILITY, FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE AUTHORS
OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER LIABILITY, WHETHER IN AN ACTION OF CONTRACT,
TORT OR OTHERWISE, ARISING FROM, OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE SOFTWARE.
*/

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
