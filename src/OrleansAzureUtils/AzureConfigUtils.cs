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

﻿using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Reflection;
using System.Text;
using System.Web;
using Microsoft.WindowsAzure.ServiceRuntime;

namespace Orleans.Runtime.Host
{
    /// <summary>
    /// Various utility functions to make it easier to access and handle Azure configuration information.
    /// </summary>
    public static class AzureConfigUtils
    {
        /// <summary>
        /// Try to determine the base location for the Azure app directory we are being run from
        /// </summary>
        /// <returns>App directory this library is being run from</returns>
        /// <exception cref="FileNotFoundException">If unable to determine our app directory location</exception>
        [Obsolete("Use the AppDirectoryLocations enumerable instead")]
        public static DirectoryInfo AzureAppDirectory
        {
            get
            {
                DirectoryInfo[] searchedLocations = AppDirectoryLocations;

                foreach (var dir in searchedLocations)
                    if (dir.Exists)
                        return dir;
                
                // Report error using first (expected) search location
                var sb = new StringBuilder();
                sb.Append("Cannot find Azure app directyory. Tried locations:");
                foreach (var loc in searchedLocations)
                    sb.Append(" ").Append(loc.FullName);
                
                Trace.TraceError(sb.ToString());
                throw new FileNotFoundException(sb.ToString(), "Azure AppRoot");
            }
        }

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

            string appRootPath;

            var roleRootDir = Environment.GetEnvironmentVariable("RoleRoot");
            if (roleRootDir != null)
            {
                // Being called from Role startup code - either Azure WorkerRole or WebRole
                Assembly assy = Assembly.GetExecutingAssembly();
                appRootPath = Path.GetDirectoryName(assy.Location);
                if (appRootPath != null)
                    yield return new DirectoryInfo(appRootPath);
            }

            // Try using Server.MapPath to resolve for web roles running in IIS web apps
            appRootPath = HttpContext.Current.Server.MapPath(@"~\");
            if (appRootPath != null) 
                yield return new DirectoryInfo(appRootPath);

            // Try using HostingEnvironment.MapPath to resolve for web roles running in IIS Express
            // https://orleans.codeplex.com/discussions/547617
            appRootPath = System.Web.Hosting.HostingEnvironment.MapPath("~/bin/");
            if (appRootPath != null)
                yield return new DirectoryInfo(appRootPath);

            // Try current directory
            yield return new DirectoryInfo(".");

            // We have run out of ideas where to look!
            // Searched locations = 
            //   RoleRoot
            //   HttpContext.Current.Server.MapPath
            //   System.Web.Hosting.HostingEnvironment.MapPath
            //   Current directory
        }

        /// <summary>
        /// Get the instance named for the specified Azure role instance
        /// </summary>
        /// <param name="roleInstance">Azure role instance information</param>
        /// <returns>Instance name for this role</returns>
        public static string GetInstanceName(RoleInstance roleInstance)
        {
            // Try to remove the deploymentId part of the instanceId, if it is there.
            // The goal is mostly to remove undesired characters that are being added to deploymentId when run in Azure emulator.
            // Notice that we should use RoleEnvironment.DeploymentId and not the deploymentId that is being passed to us by the user, 
            // since in general this may be an arbitrary string and thus we will not remove the RoleEnvironment.DeploymentId correctly.
            string deploymentId = RoleEnvironment.DeploymentId;
            string instanceId = roleInstance.Id;
            return GetInstanceNameInternal(deploymentId, instanceId);
        }

        private static string GetInstanceNameInternal(string deploymentId, string instanceId)
        {
            return instanceId.Length > deploymentId.Length && instanceId.StartsWith(deploymentId)
                ? instanceId.Substring(deploymentId.Length + 1)
                : instanceId;
        }

        /// <summary>
        /// Get the instance name for the current Azure role instance
        /// </summary>
        /// <returns>Instance name for the current role instance</returns>
        public static string GetMyInstanceName()
        {
            return GetInstanceName(RoleEnvironment.CurrentRoleInstance);
        }

        /// <summary>
        /// List instance details of the specified roles
        /// </summary>
        /// <param name="roles">Dictionary contining the roles to be listed, indexed by instance name</param>
        public static void ListAllRoleDetails(IDictionary<string, Role> roles)
        {
            if (roles == null) throw new ArgumentNullException("roles", "No roles dictionary provided");

            foreach (string name in roles.Keys)
            {
                Role r = roles[name];
                foreach (RoleInstance instance in r.Instances)
                    ListRoleInstanceDetails(instance);
            }
        }

        /// <summary>
        /// List details of the specified role instance
        /// </summary>
        /// <param name="instance">role instance to be listed</param>
        public static void ListRoleInstanceDetails(RoleInstance instance)
        {
            if (instance == null) throw new ArgumentNullException("instance", "No RoleInstance data provided");

            Trace.TraceInformation("Role={0} Instance: Id={1} FaultDomain={2} UpdateDomain={3}",
                instance.Role.Name, instance.Id, instance.FaultDomain, instance.UpdateDomain);

            ListRoleInstanceEndpoints(instance);
        }

        /// <summary>
        /// List endpoint details of the specified role instance
        /// </summary>
        /// <param name="instance">role instance to be listed</param>
        public static void ListRoleInstanceEndpoints(RoleInstance instance)
        {
            if (instance == null) throw new ArgumentNullException("instance", "No RoleInstance data provided");

            foreach (string endpointName in instance.InstanceEndpoints.Keys)
            {
                Trace.TraceInformation("Role={0} Instance={1} EndpointName={2}", 
                    instance.Role.Name, instance.Id, endpointName);
                
                ListEndpointDetails(instance.InstanceEndpoints[endpointName]);
            }
        }

        internal static void ListEndpointDetails(RoleInstanceEndpoint endpoint)
        {
            if (endpoint == null) throw new ArgumentNullException("endpoint", "No RoleInstanceEndpoint data provided");

            Trace.TraceInformation("Role={0} Instance={1} Address={2} Port={3}",
                endpoint.RoleInstance.Role.Name, endpoint.RoleInstance.Id, endpoint.IPEndpoint.Address, endpoint.IPEndpoint.Port);
        }

        /// <summary>
        /// Get the endpoint details of the specified role
        /// </summary>
        /// <param name="role">role to be inspected</param>
        /// <returns>The list of <c>RoleInstanceEndpoint</c> data associated with the specified Azure role.</returns>
        public static List<RoleInstanceEndpoint> GetRoleEndpoints(Role role)
        {
            if (role == null) throw new ArgumentNullException("role", "No Role data provided");

            var endpoints = new List<RoleInstanceEndpoint>();
            foreach (RoleInstance instance in role.Instances)
                endpoints.AddRange(instance.InstanceEndpoints.Values);
            
            return endpoints;
        }

        /// <summary>
        /// Get the endpoint IP address details of the specified role
        /// </summary>
        /// <param name="roleName">Name of the role to be inspected</param>
        /// <param name="endpointName">Name of the endpoint to be inspected</param>
        /// <returns>The list of <c>IPEndPoint</c> data for the specified endpoint associated with the specified Azure role name.</returns>
        public static List<IPEndPoint> GetRoleInstanceEndpoints(string roleName, string endpointName)
        {
            var endpoints = new List<IPEndPoint>();

            if (RoleEnvironment.Roles.ContainsKey(roleName))
            {
                foreach (RoleInstance inst in RoleEnvironment.Roles[roleName].Instances)
                if (inst.InstanceEndpoints.ContainsKey(endpointName))
                {
                    RoleInstanceEndpoint instEndpoint = inst.InstanceEndpoints[endpointName];
                    if (instEndpoint != null)
                        endpoints.Add(instEndpoint.IPEndpoint);
                }
            }

            return endpoints;
        }

        /// <summary>
        /// Return <c>true</c> is this value is a reference to an setting value in the Azure service configuration for the current role,
        /// </summary>
        /// <param name="name"></param>
        /// <param name="val">Input value to be processed.</param>
        /// <returns>Return the original value if it was not a reference to </returns>
        public static string CheckServiceConfigurationSetting(string name, string val)
        {
            if (!val.StartsWith("@"))
                return val; // Use original value
            
            // Assume this is a reference to a setting in the Aazure service configuration file, so substitute.
            var settingName = val.Substring(1);
            var subsVal = RoleEnvironment.GetConfigurationSettingValue(settingName);
            Trace.TraceInformation("Config value {0} replaced with {1} setting value from role config settings", name, subsVal);
            return subsVal;
        }
    }
}