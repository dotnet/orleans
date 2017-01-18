using System.Threading.Tasks;
using Orleans;

namespace GrainInterfaces
{
    public interface  ICalculatorGrain : IGrainWithGuidKey
    {
        Task<double> Add(double value);
        Task<double> Subtract(double value);
        Task<double> Divide(double value);
        Task<double> Multiply(double value);
        Task<double> Set(double value);
        Task<double> Get();
    }
}
