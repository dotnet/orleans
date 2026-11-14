using Xunit.v3;

internal static class TestTraitNames
{
    public const string Area = "Area";
    public const string Category = "Category";
    public const string Provider = "Provider";
    public const string Suite = "Suite";
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
[AttributeUsage(AttributeTargets.Assembly | AttributeTargets.Class | AttributeTargets.Method, AllowMultiple = true)]
public sealed class TestSuiteAttribute : Attribute, ITraitAttribute
{
    public TestSuiteAttribute(string suite)
    {
        Suite = suite;
    }

    public string Suite { get; }

    public IReadOnlyCollection<KeyValuePair<string, string>> GetTraits() =>
        [new(TestTraitNames.Suite, Suite)];
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
[AttributeUsage(AttributeTargets.Assembly | AttributeTargets.Class | AttributeTargets.Method, AllowMultiple = true)]
public sealed class TestProviderAttribute : Attribute, ITraitAttribute
{
    public TestProviderAttribute(string provider)
    {
        Provider = provider;
    }

    public string Provider { get; }

    public IReadOnlyCollection<KeyValuePair<string, string>> GetTraits() =>
        [new(TestTraitNames.Provider, Provider)];
}

/// <summary>
/// Marks a test with an informational functional area.
/// </summary>
/// <example>
/// <code>
/// [TestArea("Streaming")]
/// </code>
/// </example>
[AttributeUsage(AttributeTargets.Assembly | AttributeTargets.Class | AttributeTargets.Method, AllowMultiple = true)]
public sealed class TestAreaAttribute : Attribute, ITraitAttribute
{
    public TestAreaAttribute(string area)
    {
        Area = area;
    }

    public string Area { get; }

    public IReadOnlyCollection<KeyValuePair<string, string>> GetTraits() =>
        [new(TestTraitNames.Area, Area)];
}
