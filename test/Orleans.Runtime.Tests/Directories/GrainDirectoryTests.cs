#nullable enable
using Microsoft.Extensions.Logging;
using Orleans.GrainDirectory;
using TestExtensions;
using Xunit;

namespace Tester.Directories;

// Base tests for custom Grain Directory
public abstract class GrainDirectoryTests<TGrainDirectory> where TGrainDirectory : IGrainDirectory
{
    protected readonly ILoggerFactory loggerFactory;
    private TGrainDirectory? _directory;

    protected GrainDirectoryTests(ITestOutputHelper testOutput)
    {
        this.loggerFactory = new LoggerFactory();
        this.loggerFactory.AddProvider(new XunitLoggerProvider(testOutput));
    }

    protected TGrainDirectory GrainDirectory => _directory ??= CreateGrainDirectory();

    protected abstract TGrainDirectory CreateGrainDirectory();

    [Fact]
    public async Task RegisterLookupUnregisterLookup()
    {
        var expected = new GrainAddress
        {
            ActivationId = ActivationId.NewId(),
            GrainId = GrainId.Parse("user/somerandomuser_" + Guid.NewGuid().ToString("N")),
            SiloAddress = SiloAddress.FromParsableString("10.0.23.12:1000@5678"),
            MembershipVersion = new MembershipVersion(51)
        };

        Assert.Equal(expected, await GrainDirectory.Register(expected, null, TestContext.Current.CancellationToken));

        Assert.Equal(expected, await GrainDirectory.Lookup(expected.GrainId, TestContext.Current.CancellationToken));

        await GrainDirectory.Unregister(expected, TestContext.Current.CancellationToken);

        Assert.Null(await GrainDirectory.Lookup(expected.GrainId, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task DoNotOverwriteEntry()
    {
        var expected = new GrainAddress
        {
            ActivationId = ActivationId.NewId(),
            GrainId = GrainId.Parse("user/somerandomuser_" + Guid.NewGuid().ToString("N")),
            SiloAddress = SiloAddress.FromParsableString("10.0.23.12:1000@5678"),
            MembershipVersion = new MembershipVersion(51)
        };

        var differentActivation = new GrainAddress
        {
            ActivationId = ActivationId.NewId(),
            GrainId = expected.GrainId,
            SiloAddress = SiloAddress.FromParsableString("10.0.23.12:1000@5678"),
            MembershipVersion = new MembershipVersion(51)
        };

        var differentSilo = new GrainAddress
        {
            ActivationId = expected.ActivationId,
            GrainId = expected.GrainId,
            SiloAddress = SiloAddress.FromParsableString("10.0.23.14:1000@4583"),
            MembershipVersion = new MembershipVersion(51)
        };

        Assert.Equal(expected, await GrainDirectory.Register(expected, null, TestContext.Current.CancellationToken));
        Assert.Equal(expected, await GrainDirectory.Register(differentActivation, null, TestContext.Current.CancellationToken));
        Assert.Equal(expected, await GrainDirectory.Register(differentSilo, null, TestContext.Current.CancellationToken));

        Assert.Equal(expected, await GrainDirectory.Lookup(expected.GrainId, TestContext.Current.CancellationToken));
    }

    /// <summary>
    /// Overwrite an existing entry if the register call includes a matching "previousAddress" parameter.
    /// </summary>
    [Fact]
    public async Task OverwriteEntryIfMatch()
    {
        var initial = new GrainAddress
        {
            ActivationId = ActivationId.NewId(),
            GrainId = GrainId.Parse("user/somerandomuser_" + Guid.NewGuid().ToString("N")),
            SiloAddress = SiloAddress.FromParsableString("10.0.23.12:1000@5678"),
            MembershipVersion = new MembershipVersion(51)
        };

        var differentActivation = new GrainAddress
        {
            ActivationId = ActivationId.NewId(),
            GrainId = initial.GrainId,
            SiloAddress = initial.SiloAddress,
            MembershipVersion = initial.MembershipVersion
        };

        var differentSilo = new GrainAddress
        {
            ActivationId = initial.ActivationId,
            GrainId = initial.GrainId,
            SiloAddress = SiloAddress.FromParsableString("10.0.23.14:1000@4583"),
            MembershipVersion = initial.MembershipVersion
        };

        // Success, no registration exists, so the previous address is ignored.
        Assert.Equal(initial, await GrainDirectory.Register(initial, differentSilo, TestContext.Current.CancellationToken));

        // Success, the previous address matches the existing registration.
        Assert.Equal(differentActivation, await GrainDirectory.Register(differentActivation, initial, TestContext.Current.CancellationToken));

        // Failure, the previous address does not match the existing registration.
        Assert.Equal(differentActivation, await GrainDirectory.Register(differentSilo, initial, TestContext.Current.CancellationToken));

        Assert.Equal(differentActivation, await GrainDirectory.Lookup(initial.GrainId, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task DoNotDeleteDifferentActivationIdEntry()
    {
        var expected = new GrainAddress
        {
            ActivationId = ActivationId.NewId(),
            GrainId = GrainId.Parse("user/somerandomuser_" + Guid.NewGuid().ToString("N")),
            SiloAddress = SiloAddress.FromParsableString("10.0.23.12:1000@5678"),
            MembershipVersion = new MembershipVersion(51)
        };

        var otherEntry = new GrainAddress
        {
            ActivationId = ActivationId.NewId(),
            GrainId = expected.GrainId,
            SiloAddress = SiloAddress.FromParsableString("10.0.23.12:1000@5678"),
            MembershipVersion = new MembershipVersion(51)
        };

        Assert.Equal(expected, await GrainDirectory.Register(expected, null, TestContext.Current.CancellationToken));
        await GrainDirectory.Unregister(otherEntry, TestContext.Current.CancellationToken);
        Assert.Equal(expected, await GrainDirectory.Lookup(expected.GrainId, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task LookupNotFound()
    {
        Assert.Null(await GrainDirectory.Lookup(
            GrainId.Parse("user/somerandomuser_" + Guid.NewGuid().ToString("N")),
            TestContext.Current.CancellationToken));
    }
}
