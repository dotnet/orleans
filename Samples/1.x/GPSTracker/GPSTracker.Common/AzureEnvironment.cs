using System;
using Microsoft.WindowsAzure.ServiceRuntime;

namespace GPSTracker.Common
{
    /// <summary>
    /// Helper class to isolate from Microsoft.WindowsAzure.ServiceRuntime.dll and its failures to load outside of Azure
    /// </summary>
    public class AzureEnvironment
    {
        private static bool alreadyChecked;
        private static bool inAzure;

        /// <summary>
        /// Returns true if we are running in Azure
        /// </summary>
        public static bool IsInAzure
        {
            get
            {
                if(alreadyChecked) return inAzure;

                lock (typeof (AzureEnvironment))
                {
                    if (alreadyChecked) return inAzure;

                    try
                    {
                        if (RoleEnvironment.IsAvailable)
                            inAzure = true;
                    }
                    catch (Exception)
                    {
                        inAzure = false;
                    }

                    alreadyChecked = true;
                }
                
                return inAzure;
            }
        }
    }
}
