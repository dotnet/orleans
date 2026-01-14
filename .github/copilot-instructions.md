## General

* Make only high confidence suggestions when reviewing code changes.
* Always use the latest version C#, currently C# 13 features.
* Never change global.json unless explicitly asked to.
* Orleans is a distributed actor framework for .NET - understand the grain-based programming model when making changes.

## Build and Test

### Building the Project

* Use `dotnet build` to build the solution (Orleans.slnx).
* The solution uses .NET SDK 9.0.306 as specified in global.json.
* Build scripts are available: `Build.cmd` (Windows) or `build.ps1` (PowerShell).
* Debug builds include a date suffix in version numbers.

### Running Tests

* Use `dotnet test` to run tests.
* Test.cmd provides a convenient way to run all tests on Windows.
* Tests are organized by category: BVT, SlowBVT, Functional, and provider-specific categories.
* Some tests require external dependencies (Redis, Cassandra, Azure, AWS, etc.) - check the CI workflow for setup examples.
* Use `--filter` to run specific test categories, e.g., `dotnet test --filter "Category=BVT"`.

## Formatting

* Apply code-formatting style defined in `.editorconfig`.
* Prefer file-scoped namespace declarations and single-line using directives.
* Insert a newline before the opening curly brace of any code block (e.g., after `if`, `for`, `while`, `foreach`, `using`, `try`, etc.).
* Ensure that the final return statement of a method is on its own line.
* Use pattern matching and switch expressions wherever possible.
* Use `nameof` instead of string literals when referring to member names.
* Ensure that XML doc comments are created for any public APIs. When applicable, include `<example>` and `<code>` documentation in the comments.

### Nullable Reference Types

* Declare variables non-nullable, and check for `null` at entry points.
* Always use `is null` or `is not null` instead of `== null` or `!= null`.
* Trust the C# null annotations and don't add null checks when the type system says a value cannot be null.

## Testing

* We use xUnit SDK v3 for tests.
* Do not emit "Act", "Arrange" or "Assert" comments.
* Use NSubstitute for mocking in tests.
* Copy existing style in nearby files for test method names and capitalization.
* Tests are located in the `test/` directory, organized by functionality (e.g., DefaultCluster.Tests, NonSilo.Tests, Extensions/).

## Repository Structure

* **src/** - Core Orleans runtime, serialization, client, hosting, and provider implementations.
  * Orleans.Core - Core runtime abstractions and implementations
  * Orleans.Serialization - High-performance serialization framework
  * Orleans.Client - Client-side grain communication
  * Orleans.Runtime - Server-side runtime implementation
  * Orleans.Hosting.Kubernetes - Kubernetes hosting support
  * Provider subdirectories: AWS/, Azure/, AdoNet/, Cassandra/, Redis/ for various storage and clustering providers
* **test/** - All test projects, mirroring the src/ structure.
* **samples/** - Example applications demonstrating Orleans usage.
* **playground/** - Experimental code and development workspace.

## Common Patterns

* Grains are the fundamental building blocks - they have stable identity, behavior, and state.
* Grain interfaces inherit from IGrain or IGrainWithGuidKey/IGrainWithStringKey/etc.
* Use async/await consistently - Orleans is built on asynchronous patterns.
* Follow the Virtual Actor Model - grains are automatically activated/deactivated by the runtime.
