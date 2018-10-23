using System.Threading.Tasks;

namespace HelloWorld.Interfaces
{
    public interface IStockGrain : Orleans.IGrainWithStringKey
    {
        Task<string> GetPrice();
    }
}