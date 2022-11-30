using System;
using System.Data;
using System.Data.Common;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Orleans.Internal;
using Orleans.Tests.SqlUtils;
using UnitTests.General;
using Xunit;

namespace UnitTests.StorageTests.AdoNet
{
    public class StreamingTest
    {
        public int Id { get; set; }

        public byte[] StreamData { get; set; }
    }

    public abstract class RelationalStoreTestsBase
    {
        //This timeout limit should be clearly less than that defined in RelationalStorageForTesting.CancellationTestQuery. 
        protected readonly TimeSpan CancellationTestTimeoutLimit = TimeSpan.FromSeconds(5);
        protected readonly TimeSpan StreamCancellationTimeoutLimit = TimeSpan.FromSeconds(15);
        protected const int MiB = 1048576;
        protected const int StreamSizeToBeInsertedInBytes = MiB * 2;
        protected const int NumberOfParallelStreams = 5;

        protected static Task<bool>[] InsertAndReadStreamsAndCheckMatch(RelationalStorageForTesting sut, int streamSize, int countOfStreams, CancellationToken cancellationToken)
        {
            Skip.If(string.IsNullOrEmpty(sut.CurrentConnectionString), "Database was not initialized correctly");

            //Stream in and steam out three binary streams in parallel.
            var streamChecks = new Task<bool>[countOfStreams];
            for(int i = 0; i < countOfStreams; ++i)
            {
                int streamId = i;
                streamChecks[i] = Task.Run(async () =>
                {
                    var rb = new byte[streamSize];
                    Random.Shared.NextBytes(rb);
                    await InsertIntoDatabaseUsingStream(sut, streamId, rb, cancellationToken);
                    var dataStreamFromTheDb = await ReadFromDatabaseUsingAsyncStream(sut, streamId, cancellationToken);
                    return dataStreamFromTheDb.StreamData.SequenceEqual(rb);
                });
            }

            return streamChecks;
        }

        protected static async Task InsertIntoDatabaseUsingStream(RelationalStorageForTesting sut, int streamId, byte[] dataToInsert, CancellationToken cancellationToken)
        {
            Skip.If(string.IsNullOrEmpty(sut.CurrentConnectionString), "Database was not initialized correctly");
            //The dataToInsert could be inserted here directly, but it wouldn't be streamed.
            using (var ms = new MemoryStream(dataToInsert))
            {
                await sut.Storage.ExecuteAsync(sut.StreamTestInsert, command =>
                {
                    var p1 = command.CreateParameter();
                    p1.ParameterName = "Id";
                    p1.Value = streamId;
                    command.Parameters.Add(p1);

                    //MySQL does not support streams in and for the time being there
                    //is not a custom stream defined. For ideas, see http://dev.mysql.com/doc/refman/5.7/en/blob.html
                    //for string operations for blobs and http://rusanu.com/2010/12/28/download-and-upload-images-from-sql-server-with-asp-net-mvc/
                    //on how one could go defining one.
                    var p2 = command.CreateParameter();
                    p2.ParameterName = "StreamData";
                    p2.Value = dataToInsert;
                    p2.DbType = DbType.Binary;
                    p2.Size = dataToInsert.Length;
                    command.Parameters.Add(p2);

                }, cancellationToken, CommandBehavior.SequentialAccess).ConfigureAwait(false);
            }
        }

        protected static async Task<StreamingTest> ReadFromDatabaseUsingAsyncStream(RelationalStorageForTesting sut, int streamId, CancellationToken cancellationToken)
        {
            Skip.If(string.IsNullOrEmpty(sut.CurrentConnectionString), "Database was not initialized correctly");
            return (await sut.Storage.ReadAsync(sut.StreamTestSelect, command =>
            {
                var p = command.CreateParameter();
                p.ParameterName = "streamId";
                p.Value = streamId;
                command.Parameters.Add(p);
            }, async (selector, resultSetCount, canellationToken) =>
            {
                var streamSelector = (DbDataReader)selector;
                var id = await streamSelector.GetValueAsync<int>("Id");
                using(var ms = new MemoryStream())
                {                    
                    using(var downloadStream = streamSelector.GetStream(1, sut.Storage))
                    {
                        await downloadStream.CopyToAsync(ms);

                        return new StreamingTest { Id = id, StreamData = ms.ToArray() };
                    }
                }                
            }, cancellationToken, CommandBehavior.SequentialAccess).ConfigureAwait(false)).Single();
        }

        protected static Task CancellationTokenTest(RelationalStorageForTesting sut, TimeSpan timeoutLimit)
        {
            Skip.If(string.IsNullOrEmpty(sut.CurrentConnectionString), "Database was not initialized correctly");
            using (var tokenSource = new CancellationTokenSource(timeoutLimit))
            {
                try
                {
                    //Here one second is added to the task timeout limit in order to account for the delays.
                    //The delays are mainly in the underlying ADO.NET libraries and database.
                    var task = sut.Storage.ReadAsync<int>(sut.CancellationTestQuery, tokenSource.Token);
                    if(!task.Wait(timeoutLimit.Add(TimeSpan.FromSeconds(2))))
                    {
                        Assert.True(false, string.Format("Timeout limit {0} ms exceeded.", timeoutLimit.TotalMilliseconds));
                    }
                }
                catch(Exception ex)
                {
                    //There can be a DbException due to the operation being forcefully cancelled...
                    //... Unless this is a test for a provider which does not support for cancellation.
                    //The exception is wrapped into an AggregrateException due to the test arrangement of hard synchronous
                    //wait to force for actual cancellation check and remove "natural timeout" causes.
                    var innerException = ex?.InnerException;
                    if(sut.Storage.SupportsCommandCancellation())
                    {
                        //If the operation is cancelled already before database calls, a OperationCancelledException
                        //will be thrown in any case.
                        Assert.True(innerException is DbException || innerException is OperationCanceledException, $"Unexpected exception: {ex}");
                    }
                    else
                    {
                        Assert.True(innerException is OperationCanceledException, $"Unexpected exception: {ex}");
                    }
                }
            }

            return Task.CompletedTask;
        }
    }
}
