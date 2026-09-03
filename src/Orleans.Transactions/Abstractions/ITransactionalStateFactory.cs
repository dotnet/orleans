namespace Orleans.Transactions.Abstractions
{
    /// <summary>
    /// Contains the configuration used to create a transactional state facet.
    /// </summary>
    public class TransactionalStateConfiguration : ITransactionalStateConfiguration
    {
        private readonly string name;
        private readonly string? storage;

        /// <summary>
        /// Initializes a new instance of the <see cref="TransactionalStateConfiguration"/> class.
        /// </summary>
        /// <param name="config">The state and storage configuration.</param>
        /// <param name="supportedRoles">The transaction participant roles supported by the state.</param>
        public TransactionalStateConfiguration(ITransactionalStateConfiguration config, ParticipantId.Role supportedRoles = ParticipantId.Role.Resource | ParticipantId.Role.Manager)
        {
            this.name = config.StateName;
            this.storage = config.StorageName;
            this.SupportedRoles = supportedRoles;
        }

        /// <inheritdoc />
        public string StateName => this.name;

        /// <inheritdoc />
        public string? StorageName => this.storage;

        /// <summary>
        /// Gets the transaction participant roles supported by the state.
        /// </summary>
        public ParticipantId.Role SupportedRoles { get; }
    }

    /// <summary>
    /// Creates transactional state facets for grain activations.
    /// </summary>
    public interface ITransactionalStateFactory
    {
        /// <summary>
        /// Creates a transactional state facet using the specified configuration.
        /// </summary>
        /// <typeparam name="TState">The transactional state type.</typeparam>
        /// <param name="config">The transactional state configuration.</param>
        /// <returns>The configured transactional state facet.</returns>
        ITransactionalState<TState> Create<TState>(TransactionalStateConfiguration config) where TState : class, new();
    }
}
