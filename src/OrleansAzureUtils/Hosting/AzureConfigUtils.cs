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
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Reflection;
using System.Text;
using System.Web;

namespace Orleans.Runtime.Host
{
    /// <summary>
    /// Various utility functions to make it easier to access and handle Azure configuration information.
    /// </summary>
    public static class AzureConfigUtils
    {
        ///<summary>
        /// Return the default file location for the Orleans client config file (ClientConfiguration.xml)
        ///</summary>
        ///<exception cref="FileNotFoundException">If client config file cannot be located</exception>
        public static FileInfo ClientConfigFileLocation
        {
            get
            {
                const string cfgFileName = "ClientConfiguration.xml";
                return FindConfigFile(cfgFileName, "Orleans client config");
            }
        }

        ///<summary>
        /// Return the default file location for the Orleans silo config file (OrleansConfiguration.xml)
        ///</summary>
        ///<exception cref="FileNotFoundException">If silo config file cannot be located</exception>
        public static FileInfo SiloConfigFileLocation
        {
            get
            {
                const string cfgFileName = "OrleansConfiguration.xml";
                return FindConfigFile(cfgFileName, "Orleans silo config");
            }
        }

        /// <summary>
        /// Search for the specified config file 
        /// by checking each of the expected app directory locations used by Azure.
        /// </summary>
        /// <param name="cfgFileName">Name of the file to be found.</param>
        /// <param name="what">Short description of the file to be found.</param>
        /// <returns>Location if the file, if found, otherwise FileNotFound exeception will be thrown.</returns>
        /// <exception cref="FileNotFoundException">If the specified config file cannot be located</exception>
        internal static FileInfo FindConfigFile(string cfgFileName, string what)
        {
            DirectoryInfo[] searchedLocations = AppDirectoryLocations;

            foreach (var dir in searchedLocations)
            {
                var file = new FileInfo(Path.Combine(dir.FullName, cfgFileName));
                if (file.Exists) 
                    return file;
            }

            // Report error using first (expected) search location
            var sb = new StringBuilder();
            sb.AppendFormat("Cannot find {0} file. Tried locations:", what);
            foreach (var loc in searchedLocations)
                sb.Append(" ").Append(loc.FullName);
            
            Trace.TraceError(sb.ToString());
            throw new FileNotFoundException(sb.ToString(), cfgFileName);
        }

        /// <summary>
        /// Return the expected possible base locations for the Azure app directory we are being run from
        /// </summary>
        /// <returns>Enererable list of app directory locations</returns>
        public static DirectoryInfo[] AppDirectoryLocations
        {
            get { return appDirectoryLocations ?? (appDirectoryLocations = FindAppDirectoryLocations().ToArray()); }
        }
        
        private static DirectoryInfo[] appDirectoryLocations;

        /// <summary>
        /// Return the expected possible base locations for the Azure app directory we are being run from
        /// </summary>
        /// <returns>Enererable list of app directory locations</returns>
        private static IEnumerable<DirectoryInfo> FindAppDirectoryLocations()
        {
            // App directory locations:
            // Worker Role code:            {RoleRoot}\approot
            // WebRole – Role startup code: {RoleRoot}\approot\bin
            // WebRole - IIS web app code:  {ServerRoot}
            // WebRole - IIS Express:       {ServerRoot}\bin

            var locations = new List<DirectoryInfo>();
            
            Utils.SafeExecute(() =>
            {
                var roleRootDir = Environment.GetEnvironmentVariable("RoleRoot");
                if (roleRootDir != null)
                {
                    // Being called from Role startup code - either Azure WorkerRole or WebRole
                    Assembly assy = typeof(AzureConfigUtils).GetTypeInfo().Assembly;
                    string appRootPath = Path.GetDirectoryName(assy.Location);
                    if (appRootPath != null)
                        locations.Add(new DirectoryInfo(appRootPath));
                }
            });

            Utils.SafeExecute(() =>
            {
                var roleRootDir = Environment.GetEnvironmentVariable("RoleRoot");
                if (roleRootDir != null)
                {
                    string appRootPath = Path.GetDirectoryName(roleRootDir + Path.DirectorySeparatorChar + "approot" + Path.DirectorySeparatorChar);
                    if (appRootPath != null)
                        locations.Add(new DirectoryInfo(appRootPath));
                }
            });

            Utils.SafeExecute(() =>
            {
                Assembly assy = typeof(AzureConfigUtils).GetTypeInfo().Assembly;
                string appRootPath = Path.GetDirectoryName(new Uri(assy.CodeBase).LocalPath);
                if (appRootPath != null)
                    locations.Add(new DirectoryInfo(appRootPath));
            });

            Utils.SafeExecute(() =>
            {
                // Try using Server.MapPath to resolve for web roles running in IIS web apps
                if (HttpContext.Current != null)
                {
                    string appRootPath = HttpContext.Current.Server.MapPath(@"~\");
                    if (appRootPath != null)
                        locations.Add(new DirectoryInfo(appRootPath));
                }
            });


            Utils.SafeExecute(() =>
            {
                // Try using HostingEnvironment.MapPath to resolve for web roles running in IIS Express
                // https://orleans.codeplex.com/discussions/547617
                string appRootPath = System.Web.Hosting.HostingEnvironment.MapPath("~/bin/");
                if (appRootPath != null)
                    locations.Add(new DirectoryInfo(appRootPath));
            });

            // Try current directory
            locations.Add(new DirectoryInfo("."));
            return locations;

            // We have run out of ideas where to look!
            // Searched locations = 
            //   RoleRoot
            //   HttpContext.Current.Server.MapPath
            //   System.Web.Hosting.HostingEnvironment.MapPath
            //   Current directory
        }
    }
}
