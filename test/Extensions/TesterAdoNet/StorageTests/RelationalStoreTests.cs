﻿
using System;
using System.Data;
using System.Data.Common;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Orleans;
using Orleans.Runtime;
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


    public class RelationalStoreTests : IClassFixture<RelationalStoreTests.Fixture>
    {
        private const string testDatabaseName = "OrleansStreamTest";
        
        //This timeout limit should be clearly less than that defined in RelationalStorageForTesting.CancellationTestQuery. 
        private readonly TimeSpan CancellationTestTimeoutLimit = TimeSpan.FromSeconds(1);
        private readonly TimeSpan StreamCancellationTimeoutLimit = TimeSpan.FromSeconds(15);
        private const int MiB = 1048576;
        private const int StreamSizeToBeInsertedInBytes = MiB * 2;
        private const int NumberOfParallelStreams = 5;

        private readonly RelationalStorageForTesting sqlServerStorage;
        private readonly RelationalStorageForTesting mySqlStorage;

        public class Fixture
        {
            public Fixture()
            {
                try
                {
                    SqlServerStorage = RelationalStorageForTesting.SetupInstance(AdoNetInvariants.InvariantNameSqlServer, testDatabaseName).GetAwaiter().GetResult();
                }
                catch (Exception ex)
                {
                    Console.WriteLine("Failed to initialize SQL Server for RelationalGeneralTests: {0}", ex);
                }

                try
                {
                    MySqlStorage = RelationalStorageForTesting.SetupInstance(AdoNetInvariants.InvariantNameMySql, testDatabaseName).GetAwaiter().GetResult();
                }
                catch (Exception ex)
                {
                    Console.WriteLine("Failed to initialize MySQL for RelationalGeneralTests: {0}", ex);
                }
            }

            public RelationalStorageForTesting SqlServerStorage { get; private set; }
            public RelationalStorageForTesting MySqlStorage { get; private set; }
        }

        public RelationalStoreTests(Fixture fixture)
        {
            this.sqlServerStorage = fixture.SqlServerStorage;
            this.mySqlStorage = fixture.MySqlStorage;
        }

        [SkippableFact, TestCategory("Functional"), TestCategory("Persistence"), TestCategory("AdoNet")]
        public async Task Streaming_SqlServer_Test()
        {
            using(var tokenSource = new CancellationTokenSource(StreamCancellationTimeoutLimit))
            {                
                var isMatch = await Task.WhenAll(InsertAndReadStreamsAndCheckMatch(sqlServerStorage, StreamSizeToBeInsertedInBytes, NumberOfParallelStreams, tokenSource.Token));
                Assert.True(isMatch.All(i => i), "All inserted streams should be equal to read streams.");
            }
        }

        [SkippableFact, TestCategory("Functional"), TestCategory("Persistence"), TestCategory("MySql")]
        public async Task Streaming_MySql_Test()
        {
            using(var tokenSource = new CancellationTokenSource(StreamCancellationTimeoutLimit))
            {             
                var isMatch = await Task.WhenAll(InsertAndReadStreamsAndCheckMatch(mySqlStorage, StreamSizeToBeInsertedInBytes, NumberOfParallelStreams, tokenSource.Token));
                Assert.True(isMatch.All(i => i), "All inserted streams should be equal to read streams.");
            }
        }

        [SkippableFact, TestCategory("Functional"), TestCategory("Persistence"), TestCategory("AdoNet")]
        public async Task CancellationToken_SqlServer_Test()
        {
            await CancellationTokenTest(sqlServerStorage, CancellationTestTimeoutLimit);
        }

        [SkippableFact, TestCategory("Functional"), TestCategory("Persistence"), TestCategory("MySql")]
        public async Task CancellationToken_MySql_Test()
        {
            await CancellationTokenTest(mySqlStorage, CancellationTestTimeoutLimit);
        }

        private static Task<bool>[] InsertAndReadStreamsAndCheckMatch(RelationalStorageForTesting sut, int streamSize, int countOfStreams, CancellationToken cancellationToken)
        {
            Skip.If(sut == null, "Database was not initialized correctly");

            //Stream in and steam out three binary streams in parallel.
            var streamChecks = new Task<bool>[countOfStreams];
            var sr = new SafeRandom();
            for(int i = 0; i < countOfStreams; ++i)
            {
                int streamId = i;
                streamChecks[i] = Task.Run(async () =>
                {
                    var rb = new byte[streamSize];
                    sr.NextBytes(rb);
                    await InsertIntoDatabaseUsingStream(sut, streamId, rb, cancellationToken);
                    var dataStreamFromTheDb = await ReadFromDatabaseUsingAsyncStream(sut, streamId, cancellationToken);
                    return dataStreamFromTheDb.StreamData.SequenceEqual(rb);
                });
            }

            return streamChecks;
        }

        private static async Task InsertIntoDatabaseUsingStream(RelationalStorageForTesting sut, int streamId, byte[] dataToInsert, CancellationToken cancellationToken)
        {
            Skip.If(sut == null, "Database was not initialized correctly");
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

        private static async Task<StreamingTest> ReadFromDatabaseUsingAsyncStream(RelationalStorageForTesting sut, int streamId, CancellationToken cancellationToken)
        {
            Skip.If(sut == null, "Database was not initialized correctly");
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

        private static Task CancellationTokenTest(RelationalStorageForTesting sut, TimeSpan timeoutLimit)
        {
            Skip.If(sut == null, "Database was not initialized correctly");
            using (var tokenSource = new CancellationTokenSource(timeoutLimit))
            {
                try
                {
                    //Here one second is added to the task timeout limit in order to account for the delays.
                    //The delays are mainly in the underlying ADO.NET libraries and database.
                    var task = sut.Storage.ReadAsync<int>(sut.CancellationTestQuery, tokenSource.Token);
                    if(!task.Wait(timeoutLimit.Add(TimeSpan.FromSeconds(1))))
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
