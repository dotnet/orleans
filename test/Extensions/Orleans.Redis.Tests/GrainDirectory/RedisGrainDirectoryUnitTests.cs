using System.Net;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using Orleans.Configuration;
using Orleans.GrainDirectory.Redis;
using Orleans.Runtime;
using StackExchange.Redis;
using TestExtensions;
using Xunit;

namespace Tester.Redis.GrainDirectory;

[TestSuite("BVT")]
[TestProvider("Redis")]
[TestCategory("BVT")]
public sealed class RedisGrainDirectoryUnitTests
{
    [Fact]
    public void Constructor_NullDirectoryOptions_ThrowsArgumentNullException()
    {
        var exception = Assert.Throws<ArgumentNullException>(
            () => new RedisGrainDirectory(
                null!,
                CreateClusterOptions(),
                NullLogger<RedisGrainDirectory>.Instance));

        Assert.Equal("directoryOptions", exception.ParamName);
    }

    [Fact]
    public void Constructor_NullClusterOptions_ThrowsArgumentNullException()
    {
        var exception = Assert.Throws<ArgumentNullException>(
            () => new RedisGrainDirectory(
                new RedisGrainDirectoryOptions(),
                null!,
                NullLogger<RedisGrainDirectory>.Instance));

        Assert.Equal("clusterOptions", exception.ParamName);
    }

    [Fact]
    public void Constructor_NullLogger_ThrowsArgumentNullException()
    {
        var exception = Assert.Throws<ArgumentNullException>(
            () => new RedisGrainDirectory(
                new RedisGrainDirectoryOptions(),
                CreateClusterOptions(),
                null!));

        Assert.Equal("logger", exception.ParamName);
    }

    [Fact]
    public async Task DefaultCreateMultiplexer_NullOptions_ThrowsArgumentNullException()
    {
        var exception = await Assert.ThrowsAsync<ArgumentNullException>(
            () => RedisGrainDirectoryOptions.DefaultCreateMultiplexer(null!));

        Assert.Equal("options", exception.ParamName);
    }

    [Fact]
    public async Task Register_NullAddress_ThrowsArgumentNullException()
    {
        using var directory = CreateDirectory();

        var exception = await Assert.ThrowsAsync<ArgumentNullException>(
            () => directory.Register(null!));

        Assert.Equal("address", exception.ParamName);
    }

    [Fact]
    public async Task RegisterWithPreviousAddress_NullAddress_ThrowsArgumentNullException()
    {
        using var directory = CreateDirectory();

        var exception = await Assert.ThrowsAsync<ArgumentNullException>(
            () => directory.Register(null!, previousAddress: null));

        Assert.Equal("address", exception.ParamName);
    }

    [Fact]
    public async Task Unregister_NullAddress_ThrowsArgumentNullException()
    {
        using var directory = CreateDirectory();

        var exception = await Assert.ThrowsAsync<ArgumentNullException>(
            () => directory.Unregister(null!));

        Assert.Equal("address", exception.ParamName);
    }

    [Fact]
    public void GetKey_TypeAndKey_ReturnsExactClusterDirectoryKey()
    {
        using var directory = CreateDirectory(serviceId: "service-must-not-appear");

        var result = directory.GetKey(GrainId.Create("type", "key"));

        Assert.Equal((RedisKey)"cluster/directory/type/key", result);
    }

    [Fact]
    public async Task RegisterAndUnregister_RequirePrimaryForScripts()
    {
        var database = Substitute.For<IDatabase>();
        database.ScriptEvaluateAsync(Arg.Any<string>(), Arg.Any<RedisKey[]>(), Arg.Any<RedisValue[]>(), Arg.Any<CommandFlags>())
            .Returns(
                Task.FromResult(RedisResult.Create(RedisValue.Null)),
                Task.FromResult(RedisResult.Create(RedisValue.Null)),
                Task.FromResult(RedisResult.Create(1)));
        using var directory = CreateDirectory(database);
        await directory.Initialize(TestContext.Current.CancellationToken);
        var address = CreateAddress();
        var replacement = CreateAddress();

        Assert.Same(address, await directory.Register(address));
        Assert.Same(replacement, await directory.Register(replacement, address));
        await directory.Unregister(replacement);

        var commands = database.ReceivedCalls().ToArray();
        Assert.Equal(3, commands.Length);
        Assert.All(commands, call =>
        {
            Assert.Equal(nameof(IDatabase.ScriptEvaluateAsync), call.GetMethodInfo().Name);
            Assert.Equal(CommandFlags.DemandMaster, Assert.IsType<CommandFlags>(call.GetArguments()[3]));
            Assert.Equal(directory.GetKey(address.GrainId), Assert.Single(Assert.IsType<RedisKey[]>(call.GetArguments()[1])));
        });
        Assert.Equal(address.ActivationId.ToString(), Assert.IsType<RedisValue[]>(commands[1].GetArguments()[2])[1].ToString());
        Assert.Equal(replacement.ActivationId.ToString(), Assert.Single(Assert.IsType<RedisValue[]>(commands[2].GetArguments()[2])).ToString());
    }

    [Fact]
    public async Task Lookup_RequiresPrimaryForCurrentRegistration()
    {
        var address = CreateAddress();
        var database = Substitute.For<IDatabase>();
        database.StringGetAsync(Arg.Any<RedisKey>(), Arg.Any<CommandFlags>())
            .Returns(Task.FromResult((RedisValue)JsonSerializer.Serialize(address)));
        using var directory = CreateDirectory(database);
        await directory.Initialize(TestContext.Current.CancellationToken);

        var result = await directory.Lookup(address.GrainId);

        Assert.NotNull(result);
        Assert.Equal(address.GrainId, result.GrainId);
        Assert.Equal(address.ActivationId, result.ActivationId);
        Assert.Equal(address.SiloAddress, result.SiloAddress);
        await database.Received(1).StringGetAsync(directory.GetKey(address.GrainId), CommandFlags.DemandMaster);
    }

    private static GrainAddress CreateAddress() => new()
    {
        GrainId = GrainId.Create("type", "key"),
        ActivationId = ActivationId.NewId(),
        SiloAddress = SiloAddress.New(IPAddress.Loopback, 11111, 1)
    };

    private static RedisGrainDirectory CreateDirectory(IDatabase database)
    {
        var multiplexer = Substitute.For<IConnectionMultiplexer>();
        multiplexer.GetDatabase(Arg.Any<int>(), Arg.Any<object>()).Returns(database);
        return new(
            new RedisGrainDirectoryOptions { CreateMultiplexer = _ => Task.FromResult((multiplexer, true)) },
            CreateClusterOptions(),
            NullLogger<RedisGrainDirectory>.Instance);
    }

    private static RedisGrainDirectory CreateDirectory(string serviceId = "service") =>
        new(
            new RedisGrainDirectoryOptions(),
            CreateClusterOptions(serviceId),
            NullLogger<RedisGrainDirectory>.Instance);

    private static IOptions<ClusterOptions> CreateClusterOptions(string serviceId = "service") =>
        Options.Create(new ClusterOptions
        {
            ServiceId = serviceId,
            ClusterId = "cluster",
        });
}
