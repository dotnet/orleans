﻿using System;
using System.Linq;
using System.Reflection;
using Microsoft.Extensions.DependencyInjection;
using Orleans.Hosting;
using Orleans.Runtime.Configuration;
using LivenessProviderType = Orleans.Runtime.Configuration.GlobalConfiguration.LivenessProviderType;

namespace Orleans.Runtime.MembershipService
{
    /// <summary>
    /// LegacyMembershipConfigurator configure membership table in the legacy way, which is from global configuration
    /// </summary>
    public interface ILegacyMembershipConfigurator
    {
        /// <summary>
        /// Configure the membership table in the legacy way 
        /// </summary>
        void ConfigureServices(GlobalConfiguration configuration, IServiceCollection services);
    }

    internal static class LegacyMembershipConfigurator
    {
        /// <summary>
        /// Legacy way to create membership table. Will need to move to a legacy package in the future
        /// </summary>
        /// <returns></returns>
        internal static void ConfigureServices(GlobalConfiguration configuration, IServiceCollection services)
        {
            ILegacyMembershipConfigurator configurator = null;
            switch (configuration.LivenessType)
            {
                case LivenessProviderType.MembershipTableGrain:
                    configurator = new LegacyGrainBasedMembershipConfigurator();
                    break;
                case LivenessProviderType.SqlServer:
                    configurator = GetConfigurator(Constants.ORLEANS_SQL_UTILS_DLL);
                    break;
                case LivenessProviderType.AzureTable:
                    configurator = GetConfigurator(Constants.ORLEANS_AZURE_UTILS_DLL);
                    break;
                case LivenessProviderType.ZooKeeper:
                    configurator = GetConfigurator(Constants.ORLEANS_ZOOKEEPER_UTILS_DLL);
                    break;
                case LivenessProviderType.Custom:
                    configurator = GetConfigurator(configuration.MembershipTableAssembly);
                    break;
                default:
                    break;
            }

            configurator?.ConfigureServices(configuration, services);
        }

        private static ILegacyMembershipConfigurator GetConfigurator(string assemblyName)
        {
            var assembly = Assembly.Load(new AssemblyName(assemblyName));
            var foundType = TypeUtils.GetTypes(assembly, type => typeof(ILegacyMembershipConfigurator).IsAssignableFrom(type), null).First();

            return (ILegacyMembershipConfigurator)Activator.CreateInstance(foundType, true);
        }

        private class LegacyGrainBasedMembershipConfigurator : ILegacyMembershipConfigurator
        {
            public void ConfigureServices(GlobalConfiguration configuration, IServiceCollection services)
            {
                services.UseGrainBasedMembership();
            }
        }
    }
}