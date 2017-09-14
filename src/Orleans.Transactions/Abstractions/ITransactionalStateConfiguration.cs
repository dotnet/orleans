
namespace Orleans.Transactions.Abstractions
{
    public interface ITransactionalStateConfiguration
    {
        string StateName { get; }
        string StorageName { get; }
    }
}
