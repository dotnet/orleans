namespace BenchmarkGrainInterfaces.Transaction
{
    [GenerateSerializer]
    public class Report
    {
        [Id(1)]
        public int Succeeded { get; set; }

        [Id(2)]
        public int Failed { get; set; }

        [Id(3)]
        public int Throttled { get; set; }

        [Id(4)]
        public TimeSpan Elapsed { get; set; }
    }

    public interface ILoadGrain : IGrainWithGuidKey
    {
        Task Generate(int run, int transactions, int conncurrent);
        Task<Report> TryGetReport();
    }
}