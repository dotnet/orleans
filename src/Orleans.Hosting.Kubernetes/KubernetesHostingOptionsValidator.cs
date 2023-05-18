using Microsoft.Extensions.Options;
using System.Collections.Generic;

namespace Orleans.Hosting.Kubernetes
{
    /// <summary>
    /// Validates <see cref="KubernetesHostingOptions"/>.
    /// </summary>
    internal class KubernetesHostingOptionsValidator : IValidateOptions<KubernetesHostingOptions>
    {
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

            if (failures is not null) return ValidateOptionsResult.Fail(failures);

            return ValidateOptionsResult.Success;
        }
    }
}
