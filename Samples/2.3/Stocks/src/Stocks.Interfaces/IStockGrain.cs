using System.Threading.Tasks;
using Orleans;

namespace Stocks.Interfaces
{
    public interface IStockGrain : IGrainWithStringKey
    {
        Task<string> GetPrice();
    }
}