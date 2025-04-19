using Microsoft.Extensions.Logging;
using Xunit;

namespace Orleans.Journaling.Tests;

[TestCategory("BVT")]
public class DurableDictionaryTests : StateMachineTestBase
{
    [Fact]
    public async Task DurableDictionary_BasicOperations_Test()
    {
        // Arrange
        var sut = CreateTestSystem();
        var manager = sut.Manager;
        var keyCodec = CodecProvider.GetCodec<string>();
        var valueCodec = CodecProvider.GetCodec<int>();
        var dictionary = new DurableDictionary<string, int>("testDict", sut.Manager, keyCodec, valueCodec, SessionPool);
        await sut.Lifecycle.OnStart();

        // Act - Add items
        dictionary.Add("one", 1);
        dictionary.Add("two", 2);
        dictionary.Add("three", 3);
        await manager.WriteStateAsync(CancellationToken.None);
        
        // Assert
        Assert.Equal(3, dictionary.Count);
        Assert.Equal(1, dictionary["one"]);
        Assert.Equal(2, dictionary["two"]);
        Assert.Equal(3, dictionary["three"]);
        
        // Act - Update item
        dictionary["two"] = 22;
        await manager.WriteStateAsync(CancellationToken.None);
        
        // Assert
        Assert.Equal(22, dictionary["two"]);
        
        // Act - Remove item
        var removed = dictionary.Remove("three");
        await manager.WriteStateAsync(CancellationToken.None);
        
        // Assert
        Assert.True(removed);
        Assert.Equal(2, dictionary.Count);
        Assert.False(dictionary.ContainsKey("three"));
    }
    
    [Fact]
    public async Task DurableDictionary_Persistence_Test()
    {
        // Arrange
        var sut = CreateTestSystem();
        var keyCodec = CodecProvider.GetCodec<string>();
        var valueCodec = CodecProvider.GetCodec<int>();
        var dictionary1 = new DurableDictionary<string, int>("testDict", sut.Manager, keyCodec, valueCodec, SessionPool);
        await sut.Lifecycle.OnStart();
        
        // Act - Add items and persist
        dictionary1.Add("one", 1);
        dictionary1.Add("two", 2);
        dictionary1.Add("three", 3);
        await sut.Manager.WriteStateAsync(CancellationToken.None);
        
        // Create a new manager with the same storage
        var sut2 = CreateTestSystem(storage: sut.Storage);
        var dictionary2 = new DurableDictionary<string, int>("testDict", sut2.Manager, keyCodec, valueCodec, SessionPool);
        await sut2.Lifecycle.OnStart();
        
        // Assert - Dictionary should be recovered
        Assert.Equal(3, dictionary2.Count);
        Assert.Equal(1, dictionary2["one"]);
        Assert.Equal(2, dictionary2["two"]);
        Assert.Equal(3, dictionary2["three"]);
    }
    
    [Fact]
    public async Task DurableDictionary_ComplexKeys_Test()
    {
        // Arrange
        var sut = CreateTestSystem();
        var manager = sut.Manager;
        var keyCodec = CodecProvider.GetCodec<TestKey>();
        var valueCodec = CodecProvider.GetCodec<string>();
        var dictionary = new DurableDictionary<TestKey, string>("complexDict", manager, keyCodec, valueCodec, SessionPool);
        await sut.Lifecycle.OnStart();
        
        // Act
        var key1 = new TestKey { Id = 1, Name = "Key1" };
        var key2 = new TestKey { Id = 2, Name = "Key2" };
        
        dictionary.Add(key1, "Value1");
        dictionary.Add(key2, "Value2");
        await manager.WriteStateAsync(CancellationToken.None);
        
        // Assert
        Assert.Equal(2, dictionary.Count);
        Assert.Equal("Value1", dictionary[key1]);
        Assert.Equal("Value2", dictionary[key2]);
    }
    
    [Fact]
    public async Task DurableDictionary_ComplexValues_Test()
    {
        // Arrange
        var sut = CreateTestSystem();
        var manager = sut.Manager;
        var keyCodec = CodecProvider.GetCodec<string>();
        var valueCodec = CodecProvider.GetCodec<TestPerson>();
        var dictionary = new DurableDictionary<string, TestPerson>("peopleDict", manager, keyCodec, valueCodec, SessionPool);
        await sut.Lifecycle.OnStart();
        
        // Act
        var person1 = new TestPerson { Id = 1, Name = "John", Age = 30 };
        var person2 = new TestPerson { Id = 2, Name = "Jane", Age = 25 };
        
        dictionary.Add("person1", person1);
        dictionary.Add("person2", person2);
        await manager.WriteStateAsync(CancellationToken.None);
        
        // Assert
        Assert.Equal(2, dictionary.Count);
        Assert.Equal("John", dictionary["person1"].Name);
        Assert.Equal(25, dictionary["person2"].Age);
        
        // Act - Update
        dictionary["person1"].Age = 31;
        await manager.WriteStateAsync(CancellationToken.None);
        
        // Assert
        Assert.Equal(31, dictionary["person1"].Age);
    }
    
    [Fact]
    public async Task DurableDictionary_Clear_Test()
    {
        // Arrange
        var sut = CreateTestSystem();
        var manager = sut.Manager;
        var keyCodec = CodecProvider.GetCodec<string>();
        var valueCodec = CodecProvider.GetCodec<int>();
        var dictionary = new DurableDictionary<string, int>("clearDict", manager, keyCodec, valueCodec, SessionPool);
        await sut.Lifecycle.OnStart();
        
        // Add items
        dictionary.Add("one", 1);
        dictionary.Add("two", 2);
        dictionary.Add("three", 3);
        await manager.WriteStateAsync(CancellationToken.None);
        
        // Act - Clear
        dictionary.Clear();
        await manager.WriteStateAsync(CancellationToken.None);
        
        // Assert
        Assert.Empty(dictionary);
    }
    
    [Fact]
    public async Task DurableDictionary_Enumeration_Test()
    {
        // Arrange
        var sut = CreateTestSystem();
        var manager = sut.Manager;
        var keyCodec = CodecProvider.GetCodec<string>();
        var valueCodec = CodecProvider.GetCodec<int>();
        var dictionary = new DurableDictionary<string, int>("enumDict", manager, keyCodec, valueCodec, SessionPool);
        await sut.Lifecycle.OnStart();
        
        // Add items
        var expectedPairs = new Dictionary<string, int>
        {
            { "one", 1 },
            { "two", 2 },
            { "three", 3 }
        };
        
        foreach (var pair in expectedPairs)
        {
            dictionary.Add(pair.Key, pair.Value);
        }
        
        await manager.WriteStateAsync(CancellationToken.None);
        
        // Act & Assert - Test enumeration
        var actualPairs = dictionary.ToDictionary(kv => kv.Key, kv => kv.Value);
        Assert.Equal(expectedPairs, actualPairs);
        
        // Test Keys and Values collections
        Assert.Equal(expectedPairs.Keys, dictionary.Keys);
        Assert.Equal(expectedPairs.Values, dictionary.Values);
    }
}

[GenerateSerializer]
public record class TestKey
{
    [Id(0)]
    public int Id { get; set; }
    [Id(1)]
    public string? Name { get; set; }
}
