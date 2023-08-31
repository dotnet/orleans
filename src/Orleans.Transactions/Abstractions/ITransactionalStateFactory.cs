namespace Orleans.Transactions.Abstractions
{
    public class TransactionalStateConfiguration : ITransactionalStateConfiguration
    {
        private readonly string storage;
        public TransactionalStateConfiguration(ITransactionalStateConfiguration config, ParticipantId.Role supportedRoles = ParticipantId.Role.Resource | ParticipantId.Role.Manager)
        {
            this.StateName = config.StateName;
            this.storage = config.StorageName;
            this.SupportedRoles = supportedRoles;
        }
        public string StateName { get; }

        public string StorageName => this.storage;

        public ParticipantId.Role SupportedRoles { get; }
    }

    public interface ITransactionalStateFactory
    {
        ITransactionalState<TState> Create<TState>(TransactionalStateConfiguration config) where TState : class, new();
    }
}
