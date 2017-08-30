using Microsoft.WindowsAzure.ServiceRuntime;
using Orleans;
using Orleans.Runtime.Host;
using System.Web.Mvc;
using System.Web.Routing;
using Orleans.Runtime.Configuration;
using System.Diagnostics;
using System.Threading;
using Orleans.Runtime;
using System;

namespace OrleansXO.Web
{
    // Note: For instructions on enabling IIS7 classic mode, 
    // visit http://go.microsoft.com/fwlink/?LinkId=301868
    public class MvcApplication : System.Web.HttpApplication
    {
        protected void Application_Start()
        {
            const int InitializeAttemptsBeforeFailing = 5;
            int attempt = 0;
            while (true)
            {
                try
                {
                    if (RoleEnvironment.IsAvailable)
                    {
                        // running in Azure
                        AzureClient.Initialize(AzureClient.DefaultConfiguration());
                    }
                    else
                    {
                        // not running in Azure
                        GrainClient.Initialize(ClientConfiguration.LocalhostSilo());
                    }
                    Trace.WriteLine("Client successfully connect to silo host");
                    break;
                }
                catch (SiloUnavailableException)
                {
                    attempt++;
                    Trace.TraceWarning($"Attempt {attempt} of {InitializeAttemptsBeforeFailing} failed to initialize the Orleans client.");
                    if (attempt > InitializeAttemptsBeforeFailing)
                    {
                        throw;
                    }
                    Thread.Sleep(TimeSpan.FromSeconds(2));
                }
            }

            AreaRegistration.RegisterAllAreas();
            RouteConfig.RegisterRoutes(RouteTable.Routes);
        }
    }
}
