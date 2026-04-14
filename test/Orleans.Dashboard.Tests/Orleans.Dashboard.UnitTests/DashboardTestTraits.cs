using Xunit.Abstractions;
using Xunit.Sdk;

internal static class DashboardTraitNames
{
    public const string Area = "Area";
    public const string Provider = "Provider";
    public const string Suite = "Suite";
}

internal static class DashboardTraitDiscoverers
{
    public const string AreaDiscovererTypeName = nameof(DashboardAreaDiscoverer);
    public const string AssemblyName = "Orleans.Dashboard.UnitTests";
    public const string ProviderDiscovererTypeName = nameof(DashboardProviderDiscoverer);
    public const string SuiteDiscovererTypeName = nameof(DashboardSuiteDiscoverer);
}

public abstract class DashboardSingleValueTraitDiscoverer : ITraitDiscoverer
{
    protected DashboardSingleValueTraitDiscoverer(IMessageSink diagnosticMessageSink)
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

[TraitDiscoverer(DashboardTraitDiscoverers.SuiteDiscovererTypeName, DashboardTraitDiscoverers.AssemblyName)]
[AttributeUsage(AttributeTargets.Class | AttributeTargets.Method, AllowMultiple = true)]
public sealed class TestSuiteAttribute : Attribute, ITraitAttribute
{
    public TestSuiteAttribute(string suite)
    {
        Suite = suite;
    }

    public string Suite { get; }
}

public sealed class DashboardSuiteDiscoverer : DashboardSingleValueTraitDiscoverer
{
    public DashboardSuiteDiscoverer(IMessageSink diagnosticMessageSink)
        : base(diagnosticMessageSink)
    {
    }

    protected override string TraitName => DashboardTraitNames.Suite;
}

[TraitDiscoverer(DashboardTraitDiscoverers.ProviderDiscovererTypeName, DashboardTraitDiscoverers.AssemblyName)]
[AttributeUsage(AttributeTargets.Class | AttributeTargets.Method, AllowMultiple = true)]
public sealed class TestProviderAttribute : Attribute, ITraitAttribute
{
    public TestProviderAttribute(string provider)
    {
        Provider = provider;
    }

    public string Provider { get; }
}

public sealed class DashboardProviderDiscoverer : DashboardSingleValueTraitDiscoverer
{
    public DashboardProviderDiscoverer(IMessageSink diagnosticMessageSink)
        : base(diagnosticMessageSink)
    {
    }

    protected override string TraitName => DashboardTraitNames.Provider;
}

[TraitDiscoverer(DashboardTraitDiscoverers.AreaDiscovererTypeName, DashboardTraitDiscoverers.AssemblyName)]
[AttributeUsage(AttributeTargets.Class | AttributeTargets.Method, AllowMultiple = true)]
public sealed class TestAreaAttribute : Attribute, ITraitAttribute
{
    public TestAreaAttribute(string area)
    {
        Area = area;
    }

    public string Area { get; }
}

public sealed class DashboardAreaDiscoverer : DashboardSingleValueTraitDiscoverer
{
    public DashboardAreaDiscoverer(IMessageSink diagnosticMessageSink)
        : base(diagnosticMessageSink)
    {
    }

    protected override string TraitName => DashboardTraitNames.Area;
}
