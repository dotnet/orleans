using TestExtensions;
using Xunit;

namespace Orleans.Streaming.Kinesis.Tests;

internal static class KinesisTestConstants
{
    public static string DynamoDbAccessKey => TestDefaultConfiguration.DynamoDbAccessKey!;
    public static string DynamoDbSecretKey => TestDefaultConfiguration.DynamoDbSecretKey!;
    public static string DynamoDbService => TestDefaultConfiguration.DynamoDbService!;
    public static string ConnectionString => TestDefaultConfiguration.KinesisConnectionString!;

    public static bool IsAvailable => !string.IsNullOrWhiteSpace(ConnectionString);

    public static bool IsDynamoDbAvailable => !string.IsNullOrWhiteSpace(DynamoDbService);

    public static void CheckPreconditionsOrThrow()
    {
        if (!IsAvailable)
        {
            throw Xunit.Sdk.SkipException.ForSkip("Empty connection string");
        }
    }

    public static void CheckDynamoDbPreconditionsOrThrow()
    {
        if (!IsDynamoDbAvailable)
        {
            throw Xunit.Sdk.SkipException.ForSkip("DynamoDB service is not configured");
        }
    }
}
