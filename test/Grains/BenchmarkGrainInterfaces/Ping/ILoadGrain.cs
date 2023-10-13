namespace BenchmarkGrainInterfaces.Ping
{
    [GenerateSerializer]
    public class Report
    {
        [Id(1)]
        public long Succeeded { get; set; }
        [Id(2)]
        public long Failed { get; set; }
        [Id(3)]
        public TimeSpan Elapsed { get; set; }
    }

    public interface ILoadGrain : IGrainWithGuidKey
    {
        Task Generate(int run, int conncurrent);
        Task<Report> TryGetReport();
    }
}
