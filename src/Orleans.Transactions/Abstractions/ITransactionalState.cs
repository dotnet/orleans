
namespace Orleans.Transactions.Abstractions
{
    /// <summary>
    /// State that respects Orleans transaction semantics
    /// </summary>
    public interface ITransactionalState<out TState>
        where TState : class, new()
    {
        TState State { get; }
        void Save();
    }
}
