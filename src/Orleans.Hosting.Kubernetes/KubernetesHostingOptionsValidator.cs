using Microsoft.Extensions.Options;
using Orleans.Configuration;
using System;
using System.Collections.Generic;

namespace Orleans.Hosting.Kubernetes
{
    /// <summary>
    /// Validates <see cref="KubernetesHostingOptions"/>.
    /// </summary>
    internal class KubernetesHostingOptionsValidator : IValidateOptions<KubernetesHostingOptions>
    {
        private readonly IOptions<SiloOptions> _siloOptions;

        public KubernetesHostingOptionsValidator(IOptions<SiloOptions> siloOptions) => _siloOptions = siloOptions;

        public ValidateOptionsResult Validate(string name, KubernetesHostingOptions options)
        {
            List<string> failures = default;
            if (string.IsNullOrWhiteSpace(options.Namespace))
            {
                failures ??= new List<string>();
                failures.Add($"{nameof(KubernetesHostingOptions)}.{nameof(KubernetesHostingOptions.Namespace)} is not set. Set it via the {KubernetesHostingOptions.PodNamespaceEnvironmentVariable} environment variable");
            }

            if (string.IsNullOrWhiteSpace(options.PodName))
            {
                failures ??= new List<string>();
                failures.Add($"{nameof(KubernetesHostingOptions)}.{nameof(KubernetesHostingOptions.PodName)} is not set. Set it via the {KubernetesHostingOptions.PodNameEnvironmentVariable} environment variable");
            }

            if (!string.Equals(_siloOptions.Value.SiloName, options.PodName, StringComparison.Ordinal))
            {
                failures ??= new List<string>();
                failures.Add($"{nameof(SiloOptions)}.{nameof(SiloOptions.SiloName)} is not equal to the current pod name as defined by {nameof(KubernetesHostingOptions)}.{nameof(KubernetesHostingOptions.PodName)}");
            }

            if (failures is not null) return ValidateOptionsResult.Fail(failures);

            return ValidateOptionsResult.Success;
        }
    }
}
