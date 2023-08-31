namespace Orleans.Transactions.Abstractions
{
    public class TransactionalStateConfiguration : ITransactionalStateConfiguration
    {
        private readonly string name;
        private readonly string storage;
        public TransactionalStateConfiguration(ITransactionalStateConfiguration config, ParticipantId.Role supportedRoles = ParticipantId.Role.Resource | ParticipantId.Role.Manager)
        {
            name = config.StateName;
            storage = config.StorageName;
            SupportedRoles = supportedRoles;
        }
        public string StateName => name;

        public string StorageName => storage;

        public ParticipantId.Role SupportedRoles { get; }
    }

    public interface ITransactionalStateFactory
    {
        ITransactionalState<TState> Create<TState>(TransactionalStateConfiguration config) where TState : class, new();
    }
}
