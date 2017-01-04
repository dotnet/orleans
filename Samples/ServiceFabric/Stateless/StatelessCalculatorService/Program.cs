﻿using System;
using System.Diagnostics;
using System.Fabric;
using System.Threading;
using Microsoft.ServiceFabric.Services.Runtime;

namespace StatelessCalculatorService
{
    internal static class Program
    {
        /// <summary>
        /// This is the entry point of the service host process.
        /// </summary>
        private static void Main()
        {
            try
            {
                // The ServiceManifest.XML file defines one or more service type names.
                // Registering a service maps a service type name to a .NET type.
                // When Service Fabric creates an instance of this service type,
                // an instance of the class is created in this host process.
                ServiceEventSource.Current.Message($"Process {Process.GetCurrentProcess().Id} starting");
                var runtime = FabricRuntime.Create(
                    () =>
                    {
                        Console.WriteLine("Service Fabric Service is exiting.");
                    });
                var nodeContext = FabricRuntime.GetNodeContext();
                var actCont = FabricRuntime.GetActivationContext();
                Console.WriteLine(nodeContext.ToString() + actCont.ToString() + runtime.ToString());
                ServiceRuntime.RegisterServiceAsync(
                    "StatelessCalculatorServiceType",
                    context => new StatelessCalculatorService(context)).Wait();
                ServiceEventSource.Current.Message($"Process {Process.GetCurrentProcess().Id} registered");

                ServiceEventSource.Current.ServiceTypeRegistered(Process.GetCurrentProcess().Id, typeof(StatelessCalculatorService).Name);

                // Prevents this host process from terminating so services keep running.
                Thread.Sleep(Timeout.Infinite);
            }
            catch (Exception e)
            {
                ServiceEventSource.Current.ServiceHostInitializationFailed(e.ToString());
                throw;
            }
        }
    }
}
