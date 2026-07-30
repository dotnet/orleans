using Orleans.Hosting;

namespace GrainStorage;

public class Program
{
    // <silo_host_builder>
    public static void ConfigureSilo()
    {
        var silo = new SiloHostBuilder()
            .UseLocalhostClustering()
            .AddFileGrainStorage("File", opts =>
            {
                opts.RootDirectory = "C:/TestFiles";
            })
            .Build();
    }
    // </silo_host_builder>
}
