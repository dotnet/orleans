using Orleans.Streams;
using System;
using System.Threading.Tasks;

namespace Orleans.Streaming.EventHubs.Testing
{
    /// <summary>
    /// Creates checkpointers which keep no persistent checkpoint state.
    /// </summary>
    public class NoOpCheckpointerFactory : IStreamQueueCheckpointerFactory
    {
        /// <summary>
        /// Gets the shared factory instance.
        /// </summary>
        public static NoOpCheckpointerFactory Instance = new NoOpCheckpointerFactory();

        /// <inheritdoc />
        public Task<IStreamQueueCheckpointer<string>> Create(string partition)
        {
            return Task.FromResult<IStreamQueueCheckpointer<string>>(NoOpCheckpointer.Instance);
        }
    }
    /// <summary>
    /// NoOpCheckpointer is used in EventDataGeneratorStreamProvider ecosystem to replace the default Checkpointer which requires a back end storage. In EventHubDataGeneratorStreamProvider,
    /// it is generating EventData on the fly when receiver pull messages from the queue, which means it doesn't support recoverable stream, hence check pointing won't bring much value there. 
    /// So a checkpointer with no ops should be enough.
    /// </summary>
    public class NoOpCheckpointer : IStreamQueueCheckpointer<string>
    {
        /// <summary>
        /// Gets the shared checkpointer instance.
        /// </summary>
        public static NoOpCheckpointer Instance = new NoOpCheckpointer();

        /// <inheritdoc />
        public bool CheckpointExists => true;

        /// <inheritdoc />
        public Task<string> Load()
        {
            return Task.FromResult(EventHubConstants.StartOfStream);
        }
        /// <summary>
        /// Ignores a checkpoint update because generated-event streams do not persist checkpoints.
        /// </summary>
        /// <param name="offset">The ignored offset.</param>
        /// <param name="utcNow">The ignored update time.</param>
        public void Update(string offset, DateTime utcNow)
        {
        }
    }
}
