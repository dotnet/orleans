using System;
using Microsoft.Extensions.DependencyInjection;
using Newtonsoft.Json;
using Newtonsoft.Json.Serialization;
using Orleans;
using Orleans.Serialization;
using Orleans.Serialization.TypeSystem;
using Xunit;

namespace NonSilo.Tests.Serialization;

/// <summary>
/// Tests for <see cref="OrleansJsonSerializationBinder"/>, which enforces the Orleans type allow-list when
/// resolving types named in a JSON payload via <see cref="TypeNameHandling"/>. This prevents arbitrary CLR
/// types from being constructed during deserialization of persisted or streamed grain state.
/// </summary>
[TestCategory("BVT"), TestCategory("Serialization")]
public class OrleansJsonSerializationBinderTests
{
    private static ServiceProvider BuildServiceProvider(params Type[] allowedTypes)
    {
        var services = new ServiceCollection();
        services.AddSerializer(builder => builder.Configure(options =>
        {
            foreach (var type in allowedTypes)
            {
                options.AllowedTypes.Add(type.FullName!);
            }
        }));

        return services.BuildServiceProvider();
    }

    private static OrleansJsonSerializationBinder CreateStrictBinder(IServiceProvider services, bool allowAllTypes = false)
        => new OrleansJsonSerializationBinder(
            services.GetRequiredService<TypeConverter>(),
            services.GetRequiredService<TypeResolver>(),
            allowAllTypes);

    private static JsonSerializerSettings CreateSettings(ISerializationBinder binder) => new JsonSerializerSettings
    {
        TypeNameHandling = TypeNameHandling.All,
        TypeNameAssemblyFormatHandling = TypeNameAssemblyFormatHandling.Simple,
        SerializationBinder = binder,
    };

    [Fact]
    public void BindToType_AllowedByConfiguration_ResolvesType()
    {
        using var services = BuildServiceProvider(typeof(AllowedState));
        var binder = CreateStrictBinder(services);

        var type = binder.BindToType(typeof(AllowedState).Assembly.GetName().Name, typeof(AllowedState).FullName!);

        Assert.Equal(typeof(AllowedState), type);
    }

    [Fact]
    public void BindToType_NotAllowed_Throws()
    {
        using var services = BuildServiceProvider(typeof(AllowedState));
        var binder = CreateStrictBinder(services);

        var exception = Assert.Throws<JsonSerializationException>(() =>
            binder.BindToType(typeof(DisallowedState).Assembly.GetName().Name, typeof(DisallowedState).FullName!));

        Assert.Contains(nameof(OrleansJsonSerializerOptions.AllowAllTypes), exception.Message);
    }

    [Fact]
    public void BindToType_NotAllowed_WithAllowAllTypes_ResolvesType()
    {
        using var services = BuildServiceProvider(typeof(AllowedState));
        var binder = CreateStrictBinder(services, allowAllTypes: true);

        var type = binder.BindToType(typeof(DisallowedState).Assembly.GetName().Name, typeof(DisallowedState).FullName!);

        Assert.Equal(typeof(DisallowedState), type);
    }

    [Fact]
    public void BindToType_LegacyConstructor_ResolvesAnyType()
    {
        using var services = BuildServiceProvider(typeof(AllowedState));
        var binder = new OrleansJsonSerializationBinder(services.GetRequiredService<TypeResolver>());

        var type = binder.BindToType(typeof(DisallowedState).Assembly.GetName().Name, typeof(DisallowedState).FullName!);

        Assert.Equal(typeof(DisallowedState), type);
    }

    [Fact]
    public void BindToType_GenerateSerializerType_IsAllowedWithoutConfiguration()
    {
        var services = new ServiceCollection();
        services.AddSerializer(builder => builder.AddAssembly(typeof(GeneratedState).Assembly));
        using var provider = services.BuildServiceProvider();
        var binder = CreateStrictBinder(provider);

        var type = binder.BindToType(typeof(GeneratedState).Assembly.GetName().Name, typeof(GeneratedState).FullName!);

        Assert.Equal(typeof(GeneratedState), type);
    }

    [Fact]
    public void Deserialize_AllowedType_RoundTrips()
    {
        using var services = BuildServiceProvider(typeof(AllowedState));
        var settings = CreateSettings(CreateStrictBinder(services));
        var payload = new AllowedState { Name = "orleans", Value = 42 };

        var json = JsonConvert.SerializeObject(payload, settings);
        var result = Assert.IsType<AllowedState>(JsonConvert.DeserializeObject(json, typeof(object), settings));

        Assert.Equal(payload.Name, result.Name);
        Assert.Equal(payload.Value, result.Value);
    }

    [Fact]
    public void Deserialize_DisallowedType_Throws()
    {
        using var services = BuildServiceProvider(typeof(AllowedState));
        var settings = CreateSettings(CreateStrictBinder(services));
        var json = CreateDisallowedPayload();

        Assert.Throws<JsonSerializationException>(() => JsonConvert.DeserializeObject(json, typeof(object), settings));
    }

    [Fact]
    public void Deserialize_DisallowedType_WithAllowAllTypes_Succeeds()
    {
        using var services = BuildServiceProvider(typeof(AllowedState));
        var settings = CreateSettings(CreateStrictBinder(services, allowAllTypes: true));
        var json = CreateDisallowedPayload();

        var result = JsonConvert.DeserializeObject(json, typeof(object), settings);

        var state = Assert.IsType<DisallowedState>(result);
        Assert.Equal("gadget", state.Name);
    }

    private static string CreateDisallowedPayload()
    {
        var assemblyName = typeof(DisallowedState).Assembly.GetName().Name;
        return $"{{\"$type\":\"{typeof(DisallowedState).FullName}, {assemblyName}\",\"Name\":\"gadget\",\"Value\":1}}";
    }
}

public sealed class AllowedState
{
    public string Name { get; set; } = null!;
    public int Value { get; set; }
}

public sealed class DisallowedState
{
    public string Name { get; set; } = null!;
    public int Value { get; set; }
}

[GenerateSerializer]
public sealed class GeneratedState
{
    [Id(0)] public string Name { get; set; } = null!;
    [Id(1)] public int Value { get; set; }
}
