using System;
using Microsoft.Extensions.DependencyInjection;
using System.Security.Cryptography.X509Certificates;
using Docker.DotNet;

namespace Microsoft.Orleans.Docker
{
    /// <summary>
    /// Extensions for hosting Orleans in Docker containers
    /// </summary>
    public static class OrleansDockerExtensions
    {
        /// <summary>
        /// Add Docker support to the provided service collection.
        /// Use this overload for unsecured endpoints of Docker Daemon or Swarm (mostly dev/test)
        /// </summary>
        /// <param name="serviceCollection">The service collection.</param>
        /// <param name="deamonEndpointUri">The Docker Daemon or Swarm endpoint.</param>
        /// <param name="deploymentId">Orleans Deployment Id.</param>
        /// <returns>The provided service collection</returns>
        public static IServiceCollection AddDockerSupport(
            this IServiceCollection serviceCollection, 
            Uri deamonEndpointUri, string deploymentId)
        {

            return serviceCollection;
        }

        /// <summary>
        /// Add Docker support to the provided service collection. 
        /// Use this overload for secured endpoints of Docker Daemon or Swarm using Basic Auth (username+password)
        /// </summary>
        /// <param name="serviceCollection">The service collection.</param>
        /// <param name="deamonEndpointUri">The Docker Daemon or Swarm endpoint.</param>
        /// <param name="deploymentId">Orleans Deployment Id.</param>
        /// <param name="userName">The username.</param>
        /// <param name="password">The password.</param>
        /// <returns>The provided service collection</returns>
        public static IServiceCollection AddDockerSupport(
            this IServiceCollection serviceCollection, 
            Uri deamonEndpointUri, string deploymentId,
            string userName, string password)
        {


            return serviceCollection;
        }

        /// <summary>
        /// Add Docker support to the provided service collection. 
        /// Use this overload for secured endpoints of Docker Daemon or Swarm using TLS (certificate)
        /// </summary>
        /// <param name="serviceCollection">The service collection.</param>
        /// <param name="deamonEndpointUri">The Docker Daemon or Swarm endpoint.</param>
        /// <param name="deploymentId">Orleans Deployment Id.</param>
        /// <param name="certificate">The certificate.</param>
        /// <returns>The provided service collection.</returns>
        public static IServiceCollection AddDockerSupport(
            this IServiceCollection serviceCollection,
            Uri deamonEndpointUri, string deploymentId,
            X509Certificate2 certificate)
        {


            return serviceCollection;
        }

        /// <summary>
        /// Add Docker support to the provided service collection. 
        /// Use this overload to provide a pre-configured <see cref="DockerClientConfiguration"/>
        /// </summary>
        /// <param name="serviceCollection">The service collection.</param>
        /// <param name="deploymentId">Orleans Deployment Id.</param>
        /// <param name="dockerConfig">The certificate.</param>
        /// <returns>The provided service collection.</returns>
        public static IServiceCollection AddDockerSupport(
            this IServiceCollection serviceCollection,
            string deploymentId,
            DockerClientConfiguration dockerConfig)
        {


            return serviceCollection;
        }

        /// <summary>
        /// Add support for connecting to a cluster hosted in Docker containers to the provided service collection. 
        /// Use this overload for unsecured endpoints of Docker Daemon or Swarm (mostly dev/test)
        /// </summary>
        /// <param name="serviceCollection">The service collection.</param>
        /// <param name="deamonEndpointUri">The Docker Daemon or Swarm endpoint.</param>
        /// <param name="deploymentId">Orleans Deployment Id.</param>
        /// <returns>The provided service collection</returns>
        public static IServiceCollection AddDockerClientSupport(
            this IServiceCollection serviceCollection,
            Uri deamonEndpointUri, string deploymentId)
        {

            return serviceCollection;
        }

        /// <summary>
        /// Add support for connecting to a cluster hosted in Docker containers to the provided service collection. 
        /// Use this overload for secured endpoints of Docker Daemon or Swarm using Basic Auth (username+password)
        /// </summary>
        /// <param name="serviceCollection">The service collection.</param>
        /// <param name="deamonEndpointUri">The Docker Daemon or Swarm endpoint.</param>
        /// <param name="deploymentId">Orleans Deployment Id.</param>
        /// <param name="userName">The username.</param>
        /// <param name="password">The password.</param>
        /// <returns>The provided service collection</returns>
        public static IServiceCollection AddDockerClientSupport(
            this IServiceCollection serviceCollection,
            Uri deamonEndpointUri, string deploymentId,
            string userName, string password)
        {


            return serviceCollection;
        }

        /// <summary>
        /// Add support for connecting to a cluster hosted in Docker containers to the provided service collection. 
        /// Use this overload for secured endpoints of Docker Daemon or Swarm using TLS (certificate)
        /// </summary>
        /// <param name="serviceCollection">The service collection.</param>
        /// <param name="deamonEndpointUri">The Docker Daemon or Swarm endpoint.</param>
        /// <param name="deploymentId">Orleans Deployment Id.</param>
        /// <param name="certificate">The certificate.</param>
        /// <returns>The provided service collection.</returns>
        public static IServiceCollection AddDockerClientSupport(
            this IServiceCollection serviceCollection,
            Uri deamonEndpointUri, string deploymentId,
            X509Certificate2 certificate)
        {


            return serviceCollection;
        }

        /// <summary>
        /// Add support for connecting to a cluster hosted in Docker containers to the provided service collection. 
        /// Use this overload to provide a pre-configured <see cref="DockerClientConfiguration"/>
        /// </summary>
        /// <param name="serviceCollection">The service collection.</param>
        /// <param name="deploymentId">Orleans Deployment Id.</param>
        /// <param name="dockerConfig">The certificate.</param>
        /// <returns>The provided service collection.</returns>
        public static IServiceCollection AddDockerClientSupport(
            this IServiceCollection serviceCollection,
            string deploymentId,
            DockerClientConfiguration dockerConfig)
        {


            return serviceCollection;
        }
    }
}
