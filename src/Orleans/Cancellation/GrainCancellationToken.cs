using System;
using System.Collections.Concurrent;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Orleans.CodeGeneration;
using Orleans.Runtime;
using Orleans.Serialization;

namespace Orleans
{
    /// <summary>
    /// Grain cancellation token
    /// </summary>
    [Serializable]
    public sealed class GrainCancellationToken : IDisposable
    {
        [NonSerialized]
        private readonly CancellationTokenSource _cancellationTokenSource;

        /// <summary>
        /// References to remote grains to which this token was passed.
        /// </summary>
        [NonSerialized]
        private readonly ConcurrentDictionary<GrainId, GrainReference> _targetGrainReferences;

        /// <summary>
        /// Initializes the <see cref="T:Orleans.GrainCancellationToken"/>.
        /// </summary>
        internal GrainCancellationToken(Guid id)
        {
            _cancellationTokenSource = new CancellationTokenSource();
            Id = id;
            _targetGrainReferences = new ConcurrentDictionary<GrainId, GrainReference>();
        }


        /// <summary>
        /// Initializes the <see cref="T:Orleans.GrainCancellationToken"/>.
        /// </summary>
        internal GrainCancellationToken(Guid id, bool canceled) : this(id)
        {
            if (canceled)
            {
                // we Cancel _cancellationTokenSource just "to store" the cancelled state.
                _cancellationTokenSource.Cancel();
            }
        }

        /// <summary>
        /// Unique id of concrete token
        /// </summary>
        internal Guid Id { get; private set; }

        /// <summary>
        /// Underlying cancellation token
        /// </summary>
        public CancellationToken CancellationToken => _cancellationTokenSource.Token;

        internal bool IsCancellationRequested => _cancellationTokenSource.IsCancellationRequested;

        internal Task Cancel()
        {
            // propagate the exception from the _cancellationTokenSource.Cancel back to the caller
            // but also cancel _targetGrainReferences. 
            Task task = OrleansTaskExtentions.WrapInTask(_cancellationTokenSource.Cancel);
            
            if (_targetGrainReferences.IsEmpty)
            {
                return task;
            }
            var cancellationTasks = _targetGrainReferences
                .Select(pair => pair.Value.AsReference<ICancellationSourcesExtension>()
                .CancelRemoteToken(this))
                .ToList();
            cancellationTasks.Add(task);

            return Task.WhenAll(cancellationTasks);
        }

        internal void AddGrainReference(GrainReference grainReference)
        {
            _targetGrainReferences.TryAdd(grainReference.GrainId, grainReference);
        }

        /// <inheritdoc />
        public void Dispose()
        {
            _cancellationTokenSource.Dispose();
        }

        #region Serialization

        [SerializerMethod]
        internal static void SerializeGrainCancellationToken(object obj, ISerializationContext context, Type expected)
        {
            var ctw = (GrainCancellationToken)obj;
            var canceled = ctw.CancellationToken.IsCancellationRequested;
            var writer = context.StreamWriter;
            writer.Write(canceled);
            writer.Write(ctw.Id);
        }

        [DeserializerMethod]
        internal static object DeserializeGrainCancellationToken(Type expected, IDeserializationContext context)
        {
            var reader = context.StreamReader;
            var cancellationRequested = reader.ReadToken() == SerializationTokenType.True;
            var tokenId = reader.ReadGuid();
            return new GrainCancellationToken(tokenId, cancellationRequested);
        }

        [CopierMethod]
        internal static object CopyGrainCancellationToken(object obj, ICopyContext context)
        {
            var gct = (GrainCancellationToken) obj;
            return new GrainCancellationToken(gct.Id, gct.IsCancellationRequested);
        }

        #endregion
    }
}