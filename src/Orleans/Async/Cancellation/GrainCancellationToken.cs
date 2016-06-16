using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Orleans.CodeGeneration;
using Orleans.Runtime;
using Orleans.Serialization;

namespace Orleans.Async
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
        /// Initializes the <see cref="T:Orleans.Async.GrainCancellationToken"/>.
        /// </summary>
        internal GrainCancellationToken(
            Guid id,
            bool canceled)
        {
            _cancellationTokenSource = new CancellationTokenSource();
            if (canceled)
            {
                _cancellationTokenSource.Cancel();
            }

            Id = id;
            _targetGrainReferences = new ConcurrentDictionary<GrainId, GrainReference>();
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
            _cancellationTokenSource.Cancel();
            if (_targetGrainReferences.IsEmpty)
            {
                return TaskDone.Done;
            }

            var cancellationTasks = _targetGrainReferences
                .Select(pair => pair.Value.AsReference<ICancellationSourcesExtension>()
                .CancelTokenSource(this))
                .ToList();

            return Task.WhenAll(cancellationTasks);
        }

        internal void AddGrainReference(GrainReference grainReference)
        {
            _targetGrainReferences.TryAdd(grainReference.GrainId, grainReference);
        }

        public void Dispose()
        {
            _cancellationTokenSource.Dispose();
        }

        #region Serialization

        [SerializerMethod]
        internal static void SerializeGrainCancellationToken(object obj, BinaryTokenStreamWriter stream, Type expected)
        {
            var ctw = (GrainCancellationToken)obj;
            var canceled = ctw.CancellationToken.IsCancellationRequested;
            stream.Write(canceled);
            stream.Write(ctw.Id);
        }

        [DeserializerMethod]
        internal static object DeserializeGrainCancellationToken(Type expected, BinaryTokenStreamReader stream)
        {
            var cancellationRequested = stream.ReadToken() == SerializationTokenType.True;
            var tokenId = stream.ReadGuid();
            return new GrainCancellationToken(tokenId, cancellationRequested);
        }

        [CopierMethod]
        internal static object CopyGrainCancellationToken(object obj)
        {
            var gct = (GrainCancellationToken) obj;
            return new GrainCancellationToken(gct.Id, gct.IsCancellationRequested);
        }

        #endregion
    }
}