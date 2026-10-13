using Microsoft.Extensions.DependencyInjection;
using MongoDB.Driver;
using Orleans.Hosting;
using Orleans.Runtime;
using Xunit;

namespace Orleans.MongoDB.Tests.Reminders;

[TestProvider("MongoDB"), TestSuite("BVT"), TestArea("Reminders")]
public sealed class MongoReminderArgumentTests
{
    [Fact]
    public async Task DefaultGrainId_PointOperationsRejectClientCallsBeforeAccessingDatabase()
    {
        using var client = new MongoClient("mongodb://localhost:27017");
        using var host = MongoTestHost.Create(silo =>
        {
            silo.Services.AddSingleton<IMongoClient>(client);
            silo.UseMongoDBReminders(options => options.DatabaseName = "unused");
        });
        var table = host.Services.GetRequiredService<IReminderTable>();
        // Deliberately do not StartAsync: guards must run before touching the collection or server.
        var token = TestContext.Current.CancellationToken;
        var read = await Assert.ThrowsAsync<InvalidOperationException>(() => table.ReadRow(default, "bill", token));
        var remove = await Assert.ThrowsAsync<InvalidOperationException>(() => table.RemoveRow(default, "bill", "etag", token));
        var upsert = await Assert.ThrowsAsync<InvalidOperationException>(() => table.UpsertRow(new ReminderEntry { ReminderName = "bill" }, token));
        foreach (var exception in new[] { read, remove, upsert })
        {
            Assert.Contains("Grain identifier is default", exception.Message);
            Assert.Contains("client", exception.Message);
        }
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public async Task InvalidReminderName_PointOperationsRejectBeforeAccessingDatabase(string? name)
    {
        using var client = new MongoClient("mongodb://localhost:27017");
        using var host = MongoTestHost.Create(silo =>
        {
            silo.Services.AddSingleton<IMongoClient>(client);
            silo.UseMongoDBReminders(options => options.DatabaseName = "unused");
        });
        var table = host.Services.GetRequiredService<IReminderTable>();
        var id = GrainId.Create("account", "name-validation");
        var token = TestContext.Current.CancellationToken;
        // Unstarted collection: missing guards would fail with collection access, not ArgumentException.
        async Task Check(Func<Task> action, string parameterName)
        {
            var error = name is null
                ? await Assert.ThrowsAsync<ArgumentNullException>(action)
                : await Assert.ThrowsAsync<ArgumentException>(action);
            Assert.Equal(parameterName, error.ParamName);
        }
        await Check(() => table.ReadRow(id, name!, token), "reminderName");
        await Check(() => table.RemoveRow(id, name!, "etag", token), "reminderName");
        var entry = new ReminderEntry { GrainId = id, ReminderName = name!, ETag = "unchanged" };
        await Check(() => table.UpsertRow(entry, token), "entry.ReminderName");
        Assert.Equal("unchanged", entry.ETag);
        Assert.Equal(id, entry.GrainId);
    }

    [Fact]
    public async Task NullReminderEntry_RejectsBeforeAccessingDatabase()
    {
        using var client = new MongoClient("mongodb://localhost:27017");
        using var host = MongoTestHost.Create(silo =>
        {
            silo.Services.AddSingleton<IMongoClient>(client);
            silo.UseMongoDBReminders(options => options.DatabaseName = "unused");
        });
        var error = await Assert.ThrowsAsync<ArgumentNullException>(() =>
            host.Services.GetRequiredService<IReminderTable>().UpsertRow(null!, TestContext.Current.CancellationToken));
        Assert.Equal("entry", error.ParamName);
    }
}
