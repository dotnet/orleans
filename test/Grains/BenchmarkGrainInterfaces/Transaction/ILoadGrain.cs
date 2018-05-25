using System;
using System.Threading.Tasks;
using Orleans;

namespace BenchmarkGrainInterfaces.Transaction
{
    public class Report
    {
        public int Succeeded { get; set; }
        public int Failed { get; set; }
        public TimeSpan Elapsed { get; set; }
    }

    public interface ILoadGrain : IGrainWithGuidKey
    {
        Task Generate(int run, int transactions, int conncurrent);
        Task<Report> TryGetReport();
    }
}