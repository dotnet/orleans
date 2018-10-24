using System.Threading.Tasks;

namespace Stocks.Interfaces
{
    public interface IStockGrain : Orleans.IGrainWithStringKey
    {
        Task<string> GetPrice();
    }
}