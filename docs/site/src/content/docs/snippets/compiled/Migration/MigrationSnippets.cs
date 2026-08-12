using Orleans.Configuration;

namespace Documentation.Migration;

internal static class MigrationSnippets
{
    internal static void ConfigureCpuThreshold()
    {
        var options = new LoadSheddingOptions();

        // <configuration_cpu_threshold>
options.CpuThreshold = 95;
        // </configuration_cpu_threshold>
    }

    internal static void ConfigureLeaseAcquisitionPeriod()
    {
        var options = new LeaseBasedQueueBalancerOptions();

        // <configuration_lease_acquisition_period>
options.LeaseAcquisitionPeriod = TimeSpan.FromSeconds(30);
        // </configuration_lease_acquisition_period>
    }
}
