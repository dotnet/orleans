using Xunit.Abstractions;
using Xunit.Sdk;

internal static class TestTraitNames
{
    public const string Area = "Area";
    public const string Category = "Category";
    public const string Provider = "Provider";
    public const string Suite = "Suite";
}

internal static class TestTraitDiscoverers
{
    public const string AreaDiscovererTypeName = nameof(AreaDiscoverer);
    public const string AssemblyName = "TestExtensions";
    public const string CategoryDiscovererTypeName = nameof(CategoryDiscoverer);
    public const string ProviderDiscovererTypeName = nameof(ProviderDiscoverer);
    public const string SuiteDiscovererTypeName = nameof(SuiteDiscoverer);
}

/// <summary>
/// Base implementation for discoverers which emit a single xUnit trait value from the attribute constructor.
/// </summary>
public abstract class SingleValueTraitDiscoverer : ITraitDiscoverer
{
    protected SingleValueTraitDiscoverer(IMessageSink diagnosticMessageSink)
    {
    }

    protected abstract string TraitName { get; }

    public IEnumerable<KeyValuePair<string, string>> GetTraits(IAttributeInfo traitAttribute)
    {
        if (traitAttribute.GetConstructorArguments().FirstOrDefault() is string value)
        {
            yield return new KeyValuePair<string, string>(TraitName, value);
        }
    }
}

/// <summary>
/// Marks a test as belonging to a CI suite.
/// </summary>
/// <remarks>
/// Expected values include <c>BVT</c>, <c>SlowBVT</c>, and <c>Functional</c> for standard CI,
/// plus nonstandard suites such as <c>Nightly</c> and <c>Benchmark</c>.
/// </remarks>
/// <example>
/// <code>
/// [TestSuite("BVT")]
/// </code>
/// </example>
[TraitDiscoverer(TestTraitDiscoverers.SuiteDiscovererTypeName, TestTraitDiscoverers.AssemblyName)]
[AttributeUsage(AttributeTargets.Class | AttributeTargets.Method, AllowMultiple = true)]
public sealed class TestSuiteAttribute : Attribute, ITraitAttribute
{
    public TestSuiteAttribute(string suite)
    {
        Suite = suite;
    }

    public string Suite { get; }
}

/// <summary>
/// Discovers suite traits.
/// </summary>
public sealed class SuiteDiscoverer : SingleValueTraitDiscoverer
{
    public SuiteDiscoverer(IMessageSink diagnosticMessageSink)
        : base(diagnosticMessageSink)
    {
    }

    protected override string TraitName => TestTraitNames.Suite;
}

/// <summary>
/// Marks a test with its backing provider or backend.
/// </summary>
/// <remarks>
/// Use <c>None</c> for tests without an external provider.
/// </remarks>
/// <example>
/// <code>
/// [TestProvider("None")]
/// </code>
/// </example>
[TraitDiscoverer(TestTraitDiscoverers.ProviderDiscovererTypeName, TestTraitDiscoverers.AssemblyName)]
[AttributeUsage(AttributeTargets.Class | AttributeTargets.Method, AllowMultiple = true)]
public sealed class TestProviderAttribute : Attribute, ITraitAttribute
{
    public TestProviderAttribute(string provider)
    {
        Provider = provider;
    }

    public string Provider { get; }
}

/// <summary>
/// Discovers provider traits.
/// </summary>
public sealed class ProviderDiscoverer : SingleValueTraitDiscoverer
{
    public ProviderDiscoverer(IMessageSink diagnosticMessageSink)
        : base(diagnosticMessageSink)
    {
    }

    protected override string TraitName => TestTraitNames.Provider;
}

/// <summary>
/// Marks a test with an informational functional area.
/// </summary>
/// <example>
/// <code>
/// [TestArea("Streaming")]
/// </code>
/// </example>
[TraitDiscoverer(TestTraitDiscoverers.AreaDiscovererTypeName, TestTraitDiscoverers.AssemblyName)]
[AttributeUsage(AttributeTargets.Class | AttributeTargets.Method, AllowMultiple = true)]
public sealed class TestAreaAttribute : Attribute, ITraitAttribute
{
    public TestAreaAttribute(string area)
    {
        Area = area;
    }

    public string Area { get; }
}

/// <summary>
/// Discovers area traits.
/// </summary>
public sealed class AreaDiscoverer : SingleValueTraitDiscoverer
{
    public AreaDiscoverer(IMessageSink diagnosticMessageSink)
        : base(diagnosticMessageSink)
    {
    }

    protected override string TraitName => TestTraitNames.Area;
}
