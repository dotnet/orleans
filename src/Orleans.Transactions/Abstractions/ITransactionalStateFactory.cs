namespace Orleans.Transactions.Abstractions
{
    public class TransactionalStateConfiguration : ITransactionalStateConfiguration
    {
        private readonly string storage;
        public TransactionalStateConfiguration(ITransactionalStateConfiguration config, ParticipantId.Role supportedRoles = ParticipantId.Role.Resource | ParticipantId.Role.Manager)
        {
            StateName = config.StateName;
            storage = config.StorageName;
            SupportedRoles = supportedRoles;
        }
        public string StateName { get; }

        public string StorageName => storage;

        public ParticipantId.Role SupportedRoles { get; }
    }

    public interface ITransactionalStateFactory
    {
        ITransactionalState<TState> Create<TState>(TransactionalStateConfiguration config) where TState : class, new();
    }
}
