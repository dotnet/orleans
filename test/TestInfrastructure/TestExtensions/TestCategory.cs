using Xunit.Abstractions;
using Xunit.Sdk;

/// <summary>
/// This is a replacement for the MSTest [TestCategoryAttribute] on xunit
/// xunit does not have the concept of Category for tests and instead, the have [TraitAttribute(string key, string value)]
/// If we replace the MSTest [TestCategoryAttribute] for the [Trait("Category", "BVT")], we will surely fall at some time in cases 
/// where people will typo on the "Category" key part of the Trait. 
/// On order to achieve the same behaviour as on MSTest, a custom [TestCategory] was created 
/// to mimic the MSTest one and avoid replace it on every existing test. 
/// The tests can be filtered by xunit runners by usage of "-trait" on the command line with the expression like
/// <code>-trait "Category=BVT"</code> for example that will only run the tests with [TestCategory("BVT")] on it.
/// More on Trait extensibility <see href="https://github.com/xunit/samples.xunit/tree/master/TraitExtensibility" />
/// </summary>

[TraitDiscoverer("CategoryDiscoverer", "TestExtensions")]
[AttributeUsage(AttributeTargets.Class | AttributeTargets.Method, AllowMultiple = true)]
public class TestCategoryAttribute : Attribute, ITraitAttribute
{
    public TestCategoryAttribute(string category) { }
}

public class CategoryDiscoverer : ITraitDiscoverer
{
    public CategoryDiscoverer(IMessageSink diagnosticMessageSink)
    {
    }

    public IEnumerable<KeyValuePair<string, string>> GetTraits(IAttributeInfo traitAttribute)
    {
        var ctorArgs = traitAttribute.GetConstructorArguments().ToList();
        yield return new KeyValuePair<string, string>("Category", ctorArgs[0].ToString());
    }
}
