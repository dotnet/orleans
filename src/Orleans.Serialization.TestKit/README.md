# Microsoft Orleans Serialization Test Kit

## Introduction
Microsoft Orleans Serialization Test Kit provides tools and utilities to help test serialization functionality in Orleans applications. This package simplifies writing tests that verify serialization and deserialization of your custom types work correctly.

## Getting Started
To use this package, install it via NuGet:

```shell
dotnet add package Microsoft.Orleans.Serialization.TestKit
```

You'll typically add this package to a test project.

## Example - Testing Serialization 
```csharp
using Orleans.Serialization.TestKit;
using Xunit.Abstractions;

public class TimeSpanTests(ITestOutputHelper output) : FieldCodecTester<TimeSpan>(output)
{
    protected override TimeSpan CreateValue() => TimeSpan.FromMilliseconds(Guid.NewGuid().GetHashCode());
    protected override TimeSpan[] TestValues => [TimeSpan.MinValue, TimeSpan.MaxValue, TimeSpan.Zero, TimeSpan.FromSeconds(12345)];
    protected override Action<Action<TimeSpan>> ValueProvider => Gen.TimeSpan.ToValueProvider();
}

public class TimeSpanCopierTests(ITestOutputHelper output) : CopierTester<TimeSpan, IDeepCopier<TimeSpan>>(output)
{
    protected override TimeSpan CreateValue() => TimeSpan.FromMilliseconds(Guid.NewGuid().GetHashCode());
    protected override TimeSpan[] TestValues => [TimeSpan.MinValue, TimeSpan.MaxValue, TimeSpan.Zero, TimeSpan.FromSeconds(12345)];
    protected override Action<Action<TimeSpan>> ValueProvider => Gen.TimeSpan.ToValueProvider();
}

public class DateTimeOffsetTests(ITestOutputHelper output) : FieldCodecTester<DateTimeOffset, DateTimeOffsetCodec>(output)
{
    protected override DateTimeOffset CreateValue() => DateTime.UtcNow;
    protected override DateTimeOffset[] TestValues =>
    [
        DateTimeOffset.MinValue,
        DateTimeOffset.MaxValue,
        new DateTimeOffset(new DateTime(1970, 1, 1, 0, 0, 0), TimeSpan.FromHours(11.5)),
        new DateTimeOffset(new DateTime(1970, 1, 1, 0, 0, 0), TimeSpan.FromHours(-11.5)),
    ];

    protected override Action<Action<DateTimeOffset>> ValueProvider => Gen.DateTimeOffset.ToValueProvider();
}

public class DateTimeOffsetCopierTests(ITestOutputHelper output) : CopierTester<DateTimeOffset, IDeepCopier<DateTimeOffset>>(output)
{
    protected override DateTimeOffset CreateValue() => DateTime.UtcNow;
    protected override DateTimeOffset[] TestValues =>
    [
        DateTimeOffset.MinValue,
        DateTimeOffset.MaxValue,
        new DateTimeOffset(new DateTime(1970, 1, 1, 0, 0, 0), TimeSpan.FromHours(11.5)),
        new DateTimeOffset(new DateTime(1970, 1, 1, 0, 0, 0), TimeSpan.FromHours(-11.5)),
    ];

    protected override Action<Action<DateTimeOffset>> ValueProvider => Gen.DateTimeOffset.ToValueProvider();
}
```

## Additional Testing Features
The TestKit provides several utilities for testing serialization and allows you to focus on testing specific serialization components:

```csharp
// Using a specific serializer
var specificSerializer = services.GetRequiredService<Serializer<MyCustomType>>();
byte[] bytes = specificSerializer.SerializeToArray(original);
var deserialized = specificSerializer.Deserialize(bytes);
```

## Documentation
For more comprehensive documentation, please refer to:
- [Microsoft Orleans Documentation](https://learn.microsoft.com/dotnet/orleans/)
- [Orleans Serialization](https://learn.microsoft.com/en-us/dotnet/orleans/host/configuration-guide/serialization)
- [Testing Orleans Applications](https://learn.microsoft.com/en-us/dotnet/orleans/implementation/testing)

## Feedback & Contributing
- If you have any issues or would like to provide feedback, please [open an issue on GitHub](https://github.com/dotnet/orleans/issues)
- Join our community on [Discord](https://aka.ms/orleans-discord)
- Follow the [@msftorleans](https://twitter.com/msftorleans) Twitter account for Orleans announcements
- Contributions are welcome! Please review our [contribution guidelines](https://github.com/dotnet/orleans/blob/main/CONTRIBUTING.md)
- This project is licensed under the [MIT license](https://github.com/dotnet/orleans/blob/main/LICENSE)