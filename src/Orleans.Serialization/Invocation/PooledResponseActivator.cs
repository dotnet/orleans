using Orleans.Serialization.Activators;

namespace Orleans.Serialization.Invocation
{
    [RegisterActivator]
    internal sealed class PooledResponseActivator<TResult> : IActivator<PooledResponse<TResult>>
    {
        /// <inheritdoc />
        public PooledResponse<TResult> Create() => ResponsePool.Get<TResult>();
    }
}