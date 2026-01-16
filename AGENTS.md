# AGENTS.md - Orleans Codebase Guidelines

This document provides guidelines for AI coding agents working in the Orleans repository.

## Overview

Orleans is a distributed actor framework for .NET using the Virtual Actor Model. Grains are the
fundamental building blocks with stable identity, behavior, and state. Grain interfaces inherit
from `IGrain` or typed variants like `IGrainWithGuidKey`/`IGrainWithStringKey`.

## Environment

- **Shell**: PowerShell Core (`pwsh.exe`)
- **SDK**: .NET SDK 10.0.102+ (see `global.json`, uses `rollForward: major`)
- **Solution**: `Orleans.slnx`
- **Language**: C# 13 (preview features enabled via `LangVersion: preview`)

## Build Commands

```powershell
# Build entire solution
dotnet build Orleans.slnx

# Build specific project
dotnet build src/Orleans.Runtime/Orleans.Runtime.csproj

# Build scripts (Windows)
./Build.cmd
./build.ps1
```

## Test Commands

Tests use **xUnit** with custom `[TestCategory]` attributes for filtering. CI runs tests on
`ubuntu-latest`, `windows-latest`, and `macos-latest` against both `net8.0` and `net10.0`.

```powershell
# Run all tests in a project
dotnet test test/DefaultCluster.Tests/DefaultCluster.Tests.csproj

# Run a single test by method name
dotnet test --filter "FullyQualifiedName~SimpleGrainTests.SimpleGrainGetGrain"
dotnet test --filter "DisplayName~SimpleGrainGetGrain"

# Run tests by class name
dotnet test --filter "ClassName=UnitTests.TimeoutTests"

# Run tests by category (how CI runs them)
dotnet test --filter "Category=BVT" --framework net8.0
dotnet test --filter "Category=Functional" --framework net10.0

# Combine categories
dotnet test --filter "Category=BVT|Category=SlowBVT"

# Provider-specific tests (require external services)
dotnet test --filter "Category=Redis&(Category=BVT|Category=SlowBVT|Category=Functional)"
dotnet test --filter "Category=AzureStorage&(Category=BVT|Category=SlowBVT|Category=Functional)"

# CI-style test run with blame and logging
dotnet test --framework net8.0 --filter "Category=BVT" --blame-hang-timeout 10m `
  --logger "trx" -- -parallel none -noshadow
```

### Test Categories

| Category | Description |
|----------|-------------|
| BVT | Build Verification Tests (fast, core functionality) |
| SlowBVT | Slower verification tests |
| Functional | Integration-style functional tests |
| Streaming | Stream provider tests |
| Persistence | Storage/persistence tests |
| Transactions | Transaction functionality tests |
| Serialization | Serialization tests |

### Provider-Specific Categories (require external dependencies)

| Category | Service | Environment Variable |
|----------|---------|---------------------|
| Redis | Redis server | `ORLEANSREDISCONNECTIONSTRING` |
| AzureStorage | Azure Storage/Azurite | `ORLEANSDATACONNECTIONSTRING` |
| EventHub | Azure Event Hubs | `ORLEANSEVENTHUBCONNECTIONSTRING` |
| Cosmos | Azure Cosmos DB | `ORLEANSCOSMOSDBACCOUNTENDPOINT` |
| PostgreSql | PostgreSQL | `ORLEANSPOSTGRESCONNECTIONSTRING` |
| MySql | MySQL/MariaDB | `ORLEANSMYSQLCONNECTIONSTRING` |
| SqlServer | SQL Server | `ORLEANSMSSQLCONNECTIONSTRING` |
| DynamoDB | AWS DynamoDB | `ORLEANSDYNAMODBSERVICE` |
| Cassandra | Apache Cassandra | `CASSANDRAVERSION` |
| Consul | HashiCorp Consul | `ORLEANSCONSULCONNECTIONSTRING` |
| ZooKeeper | Apache ZooKeeper | `ORLEANSZOOKEEPERCONNECTIONSTRING` |
| NATS | NATS JetStream | (default localhost) |

## Code Style Guidelines

### Formatting (from `.editorconfig`)

- **Indentation**: 4 spaces for C#, 2 spaces for XML/JSON
- **Namespaces**: File-scoped (`namespace Foo;`)
- **Braces**: Allman style - newline before opening brace on all blocks
- **`var`**: Use `var` everywhere (when type is apparent or for built-in types)
- **Braces required**: Always use braces even for single-line blocks

### Imports

- Sort `System.*` directives first
- Remove unnecessary usings
- Implicit usings are enabled

### Naming Conventions

| Symbol | Convention | Example |
|--------|------------|---------|
| Public/internal fields | PascalCase | `PublicField` |
| Private fields | `_camelCase` | `_privateField` |
| Constants | PascalCase | `MaxRetries` |
| Parameters | camelCase | `grainId` |
| Interfaces | `I` prefix + PascalCase | `IGrainFactory` |
| Classes/Structs/Enums | PascalCase | `GrainState` |

### Nullable Reference Types

- Declare variables non-nullable; check for `null` at entry points
- Use `is null` / `is not null` instead of `== null` / `!= null`
- Trust C# null annotations - don't add redundant null checks

### Error Handling

- Use `ArgumentNullException.ThrowIfNull()` throw helpers
- Use `ArgumentException.ThrowIfNullOrEmpty()` for strings
- Use `ArgumentOutOfRangeException.ThrowIfNegative()` etc.
- Prefer pattern matching in null checks

### Async Patterns

- Use `async`/`await` consistently - Orleans is built on async patterns
- Forward `CancellationToken` parameters to methods that accept them
- Use `ValueTask` correctly (don't await multiple times)
- **CRITICAL: Never use `ConfigureAwait(false)` in grain code or grain library code** (e.g., `Orleans.Journaling`, `Orleans.DurableJobs`, etc.). Orleans grains rely on the synchronization context to maintain single-threaded execution semantics. Using `ConfigureAwait(false)` causes the continuation to run on a thread pool thread, breaking grain thread-safety guarantees and causing subtle race conditions. Use `ConfigureAwait(true)` (the default) or omit `ConfigureAwait` entirely in grain-related code.

### Modern C# Features

- Use pattern matching and switch expressions
- Use `nameof()` instead of string literals for member names
- Use collection expressions where appropriate
- Use primary constructors where it improves readability

### Documentation

- XML doc comments required for public APIs
- Include `<example>` and `<code>` blocks when applicable
- Do not emit "Arrange", "Act", "Assert" comments in tests

## Testing Guidelines

- Use **NSubstitute** for mocking
- Copy existing style in nearby files for test method names
- Test classes typically inherit from `HostedTestClusterEnsureDefaultStarted` or use `IClassFixture<>`
- Tests are in `test/` directory, organized by functionality

```csharp
// Example test structure
public class MyTests : HostedTestClusterEnsureDefaultStarted
{
    public MyTests(DefaultClusterFixture fixture) : base(fixture) { }

    [Fact, TestCategory("BVT")]
    public async Task MyGrain_DoesExpectedBehavior()
    {
        var grain = GrainFactory.GetGrain<IMyGrain>(Guid.NewGuid());
        var result = await grain.DoSomething();
        Assert.Equal(expected, result);
    }
}
```

## Repository Structure

```
src/                    # Core Orleans runtime and providers
  Orleans.Core/         # Core runtime implementation
  Orleans.Runtime/      # Server-side runtime
  Orleans.Client/       # Client-side grain communication
  Orleans.Serialization/# High-performance serialization
  Azure/                # Azure storage/clustering providers
  Redis/                # Redis providers
  AdoNet/               # ADO.NET database providers
test/                   # All test projects
  DefaultCluster.Tests/ # Default cluster integration tests
  NonSilo.Tests/        # Non-silo unit tests
  TesterInternal/       # Internal integration tests
  TestInfrastructure/   # Test utilities and base classes
playground/             # Experimental code and samples
```

## Important Notes

- **Never change `global.json`** unless explicitly asked
- Warnings are treated as errors (`TreatWarningsAsErrors: true`)
- Code style is enforced in build (`EnforceCodeStyleInBuild: true`)
- Test parallelization is disabled (see `test/xunit.runner.json`)
- Long-running test timeout is 120 seconds
- CI uses `--blame-hang-timeout 10m` to detect hung tests
