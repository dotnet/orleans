using Docker.DotNet;
using Docker.DotNet.BasicAuth;
using Docker.DotNet.X509;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Orleans.Messaging;
using Orleans.Runtime;
using Orleans.Runtime.Configuration;
using System;
using System.IO;
using System.Linq;
using System.Net;
using System.Security.Cryptography.X509Certificates;

namespace Microsoft.Orleans.Docker
{
    /// <summary>
    /// Extensions for hosting Orleans in Docker containers
    /// </summary>
    public static class OrleansDockerExtensions
    {
        /// <summary>
        /// Add Docker support to the provided service collection. 
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
            if (string.IsNullOrWhiteSpace(deploymentId)) deploymentId = Dns.GetHostName();

            serviceCollection.TryAddSingleton(dockerConfig.CreateClient());

            serviceCollection.TryAddSingleton(sp =>
                new DockerSiloResolver(deploymentId, 
                sp.GetService<DockerClient>(), 
                sp.GetService<Func<string, Logger>>()));

            serviceCollection.AddSingleton<IMembershipOracle, DockerMembershipOracle>();

            return serviceCollection;
        }
        
        internal static DockerClient CreateDockerClient(this ClientConfiguration clientConfig)
        {
            if (string.IsNullOrWhiteSpace(clientConfig.DataConnectionString))
                throw new InvalidOperationException("DataConnectionString must be set in order to connect to Docker Daemon");

            var cs = clientConfig.DataConnectionString;

            var parameters = cs.Split(new char[] { '|' }, StringSplitOptions.RemoveEmptyEntries)
                .Select(_ => _.Split(new char[] { '=' }, StringSplitOptions.RemoveEmptyEntries))
                .Where(_ => _.Length == 2)
                .ToDictionary(_ => _.First().ToUpper(), _ => _.Last());

            string dockerDaemonEndpoint = string.Empty;
            string username = string.Empty;
            string password = string.Empty;
            string certificate = string.Empty;

            Credentials credentials = null;

            if (parameters.TryGetValue("DaemonEndpoint".ToUpper(), out username))
            {
                parameters.TryGetValue("Password".ToUpper(), out password);

                credentials = new BasicAuthCredentials(username, password);
            }

            if (parameters.TryGetValue("Certificate".ToUpper(), out certificate))
            {
                if (!File.Exists(certificate)) throw new FileNotFoundException("Unable to find certificate file");

                parameters.TryGetValue("Password".ToUpper(), out password);

                credentials = new CertificateCredentials(new X509Certificate2(certificate, password));
            }

            if (credentials == null) credentials = new AnonymousCredentials();

            if (parameters.TryGetValue("DaemonEndpoint".ToUpper(), out dockerDaemonEndpoint))
                return new DockerClientConfiguration(new Uri(dockerDaemonEndpoint), credentials).CreateClient();

            throw new InvalidOperationException("Unable to create Docker Client. Please check the DataConnectionString parameter on ClientConfiguration.");
        }
    }
}
