# Proposal 7: Format-family registration helper

## Status

Draft

## Problem

Each journaling format package repeats the same dependency-injection pattern:

- register an `ILogFormat` keyed by `LogFormatKeys.*`,
- register one provider implementing seven durable operation codec provider interfaces,
- register each keyed provider interface,
- register each unkeyed provider interface for compatibility,
- cache closed generic codec instances in a `ConcurrentDictionary<Type, object>`.

This duplication is easy to get wrong when adding new formats or provider interfaces.

## Goals

- Reduce DI registration boilerplate.
- Make format family registration harder to misconfigure.
- Keep existing extension methods and behavior.
- Avoid changing hot-path codec code.

## Proposed design

Add a registration helper:

```csharp
public static class JournalingFormatFamilyServiceCollectionExtensions
{
    public static JournalingFormatFamilyBuilder AddJournalingFormatFamily(
        this IServiceCollection services,
        string key);
}

public sealed class JournalingFormatFamilyBuilder
{
    public IServiceCollection Services { get; }
    public string Key { get; }

    public JournalingFormatFamilyBuilder AddLogFormat<TFormat>()
        where TFormat : class, ILogFormat;

    public JournalingFormatFamilyBuilder AddOperationCodecProvider<TProvider>()
        where TProvider :
            class,
            IDurableDictionaryOperationCodecProvider,
            IDurableListOperationCodecProvider,
            IDurableQueueOperationCodecProvider,
            IDurableSetOperationCodecProvider,
            IDurableValueOperationCodecProvider,
            IDurableStateOperationCodecProvider,
            IDurableTaskCompletionSourceOperationCodecProvider;
}
```

Usage:

```csharp
builder.Services
    .AddJournalingFormatFamily(LogFormatKeys.Json)
    .AddLogFormat<JsonLinesLogFormat>()
    .AddOperationCodecProvider<JsonOperationCodecProvider>();
```

The helper registers both keyed and unkeyed provider interfaces using the same concrete provider instance, matching current behavior.

## Optional codec cache helper

Add an internal helper to reduce provider boilerplate:

```csharp
internal sealed class DurableCodecCache
{
    private readonly ConcurrentDictionary<Type, object> _codecs = new();

    public T GetOrAdd<T>(Func<T> create)
        where T : class
        => (T)_codecs.GetOrAdd(typeof(T), _ => create());
}
```

Provider code becomes:

```csharp
public IDurableListOperationCodec<T> GetCodec<T>()
    => _cache.GetOrAdd<IDurableListOperationCodec<T>>(
        () => new JsonListOperationCodec<T>(_options));
```

## Benefits

- Less duplicated DI code in JSON, protobuf, MessagePack, and Orleans binary registration.
- Lower risk of missing a provider interface.
- Better third-party format authoring experience.
- No recovery or write-path behavior change.

## Costs and risks

- Adds another abstraction around service registration.
- Must preserve existing unkeyed registration behavior.
- Generic constraints for provider registration are verbose.

## Validation

- Existing keyed registration tests continue to pass.
- Add tests that each built-in format key resolves:
  - `ILogFormat`,
  - all seven durable operation codec provider interfaces.
- Add tests that unkeyed providers still resolve to the selected extension's provider where current behavior requires it.

## Recommendation

Implement as additive cleanup. This is low risk and useful before adding more format-specific dispatch surfaces.
