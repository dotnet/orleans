# Microsoft Orleans Serialization Test Kit

## Introduction
Microsoft Orleans Serialization Test Kit provides tools and utilities to help test serialization functionality in Orleans applications. This package simplifies writing tests that verify serialization and deserialization of your custom types work correctly.

## Getting Started
To use this package, install it via NuGet:

```shell
dotnet add package Microsoft.Orleans.Serialization.TestKit
```

You'll typically add this package to a test project.

## Example - Testing Serialization of a Custom Type
```csharp
using Microsoft.Extensions.DependencyInjection;
using Orleans.Serialization;
using Orleans.Serialization.TestKit;
using Xunit;

// Define a test class for serialization
public class SerializationTests
{
    // Create a test for a custom class
    [Fact]
    public void MyClass_Should_Serialize_And_Deserialize()
    {
        // Set up the serialization context
        var services = new ServiceCollection()
            .AddSerializer()
            .BuildServiceProvider();

        // Get a serializer tester
        var serializerTester = services.GetRequiredService<SerializerTester>();

        // Create an instance of your class to test
        var original = new MyCustomClass
        {
            Id = 123,
            Name = "Test Object",
            Data = new List<string> { "item1", "item2", "item3" }
        };

        // Test that serialization and deserialization work correctly
        var roundTripped = serializerTester.RoundTrip(original);

        // Verify properties are correct after round-trip
        Assert.Equal(original.Id, roundTripped.Id);
        Assert.Equal(original.Name, roundTripped.Name);
        Assert.Equal(original.Data.Count, roundTripped.Data.Count);

        for (int i = 0; i < original.Data.Count; i++)
        {
            Assert.Equal(original.Data[i], roundTripped.Data[i]);
        }
    }
}

// The custom class being tested

public class MyCustomClass
{
    public int Id { get; set; }
    public string Name { get; set; }
    public List<string> Data { get; set; }
}
```

## Additional Testing Features
The TestKit provides several utilities for testing serialization:

```csharp
// Testing with serialization contexts
var context1 = services.GetRequiredService<SerializerSessionPool>().GetSession();
var context2 = services.GetRequiredService<SerializerSessionPool>().GetSession();

// Deep copying objects
var originalObject = new MyObject();
var copiedObject = serializerTester.DeepCopy(originalObject);
// Verify copied object is equivalent but not the same instance
Assert.Equal(originalObject.Value, copiedObject.Value);
Assert.NotSame(originalObject, copiedObject);

// Testing specific serializers
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