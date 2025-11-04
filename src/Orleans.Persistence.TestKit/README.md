# Orleans.Persistence.TestKit

A comprehensive testing kit for Orleans `IGrainStorage` providers. This package provides a reusable test suite that helps developers test their custom storage providers to ensure they conform to expected behavior.

## Features

- **Comprehensive Test Coverage**: Tests for Read, Write, and Clear operations
- **Concurrency Testing**: Validates correct behavior under concurrent operations
- **ETag Consistency**: Ensures proper optimistic concurrency control
- **Easy Integration**: Simple base classes for quick test setup
- **xUnit Compatible**: Works seamlessly with xUnit testing framework

## Installation

```bash
dotnet add package Microsoft.Orleans.Persistence.TestKit
```

## Quick Start

### Step 1: Create a Test Fixture

Inherit from `GrainStorageTestFixture` and configure your storage provider:

```csharp
using Orleans.Persistence.TestKit;
using Orleans.Hosting;

public class MyStorageTestFixture : GrainStorageTestFixture
{
    protected override string StorageProviderName => "MyStorage";

    protected override void ConfigureSilo(ISiloBuilder siloBuilder)
    {
        // Configure your custom storage provider
        siloBuilder.AddMyCustomGrainStorage("MyStorage", options =>
        {
            // Configure options
        });
    }
}
```

### Step 2: Create Test Class

Inherit from `GrainStorageTestRunner` and implement your tests:

```csharp
using Orleans.Persistence.TestKit;
using Xunit;

public class MyStorageTests : GrainStorageTestRunner, IClassFixture<MyStorageTestFixture>
{
    public MyStorageTests(MyStorageTestFixture fixture) 
        : base(fixture.Storage)
    {
    }

    [Fact]
    public Task PersistenceStorage_WriteReadIdCyrillic() => base.PersistenceStorage_WriteReadIdCyrillic();

    [Fact]
    public Task PersistenceStorage_WriteDuplicateFailsWithInconsistentStateException() => base.PersistenceStorage_WriteDuplicateFailsWithInconsistentStateException();

    [Fact]
    public Task PersistenceStorage_WriteInconsistentFailsWithInconsistentStateException() => base.PersistenceStorage_WriteInconsistentFailsWithInconsistentStateException();

    [Fact]
    public Task PersistenceStorage_WriteReadWriteReadStatesInParallel() => base.PersistenceStorage_WriteReadWriteReadStatesInParallel();
}
```

## Available Tests

The `GrainStorageTestRunner` base class provides the following test methods:

### Basic Operations

- **`PersistenceStorage_WriteReadIdCyrillic()`**: Tests basic write and read operations
- **`PersistenceStorage_ReadNonExistentState()`**: Tests that reading a non-existent state returns RecordExists=false
- **`PersistenceStorage_ReadNonExistentStateHasNonNullState()`**: Verifies State property is not null after reading non-existent state
- **`PersistenceStorage_WriteClearWrite()`**: Tests write, clear, and write cycle to ensure state can be re-written after clearing
- **`PersistenceStorage_WriteClearRead()`**: Tests the full write-clear-read cycle with verification that state is properly initialized after clear
- **`PersistenceStorage_WriteReadClearReadCycle()`**: Tests complete write-read-clear-read cycle to verify state transitions

### Key Type Tests

- **`PersistenceStorage_WriteRead_StringKey()`**: Tests storage operations with string-based grain keys
- **`PersistenceStorage_WriteRead_IntegerKey()`**: Tests storage operations with integer-based grain keys

### Clear/Delete Operation Tests

- **`PersistenceStorage_ClearBeforeWrite()`**: Tests that calling Clear before any write works correctly
- **`PersistenceStorage_ClearStateDoesNotNullifyState()`**: Verifies that State property is never null after Clear operation
- **`PersistenceStorage_ClearUpdatesETag()`**: Tests that ETag changes after Clear operation
- **`PersistenceStorage_ReadAfterClear()`**: Tests reading state after it has been cleared
- **`PersistenceStorage_MultipleClearOperations()`**: Tests multiple successive clear operations are idempotent

### Consistency Tests

- **`PersistenceStorage_WriteDuplicateFailsWithInconsistentStateException()`**: Verifies that attempting to insert duplicate data throws `InconsistentStateException`
- **`PersistenceStorage_WriteInconsistentFailsWithInconsistentStateException()`**: Verifies that writing with an incorrect ETag throws `InconsistentStateException`
- **`PersistenceStorage_ETagChangesOnWrite()`**: Tests that ETag updates properly on successive writes
- **`PersistenceStorage_WriteWithSameValuesUpdatesETag()`**: Tests that updating state with same values still updates ETag

### Concurrency Tests

- **`PersistenceStorage_WriteReadWriteReadStatesInParallel()`**: Tests parallel write and read operations with multiple grains

## Testing Approach

This test kit provides **direct IGrainStorage testing**, which differs from grain-based tests:

- **Direct Storage Testing** (this kit): Tests the `IGrainStorage` interface directly without requiring grain implementations
- **Grain-Based Testing** (e.g., `GrainPersistenceTestsRunner`): Tests storage through actual grain operations

Both approaches are valuable:
- Direct storage tests validate the storage provider implementation in isolation
- Grain-based tests validate the full persistence pipeline including grain lifecycle

This test kit is recommended for:
- Testing custom storage providers
- Verifying storage behavior before deploying to production
- Quick iteration during storage provider development
- Testing edge cases and error handling

## Advanced Usage

### Custom Test State

While the test kit provides a default `TestState1` class, you can use your own state classes in custom tests:

```csharp
public class MyCustomStateTests : GrainStorageTestRunner, IClassFixture<MyStorageTestFixture>
{
    public MyCustomStateTests(MyStorageTestFixture fixture) : base(fixture.Storage)
    {
    }

    [Fact]
    public async Task TestCustomState()
    {
        var grainId = GrainId.Create("my-grain", Guid.NewGuid().ToString());
        var grainState = new GrainState<MyCustomState> 
        { 
            State = new MyCustomState { Property = "value" } 
        };

        await Store_WriteRead("MyGrainType", grainId, grainState);
    }
}
```

### Custom Test Cluster Configuration

You can customize the test cluster configuration by overriding `ConfigureTestCluster`:

```csharp
public class MyStorageTestFixture : GrainStorageTestFixture
{
    protected override string StorageProviderName => "MyStorage";

    protected override void ConfigureSilo(ISiloBuilder siloBuilder)
    {
        siloBuilder.AddMyCustomGrainStorage("MyStorage");
    }

    protected override void ConfigureTestCluster(InProcessTestClusterBuilder builder)
    {
        // Configure additional settings
        builder.ConfigureHostConfiguration(config =>
        {
            // Add configuration
        });
    }
}
```

## Helper Methods

The `GrainStorageTestRunner` class provides protected helper methods you can use in your tests:

- **`GetTestReferenceAndState(long grainId, string version)`**: Creates a test grain ID and state with an integer key
- **`GetTestReferenceAndState(string grainId, string version)`**: Creates a test grain ID and state with a string key
- **`Store_WriteRead<T>(string grainTypeName, GrainId grainId, GrainState<T> grainState)`**: Writes and reads state, asserting correctness
- **`Store_WriteClearRead<T>(string grainTypeName, GrainId grainId, GrainState<T> grainState)`**: Writes, clears, and reads state

## Example: Testing Memory Storage

```csharp
using Orleans.Persistence.TestKit;
using Orleans.Hosting;
using Xunit;

// Fixture
public class MemoryStorageTestFixture : GrainStorageTestFixture
{
    protected override string StorageProviderName => "MemoryStore";

    protected override void ConfigureSilo(ISiloBuilder siloBuilder)
    {
        siloBuilder.AddMemoryGrainStorage("MemoryStore");
    }
}

// Tests
public class MemoryStorageTests : GrainStorageTestRunner, IClassFixture<MemoryStorageTestFixture>
{
    public MemoryStorageTests(MemoryStorageTestFixture fixture) : base(fixture.Storage)
    {
    }

    [Fact]
    public Task PersistenceStorage_WriteReadIdCyrillic() => base.PersistenceStorage_WriteReadIdCyrillic();

    [Fact]
    public Task PersistenceStorage_WriteDuplicateFailsWithInconsistentStateException() => base.PersistenceStorage_WriteDuplicateFailsWithInconsistentStateException();

    [Fact]
    public Task PersistenceStorage_WriteInconsistentFailsWithInconsistentStateException() => base.PersistenceStorage_WriteInconsistentFailsWithInconsistentStateException();

    [Fact]
    public Task PersistenceStorage_WriteReadWriteReadStatesInParallel() => base.PersistenceStorage_WriteReadWriteReadStatesInParallel();
}
```

## Requirements

- .NET 8.0 or later
- xUnit 2.0 or later
- Orleans 9.0 or later

## Contributing

This is an official Orleans package. For issues or contributions, please visit the [Orleans repository](https://github.com/dotnet/orleans).

## License

This project is licensed under the MIT License - see the [LICENSE](https://github.com/dotnet/orleans/blob/main/LICENSE) file for details.
