# Proposal 8: Format mismatch diagnostics

## Status

Draft

## Problem

The log format key is configuration, not persisted data. If a grain writes data using one format key, such as `json`, and is later configured to recover using another key, such as `protobuf`, recovery fails only when the selected physical reader encounters incompatible bytes.

The failure is technically correct but may not be actionable.

## Goals

- Make format-key mismatch failures easier to diagnose.
- Avoid making storage format-aware as a required contract.
- Avoid changing every physical format unless there is a migration story.

## Options

### Option A: diagnostic wrapping only

When recovery fails while reading persisted data, wrap or augment the exception message:

```text
Recovery failed using configured journaling format key 'protobuf'.
If this grain previously used another journaling format key, restore that key or migrate the data.
```

This requires `LogManager` to retain the active key string for diagnostics.

### Option B: provider metadata

Storage providers that support metadata can persist:

```text
orleans-journaling-format = json
```

On recovery, `LogManager` asks the storage provider for persisted metadata and compares it with the configured key.

Possible interface:

```csharp
public interface ILogStorageFormatMetadata
{
    ValueTask<string?> ReadLogFormatKeyAsync(CancellationToken cancellationToken);
    ValueTask WriteLogFormatKeyAsync(string key, CancellationToken cancellationToken);
}
```

### Option C: physical header entry

Each physical log begins with a format identity header.

Benefits:

- Storage-independent.

Costs:

- Changes every physical format.
- Requires compatibility handling for existing data.
- Complicates append and replace behavior.

## Recommendation

Start with Option A. It is low risk and improves operator experience immediately.

Option B can be considered for providers with natural metadata support, such as Azure Blob Storage, once there is a concrete migration or validation story.

Avoid Option C for now. It changes the physical format contract and complicates compatibility for limited benefit.

## Implementation sketch for Option A

Store the active log format key in `LogManager`:

```csharp
private readonly string _logFormatKey;
```

When `RecoverAsync` catches a parsing failure, throw a more actionable exception:

```csharp
catch (Exception exception) when (IsRecoveryFormatException(exception))
{
    throw new InvalidOperationException(
        $"Failed to recover journaling state using configured log format key '{_logFormatKey}'. " +
        "If this grain previously used another journaling format key, restore that key or migrate the data.",
        exception);
}
```

This should be scoped carefully to recovery parsing/application failures, not storage connectivity failures.

## Benefits

- Clearer errors when users accidentally switch format keys.
- No persisted format changes.
- No provider changes required.

## Costs and risks

- The diagnostic cannot prove a mismatch; it can only suggest one.
- Wrapping too broadly could obscure real codec bugs.

## Validation

- Add tests that recovering JSON bytes with protobuf key produces a message mentioning the configured key and migration possibility.
- Add tests that storage exceptions are not mislabeled as format mismatches.
- Keep existing malformed-data tests checking inner exception details.
