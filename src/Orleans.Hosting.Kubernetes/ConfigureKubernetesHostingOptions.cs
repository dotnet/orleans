#nullable enable
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Orleans.Configuration;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;

namespace Orleans.Hosting.Kubernetes
{
    internal class ConfigureKubernetesHostingOptions :
        IConfigureOptions<ClusterOptions>,
        IConfigureOptions<SiloOptions>,
        IPostConfigureOptions<EndpointOptions>,
        IConfigureOptions<KubernetesHostingOptions>
    {
        private readonly IServiceProvider _serviceProvider;

        public ConfigureKubernetesHostingOptions(IServiceProvider serviceProvider)
        {
            _serviceProvider = serviceProvider;
        }

        public void Configure(KubernetesHostingOptions options)
        {
            options.Namespace ??= Environment.GetEnvironmentVariable(KubernetesHostingOptions.PodNamespaceEnvironmentVariable) ?? ReadNamespaceFromServiceAccount();
            options.PodName ??= Environment.GetEnvironmentVariable(KubernetesHostingOptions.PodNameEnvironmentVariable) ?? Environment.MachineName;
            options.PodIP ??= Environment.GetEnvironmentVariable(KubernetesHostingOptions.PodIPEnvironmentVariable);
        }

        public void Configure(ClusterOptions options)
        {
            var serviceIdEnvVar = Environment.GetEnvironmentVariable(KubernetesHostingOptions.ServiceIdEnvironmentVariable);
            if (!string.IsNullOrWhiteSpace(serviceIdEnvVar))
            {
                options.ServiceId = serviceIdEnvVar;
            }

            var clusterIdEnvVar = Environment.GetEnvironmentVariable(KubernetesHostingOptions.ClusterIdEnvironmentVariable);
            if (!string.IsNullOrWhiteSpace(clusterIdEnvVar))
            {
                options.ClusterId = clusterIdEnvVar;
            }
        }

        public void Configure(SiloOptions options)
        {
            var hostingOptions = _serviceProvider.GetRequiredService<IOptions<KubernetesHostingOptions>>().Value;
            if (!string.IsNullOrWhiteSpace(hostingOptions.PodName))
            {
                options.SiloName = hostingOptions.PodName;
            }
        }

        public void PostConfigure(string? name, EndpointOptions options)
        {
            // Use PostConfigure to give the developer an opportunity to set SiloPort and GatewayPort using regular
            // Configure methods without needing to worry about ordering with respect to the UseKubernetesHosting call.
            if (options.AdvertisedIPAddress is null)
            {
                var hostingOptions = _serviceProvider.GetRequiredService<IOptions<KubernetesHostingOptions>>().Value;
                IPAddress? podIp = null;
                if (hostingOptions.PodIP is not null)
                {
                    podIp = IPAddress.Parse(hostingOptions.PodIP);
                }
                else
                {
                    var hostAddresses = Dns.GetHostAddresses(hostingOptions.PodName);
                    if (hostAddresses != null)
                    {
                        podIp = IPAddressSelector.PickIPAddress(hostAddresses);
                    }
                }

                if (podIp is not null)
                {
                    options.AdvertisedIPAddress = podIp;
                }
            }

            if (options.SiloListeningEndpoint is null)
            {
                options.SiloListeningEndpoint = new IPEndPoint(IPAddress.Any, options.SiloPort);
            }

            if (options.GatewayListeningEndpoint is null && options.GatewayPort > 0)
            {
                options.GatewayListeningEndpoint = new IPEndPoint(IPAddress.Any, options.GatewayPort);
            }
        }

        private string? ReadNamespaceFromServiceAccount()
        {
            // Read the namespace from the pod's service account.
            var serviceAccountNamespacePath = Path.Combine($"{Path.DirectorySeparatorChar}var", "run", "secrets", "kubernetes.io", "serviceaccount", "namespace");
            if (File.Exists(serviceAccountNamespacePath))
            {
                return File.ReadAllText(serviceAccountNamespacePath).Trim();
            }

            return null;
        }

        private static class IPAddressSelector
        {
            // IANA private IPv4 addresses
            private static readonly (IPAddress Address, IPAddress SubnetMask)[] PreferredRanges = new []
            {
                (IPAddress.Parse("192.168.0.0"), IPAddress.Parse("255.255.0.0")),
                (IPAddress.Parse("10.0.0.0"), IPAddress.Parse("255.0.0.0")),
                (IPAddress.Parse("172.16.0.0"), IPAddress.Parse("255.240.0.0")),
            };

            public static IPAddress? PickIPAddress(IReadOnlyList<IPAddress> candidates)
            {
                IPAddress? chosen = null;
                foreach (var address in candidates)
                {
                    if (chosen is null)
                    {
                        chosen = address;
                    }
                    else
                    {
                        if (CompareIPAddresses(address, chosen))
                        {
                            chosen = address;
                        }
                    }
                }
                return chosen;

                // returns true if lhs is "less" (in some repeatable sense) than rhs
                static bool CompareIPAddresses(IPAddress lhs, IPAddress rhs)
                {
                    var lhsBytes = lhs.GetAddressBytes();
                    var rhsBytes = rhs.GetAddressBytes();

                    if (lhsBytes.Length != rhsBytes.Length)
                    {
                        return lhsBytes.Length < rhsBytes.Length;
                    }

                    // Prefer IANA private IPv4 address ranges 10.x.x.x, 192.168.x.x, 172.16-31.x.x over other addresses.
                    if (lhs.AddressFamily is AddressFamily.InterNetwork && rhs.AddressFamily is AddressFamily.InterNetwork)
                    {
                        var lhsPref = GetPreferredSubnetRank(lhs);
                        var rhsPref = GetPreferredSubnetRank(rhs);
                        if (lhsPref != rhsPref)
                        {
                            return lhsPref < rhsPref;
                        }
                    }

                    // Compare starting from most significant octet.
                    // 10.68.20.21 < 10.98.05.04
                    return lhsBytes.AsSpan().SequenceCompareTo(rhsBytes.AsSpan()) < 0;
                }

                static int GetPreferredSubnetRank(IPAddress ip)
                {
                    var ipBytes = ip.GetAddressBytes();
                    Span<byte> masked = stackalloc byte[ipBytes.Length];
                    var i = 0;
                    foreach (var (Address, SubnetMask) in PreferredRanges)
                    {
                        ipBytes.CopyTo(masked);
                        var subnetMaskBytes = SubnetMask.GetAddressBytes();
                        if (ipBytes.Length != subnetMaskBytes.Length)
                        {
                            continue;
                        }

                        And(ipBytes, subnetMaskBytes, masked);
                        if (masked.SequenceEqual(Address.GetAddressBytes()))
                        {
                            return i;
                        }

                        ++i;
                    }

                    return PreferredRanges.Length;
                    static void And(ReadOnlySpan<byte> lhs, ReadOnlySpan<byte> rhs, Span<byte> result)
                    {
                        Debug.Assert(lhs.Length == rhs.Length);
                        Debug.Assert(lhs.Length == result.Length);

                        for (var i = 0; i < lhs.Length; i++)
                        {
                            result[i] = (byte)(lhs[i] & rhs[i]);
                        }
                    }
                }
            }
        }
    }
}
