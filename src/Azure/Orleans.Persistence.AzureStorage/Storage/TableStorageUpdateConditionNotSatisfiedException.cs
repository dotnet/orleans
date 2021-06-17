using System;
using System.Runtime.Serialization;

namespace Orleans.Storage
{
    /// <summary>
    /// Exception thrown when a storage provider detects an Etag inconsistency when attempting to perform a WriteStateAsync operation.
    /// </summary>
    [Serializable]
    [GenerateSerializer]
    public class TableStorageUpdateConditionNotSatisfiedException : InconsistentStateException
    {
        private const string DefaultMessageFormat = "Table storage condition not Satisfied.  GrainType: {0}, GrainId: {1}, TableName: {2}, StoredETag: {3}, CurrentETag: {4}";

        /// <summary>
        /// Exception thrown when an azure table storage exception is thrown due to update conditions not being satisfied.
        /// </summary>
        public TableStorageUpdateConditionNotSatisfiedException(
            string errorMsg,
            string grainType,
            string grainId,
            string tableName,
            string storedEtag,
            string currentEtag,
            Exception storageException)
            : base(errorMsg, storedEtag, currentEtag, storageException)
        {
            this.GrainType = grainType;
            this.GrainId = grainId;
            this.TableName = tableName;
        }

        /// <summary>
        /// Exception thrown when an azure table storage exception is thrown due to update conditions not being satisfied.
        /// </summary>
        public TableStorageUpdateConditionNotSatisfiedException(
            string grainType,
            string grainId,
            string tableName,
            string storedEtag,
            string currentEtag,
            Exception storageException)
            : this(CreateDefaultMessage(grainType, grainId, tableName, storedEtag, currentEtag), grainType, grainId, tableName, storedEtag, currentEtag, storageException)
        {
        }

        /// <summary>
        /// Id of grain
        /// </summary>
        [Id(0)]
        public string GrainId { get; }

        /// <summary>
        /// Type of grain that throw this exception
        /// </summary>
        [Id(1)]
        public string GrainType { get; }

        /// <summary>
        /// Azure table name
        /// </summary>
        [Id(2)]
        public string TableName { get; }

        /// <summary>
        /// Exception thrown when an azure table storage exception is thrown due to update conditions not being satisfied.
        /// </summary>
        public TableStorageUpdateConditionNotSatisfiedException()
        {
        }

        /// <summary>
        /// Exception thrown when an azure table storage exception is thrown due to update conditions not being satisfied.
        /// </summary>
        public TableStorageUpdateConditionNotSatisfiedException(string msg)
            : base(msg)
        {
        }

        /// <summary>
        /// Exception thrown when an azure table storage exception is thrown due to update conditions not being satisfied.
        /// </summary>
        public TableStorageUpdateConditionNotSatisfiedException(string msg, Exception exc)
            : base(msg, exc)
        {
        }

        private static string CreateDefaultMessage(
            string grainType,
            string grainId,
            string tableName,
            string storedEtag,
            string currentEtag)
        {
            return string.Format(DefaultMessageFormat, grainType, grainId, tableName, storedEtag, currentEtag);
        }

        /// <summary>
        /// Exception thrown when an azure table storage exception is thrown due to update conditions not being satisfied.
        /// </summary>
        protected TableStorageUpdateConditionNotSatisfiedException(SerializationInfo info, StreamingContext context)
            : base(info, context)
        {
            this.GrainType = info.GetString("GrainType");
            this.GrainId = info.GetString("GrainId");
            this.TableName = info.GetString("TableName");
        }

        /// <inheritdoc />
        public override void GetObjectData(SerializationInfo info, StreamingContext context)
        {
            if (info == null) throw new ArgumentNullException(nameof(info));

            info.AddValue("GrainType", this.GrainType);
            info.AddValue("GrainId", this.GrainId);
            info.AddValue("TableName", this.TableName);
            base.GetObjectData(info, context);
        }
    }
}
