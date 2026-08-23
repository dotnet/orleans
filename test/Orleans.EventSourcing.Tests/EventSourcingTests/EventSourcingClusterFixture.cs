using System.Buffers;
using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Orleans.EventSourcing.CustomStorage;
using Orleans.Journaling;
using Orleans.Journaling.Json;
using Orleans.Storage;
using Orleans.TestingHost;
using TestExtensions;
using TestGrains;

#pragma warning disable ORLEANSEXP005
namespace Tester.EventSourcingTests;

/// <summary>
/// We use a special fixture for event sourcing tests
/// so we can add the required log consistency providers, and
/// do more tracing
/// </summary>
public class EventSourcingClusterFixture : BaseTestClusterFixture
{
    protected override void ConfigureTestCluster(TestClusterBuilder builder)
    {
        builder.AddSiloBuilderConfigurator<TestSiloConfigurator>();
    }

    public void FailNextJournalAppend(IAddressable grain, Exception exception, bool afterWrite = false)
    {
        JournalStorageProvider.FailNextAppend(JournalId.FromGrainId(grain.GetGrainId()), exception, afterWrite);
    }

    private static readonly FaultInjectingJournalStorageProvider JournalStorageProvider = new();

    private class TestSiloConfigurator : ISiloConfigurator
    {
        public void Configure(ISiloBuilder hostBuilder)
        {
            // we use a slowed-down memory storage provider
            hostBuilder
                .AddLogStorageBasedLogConsistencyProvider("LogStorage")
                .AddStateStorageBasedLogConsistencyProvider("StateStorage")
                .AddJournaledStateBasedLogConsistencyProvider("JournaledState")
                .UseJsonJournalFormat(options =>
                {
                    options.SerializerOptions.IncludeFields = true;
                    options.SerializerOptions.Converters.Add(new LogTestEventJsonConverter());
                    options.AddTypeInfoResolver(EventSourcingTestsJsonContext.Default);
                })
                .AddCustomStorageBasedLogConsistencyProvider("CustomStoragePrimaryCluster")
                .ConfigureLogging(builder =>
                {
                    builder.AddFilter(typeof(MemoryGrainStorage).FullName, LogLevel.Debug);
                    builder.AddFilter(typeof(LogConsistencyProvider).Namespace, LogLevel.Debug);
                })
                .AddMemoryGrainStorageAsDefault()
                .AddMemoryGrainStorage("AzureStore")
                .AddMemoryGrainStorage("MemoryStore")
                .AddFaultInjectionMemoryStorage("SlowMemoryStore", options => options.NumStorageGrains = 10, faultyOptions => faultyOptions.Latency = TimeSpan.FromMilliseconds(15));

            hostBuilder.Services.AddSingleton<IJournalStorageProvider>(JournalStorageProvider);
        }

        private sealed class LogTestEventJsonConverter : JsonConverter<object>
        {
            public override bool CanConvert(Type typeToConvert) => typeToConvert == typeof(object);

            public override object? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
            {
                if (reader.TokenType is JsonTokenType.Null)
                {
                    return null;
                }

                using var document = JsonDocument.ParseValue(ref reader);
                var root = document.RootElement;
                var type = root.GetProperty("$type").GetString();
                var value = root.TryGetProperty(nameof(UpdateA.Val), out var valueProperty) ? valueProperty.GetInt32() : 0;

                return type switch
                {
                    nameof(UpdateA) => new UpdateA { Val = value },
                    nameof(UpdateB) => new UpdateB { Val = value },
                    nameof(IncrementA) => new IncrementA { Val = value },
                    nameof(AddReservation) => new AddReservation { Val = value },
                    nameof(RemoveReservation) => new RemoveReservation { Val = value },
                    _ => throw new JsonException($"Unknown log test event type '{type}'.")
                };
            }

            public override void Write(Utf8JsonWriter writer, object value, JsonSerializerOptions options)
            {
                writer.WriteStartObject();
                switch (value)
                {
                    case UpdateA update:
                        WriteEvent(writer, nameof(UpdateA), update.Val);
                        break;
                    case UpdateB update:
                        WriteEvent(writer, nameof(UpdateB), update.Val);
                        break;
                    case IncrementA update:
                        WriteEvent(writer, nameof(IncrementA), update.Val);
                        break;
                    case AddReservation update:
                        WriteEvent(writer, nameof(AddReservation), update.Val);
                        break;
                    case RemoveReservation update:
                        WriteEvent(writer, nameof(RemoveReservation), update.Val);
                        break;
                    default:
                        throw new JsonException($"Unsupported log test event type '{value.GetType()}'.");
                }

                writer.WriteEndObject();
            }

            private static void WriteEvent(Utf8JsonWriter writer, string type, int value)
            {
                writer.WriteString("$type", type);
                writer.WriteNumber(nameof(UpdateA.Val), value);
            }
        }
    }

    private sealed class FaultInjectingJournalStorageProvider : IJournalStorageProvider
    {
        private readonly ConcurrentDictionary<JournalId, AppendFailure> _appendFailures = new();
        private readonly VolatileJournalStorageProvider _inner = new();

        public IJournalStorage CreateStorage(JournalId journalId) => new FaultInjectingJournalStorage(this, journalId, _inner.CreateStorage(journalId));

        public void FailNextAppend(JournalId journalId, Exception exception, bool afterWrite)
        {
            ArgumentNullException.ThrowIfNull(exception);
            if (!_appendFailures.TryAdd(journalId, new(exception, afterWrite)))
            {
                throw new InvalidOperationException($"An append failure is already configured for journal '{journalId}'.");
            }
        }

        private bool TryTakeAppendFailure(JournalId journalId, out AppendFailure failure) => _appendFailures.TryRemove(journalId, out failure);

        private readonly record struct AppendFailure(Exception Exception, bool AfterWrite);

        private sealed class FaultInjectingJournalStorage(
            FaultInjectingJournalStorageProvider provider,
            JournalId journalId,
            IJournalStorage inner) : IJournalStorage
        {
            public bool IsCompactionRequested => inner.IsCompactionRequested;

            public ValueTask ReadAsync(IJournalStorageConsumer consumer, CancellationToken cancellationToken) =>
                inner.ReadAsync(consumer, cancellationToken);

            public ValueTask<bool> CreateIfNotExistsAsync(
                IReadOnlyDictionary<string, string>? metadata = null,
                CancellationToken cancellationToken = default) =>
                inner.CreateIfNotExistsAsync(metadata, cancellationToken);

            public ValueTask<IJournalMetadata?> GetMetadataAsync(CancellationToken cancellationToken = default) =>
                inner.GetMetadataAsync(cancellationToken);

            public ValueTask<IJournalMetadata?> UpdateMetadataAsync(
                IReadOnlyDictionary<string, string>? set = null,
                IEnumerable<string>? remove = null,
                string? expectedETag = null,
                CancellationToken cancellationToken = default) =>
                inner.UpdateMetadataAsync(set, remove, expectedETag, cancellationToken);

            public ValueTask ReplaceAsync(ReadOnlySequence<byte> value, CancellationToken cancellationToken) =>
                inner.ReplaceAsync(value, cancellationToken);

            public async ValueTask AppendAsync(ReadOnlySequence<byte> value, CancellationToken cancellationToken)
            {
                var hasFailure = provider.TryTakeAppendFailure(journalId, out var failure);
                if (hasFailure && !failure.AfterWrite)
                {
                    throw failure.Exception;
                }

                await inner.AppendAsync(value, cancellationToken);
                if (hasFailure)
                {
                    throw failure.Exception;
                }
            }

            public ValueTask DeleteAsync(CancellationToken cancellationToken) => inner.DeleteAsync(cancellationToken);
        }
    }
}

[JsonSourceGenerationOptions(IncludeFields = true)]
[JsonSerializable(typeof(DateTime))]
[JsonSerializable(typeof(int))]
[JsonSerializable(typeof(object))]
[JsonSerializable(typeof(string))]
[JsonSerializable(typeof(uint))]
[JsonSerializable(typeof(UpdateA))]
[JsonSerializable(typeof(UpdateB))]
[JsonSerializable(typeof(IncrementA))]
[JsonSerializable(typeof(AddReservation))]
[JsonSerializable(typeof(RemoveReservation))]
internal sealed partial class EventSourcingTestsJsonContext : JsonSerializerContext;
#pragma warning restore ORLEANSEXP005

public sealed class CommaClusterIdEventSourcingClusterFixture : EventSourcingClusterFixture
{
    public const string ClusterId = "west,prod-v2.canary";

    protected override void ConfigureTestCluster(TestClusterBuilder builder)
    {
        base.ConfigureTestCluster(builder);
        builder.Options.ClusterId = ClusterId;
    }
}
