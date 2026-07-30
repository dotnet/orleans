namespace TransactionalExample.Grains;

[StatelessWorker]
public class AtmGrain : Grain, IAtmGrain
{
    public Task Transfer(
        string fromId,
        string toId,
        decimal amount) =>
        Task.WhenAll(
            GrainFactory.GetGrain<IAccountGrain>(fromId).Withdraw(amount),
            GrainFactory.GetGrain<IAccountGrain>(toId).Deposit(amount));
}
