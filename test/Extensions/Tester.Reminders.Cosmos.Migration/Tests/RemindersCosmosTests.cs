using Orleans;
using Orleans.Runtime;
using Tester.AzureUtils.Migration.Abstractions;
using UnitTests.GrainInterfaces;
using Xunit;

namespace Tester.Reminders.Cosmos.Migration.Tests;

public abstract class RemindersCosmosTests : MigrationBaseTests
{
    const int baseId = 1000;

    public RemindersCosmosTests(BaseAzureTestClusterFixture fixture)
        : base(fixture)
    {
    }

    [SkippableFact, TestCategory("Reminders")]
    public async Task Reminders_Cosmos_InsertNewRowAndReadBack()
    {
        await ReminderTable.Init();

        var grainRef = PrepareGrainReference();
        var reminderEntry = NewReminderEntry(grainRef);

        var keyInfo = grainRef.GrainId.ToKeyInfo();
        Console.WriteLine($"GrainRef: {keyInfo}");

        var eTag = await ReminderTable.UpsertRow(reminderEntry);
        Assert.NotNull(eTag);

        var reminderDb = await ReminderTable.ReadRow(grainRef, reminderEntry.ReminderName);
        Assert.NotNull(reminderDb);
        CompareReminders(grainRef, reminderEntry, reminderDb);
    }

    private ReminderEntry NewReminderEntry(GrainReference grainReference) => new ReminderEntry
    {
        GrainRef = grainReference ?? PrepareGrainReference(),
        ReminderName = string.Format("TestReminder.{0}", Guid.NewGuid()),
        Period = TimeSpan.FromSeconds(5),
        StartAt = DateTime.UtcNow
    };

    private GrainReference PrepareGrainReference()
    {
        var grain = this.fixture.Client.GetGrain<ISimplePersistentGrain>(baseId + 1);
        return (GrainReference)grain;
    }

    /// <summary>
    /// Compares grain reference + reminder (built manually) with the reminder taken from the CosmosDb
    /// </summary>
    private static void CompareReminders(
        GrainReference grainReference,
        ReminderEntry reminderActual,
        ReminderEntry reminderDb)
    {
        Assert.NotNull(reminderActual);
        Assert.NotNull(reminderDb);

        Assert.Equal(grainReference.ToKeyString(), reminderDb.GrainRef.ToKeyString());
        Assert.Equal(grainReference.GetUniformHashCode(), reminderDb.GrainRef.GetUniformHashCode());

        Assert.Equal(reminderActual.ReminderName, reminderDb.ReminderName);
        Assert.Equal(reminderActual.Period, reminderDb.Period);
        Assert.Equal(reminderActual.StartAt, reminderDb.StartAt);
    }
}