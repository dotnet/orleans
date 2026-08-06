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

    public static void CheckPreconditionsOrThrow()
    {
        if (!IsAvailable)
        {
            throw new SkipException("Empty connection string");
        }
    }
}
