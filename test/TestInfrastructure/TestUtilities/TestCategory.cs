using Xunit.v3;

/// <summary>
/// Provides a compatibility bridge for the legacy xUnit <c>Category</c> trait while the test suite
/// migrates to first-class <see cref="TestSuiteAttribute"/>, <see cref="TestProviderAttribute"/>, and
/// <see cref="TestAreaAttribute"/> traits.
/// </summary>
/// <remarks>
/// New tests should prefer the first-class traits. Existing tests can continue using
/// <c>[TestCategory("BVT")]</c> until they are migrated.
/// </remarks>
/// <example>
/// <code>
/// [TestCategory("BVT")]
/// </code>
/// </example>
[AttributeUsage(AttributeTargets.Class | AttributeTargets.Method, AllowMultiple = true)]
public class TestCategoryAttribute : Attribute, ITraitAttribute
{
    public TestCategoryAttribute(string category)
    {
        Category = category;
    }

    public string Category { get; }

    public IReadOnlyCollection<KeyValuePair<string, string>> GetTraits() =>
        [new(TestTraitNames.Category, Category)];
}
