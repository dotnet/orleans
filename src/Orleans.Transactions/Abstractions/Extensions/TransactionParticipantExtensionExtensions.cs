using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Orleans.CodeGeneration;
using Orleans.Concurrency;
using Orleans.Runtime;
using Orleans.Serialization;

namespace Orleans.Transactions.Abstractions.Extensions
{
    public static class TransactionParticipantExtensionExtensions
    {
        public static string ToShortString(this ITransactionParticipant participant)
        {
            // Meant to help humans when debugging or reading traces
            return participant.GetHashCode().ToString("x4").Substring(0, 4);
        }

        public static ITransactionParticipant AsTransactionParticipant(this ITransactionParticipantExtension transactionalExtension, string resourceId)
        {
            return new TransactionParticipantExtensionWrapper(transactionalExtension, resourceId);
        }

        public static JsonSerializerSettings GetJsonSerializerSettings(ITypeResolver typeResolver, IGrainFactory grainFactory)
        {
            var serializerSettings = OrleansJsonSerializer.GetDefaultSerializerSettings(typeResolver, grainFactory);
            serializerSettings.PreserveReferencesHandling = PreserveReferencesHandling.None;
            return serializerSettings;
        }

        [Serializable]
        [Immutable]
        internal sealed class TransactionParticipantExtensionWrapper : ITransactionParticipant
        {
            private readonly ITransactionParticipantExtension extension;
            [JsonProperty]
            private readonly string resourceId;

            public TransactionParticipantExtensionWrapper(ITransactionParticipantExtension transactionalExtension, string resourceId)
            {
                this.extension = transactionalExtension;
                this.resourceId = resourceId;
            }

            public bool Equals(ITransactionParticipant other)
            {
                return Equals((object)other);
            }

            private bool Equals(TransactionParticipantExtensionWrapper other)
            {
                return Equals(extension, other.extension) && string.Equals(resourceId, other.resourceId);
            }

            public override bool Equals(object obj)
            {
                if (ReferenceEquals(null, obj)) return false;
                if (ReferenceEquals(this, obj)) return true;
                if (obj.GetType() != this.GetType()) return false;
                return Equals((TransactionParticipantExtensionWrapper)obj);
            }

            public override int GetHashCode()
            {
                unchecked
                {
                    return ((extension?.GetHashCode() ?? 0) * 397) ^ (resourceId?.GetHashCode() ?? 0);
                }
            }

            #region request forwarding

            public Task Abort(Guid transactionId)
            {
                return extension.Abort(resourceId, transactionId);
            }

            public Task Cancel(Guid transactionId, DateTime timeStamp, TransactionalStatus status)
            {
                return extension.Cancel(resourceId, transactionId, timeStamp, status);
            }

            public Task<TransactionalStatus> CommitReadOnly(Guid transactionId, AccessCounter accessCount, DateTime timeStamp)
            {
                return extension.CommitReadOnly(resourceId, transactionId, accessCount, timeStamp);
            }

            public Task Confirm(Guid transactionId, DateTime timeStamp)
            {
                return extension.Confirm(resourceId, transactionId, timeStamp);
            }
            public Task Ping(Guid transactionId, DateTime timeStamp, ITransactionParticipant participant)
            {
                return extension.Ping(resourceId, transactionId, timeStamp, participant);
            }

            public Task Prepare(Guid transactionId, AccessCounter accessCount, DateTime timeStamp, ITransactionParticipant transactionManager)
            {
                return extension.Prepare(resourceId, transactionId, accessCount, timeStamp, transactionManager);
            }

            public Task<TransactionalStatus> PrepareAndCommit(Guid transactionId, AccessCounter accessCount, DateTime timeStamp, List<ITransactionParticipant> writeParticipants, int totalParticipants)
            {
                return extension.PrepareAndCommit(resourceId, transactionId, accessCount, timeStamp, writeParticipants, totalParticipants);
            }

            public Task Prepared(Guid transactionId, DateTime timeStamp, ITransactionParticipant participant, TransactionalStatus status)
            {
                return extension.Prepared(resourceId, transactionId, timeStamp, participant, status);
            }

            #endregion
        }
    }
}
