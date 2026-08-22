using System;

namespace Orleans.Reminders.TestKit;

/// <summary>
/// Provides capability manifests for the built-in Orleans reminder table implementations.
/// </summary>
public static class ReminderTableProviderProfiles
{
    /// <summary>Creates the Azure Table Storage reminder profile.</summary>
    public static ReminderTableCapabilities AzureStorage(string providerName)
    {
        var result = Immediate(providerName);
        result.SupportsRestartAfterStop = false;
        return result;
    }

    /// <summary>Creates the Cosmos DB reminder profile.</summary>
    public static ReminderTableCapabilities Cosmos(string providerName)
    {
        var result = Immediate(providerName);
        result.SupportsConditionalUpsert = true;
        return result;
    }

    /// <summary>Creates an ADO.NET reminder profile.</summary>
    public static ReminderTableCapabilities AdoNet(string providerName)
    {
        var result = Immediate(providerName);
        result.SupportsUnsignedHashRangeBoundaries = false;
        result.CardinalityMutationBatchSize = 1;
        return result;
    }

    /// <summary>Creates the Firestore reminder profile.</summary>
    public static ReminderTableCapabilities Firestore(string providerName)
    {
        var result = Immediate(providerName);
        result.SupportsRestartAfterStop = false;
        result.SupportsETagRotation = false;
        result.SupportsConditionalUpsert = true;
        result.ReadConvergenceTimeout = TimeSpan.FromSeconds(2);
        result.ReadConvergenceDelay = TimeSpan.FromMilliseconds(25);
        return result;
    }

    /// <summary>Creates the DynamoDB reminder profile.</summary>
    public static ReminderTableCapabilities DynamoDB(string providerName)
    {
        var result = Immediate(providerName);
        result.SupportsSameIdentityConcurrentUpserts = true;
        result.ReadConvergenceTimeout = TimeSpan.FromSeconds(10);
        return result;
    }

    /// <summary>Creates the Redis reminder profile.</summary>
    public static ReminderTableCapabilities Redis(string providerName)
    {
        var result = Immediate(providerName);
        result.SupportsSameIdentityConcurrentUpserts = true;
        return result;
    }

    /// <summary>Creates the grain-based in-memory reminder profile.</summary>
    public static ReminderTableCapabilities InMemory(string providerName)
    {
        var result = Immediate(providerName);
        result.SupportsSubSecondPrecision = true;
        result.SupportsRestartAfterStop = true;
        result.SupportsSameIdentityConcurrentUpserts = true;
        return result;
    }

    /// <summary>Creates the deterministic TestKit oracle profile.</summary>
    public static ReminderTableCapabilities Oracle(string providerName)
    {
        var result = ReminderTableCapabilities.Strict(providerName);
        result.SupportsConditionalUpsert = false;
        return result;
    }

    private static ReminderTableCapabilities Immediate(string providerName)
        => new()
        {
            ProviderName = providerName,
            SupportsRestartAfterStop = true,
            SupportsParallelDistinctRows = true,
            SupportsETagRotation = true,
            SupportsUnsignedHashRangeBoundaries = true
        };
}
