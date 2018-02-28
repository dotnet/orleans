using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.WindowsAzure.Storage;
using Microsoft.WindowsAzure.Storage.Table;
using Microsoft.WindowsAzure.Storage.Table.Queryable;
using ReplicatedEventSample.Interfaces;

namespace ReplicatedEventSample.Grains
{
    /// <summary>
    /// This class encapsulates Azure table storage for events.
    /// 
    /// An event is stored as the sequence of its outcomes.
    /// Each row is one outcome.
    /// The event name is used as partition key.
    /// The sequence number is used as the row key.
    /// </summary>
    public class ReplicatedEventTable
    {
        private CloudTable tableRef;

        public ReplicatedEventTable()
        {
        }

        /// <summary>
        /// Connect to azure storage table
        /// </summary>
        /// <returns></returns>
        public async Task Connect()
        {
            var storageAccount = CloudStorageAccount.DevelopmentStorageAccount;
            var tableClient = storageAccount.CreateCloudTableClient();
            tableRef = tableClient.GetTableReference("ReplicatedEvents");
            await tableRef.CreateIfNotExistsAsync();
        }


        public async Task<EventState> ReadEventState(string eventname)
        {
            // create fresh state, then read all outcomes and apply them
            var state = new EventState();

            var query = from ent in tableRef.CreateQuery<OutcomeEntity>()
                where ent.PartitionKey == eventname
                select ent;

            await IterateOverQuery(query, (OutcomeEntity e) => { state.Apply(e.ToOutcome()); });

            return state;
        }

        public Task ApplyUpdatesToStorageAsync(string eventname, int expectedversion, IReadOnlyList<Outcome> outcomes)
        {
            TableBatchOperation batchOperation = new TableBatchOperation();

            foreach (var outcome in outcomes)
                batchOperation.Insert(new OutcomeEntity(eventname, expectedversion++, outcome));

            // there cannot be two inserts with the same (eventname,sequencenumber)
            // therefore, the batch will fail if the expectedversion is wrong
            // this ensures integrity

            return tableRef.ExecuteBatchAsync(batchOperation);
        }

        // little utility function to encapsulate ugly async iteration over table query
        public async Task IterateOverQuery(IQueryable<OutcomeEntity> query, Action<OutcomeEntity> action)
        {
            TableQuerySegment<OutcomeEntity> querySegment = null;
            while (querySegment == null || querySegment.ContinuationToken != null)
            {
                querySegment = await query.AsTableQuery()
                    .ExecuteSegmentedAsync(querySegment != null
                        ? querySegment.ContinuationToken
                        : null);
                foreach (var entity in querySegment)
                    action(entity);
            }
        }

        public class OutcomeEntity : TableEntity
        {
            // entities must have a default parameterless constructor
            public OutcomeEntity()
            {
            }

            // construct entity from Outcome object
            public OutcomeEntity(string eventname, int sequencenumber, Outcome outcome)
                : base(eventname, sequencenumber.ToString("D10"))
            {
                Name = outcome.Name;
                Score = outcome.Score;
                When = outcome.When;
            }

            public string EventName
            {
                get { return PartitionKey; }
            }

            public int SequenceNumber
            {
                get { return int.Parse(RowKey); }
            }

            public string Name { get; set; }
            public int Score { get; set; }
            public DateTime When { get; set; }

            // construct Outcome object from entity
            public Outcome ToOutcome()
            {
                return new Outcome()
                {
                    Name = Name,
                    Score = Score,
                    When = When
                };
            }
        }
    }
}