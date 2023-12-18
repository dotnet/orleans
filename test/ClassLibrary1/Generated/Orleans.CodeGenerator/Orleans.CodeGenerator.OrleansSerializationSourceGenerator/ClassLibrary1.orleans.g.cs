[assembly: global::Orleans.ApplicationPartAttribute("ClassLibrary1")]
[assembly: global::Orleans.ApplicationPartAttribute("Orleans.Core.Abstractions")]
[assembly: global::Orleans.ApplicationPartAttribute("Orleans.Serialization")]
[assembly: global::Orleans.ApplicationPartAttribute("Orleans.Core")]
[assembly: global::Orleans.Serialization.Configuration.TypeManifestProviderAttribute(typeof(OrleansCodeGen.ClassLibrary1.Metadata_ClassLibrary1))]
namespace OrleansCodeGen.ClassLibrary1
{
    using global::Orleans.Serialization.Codecs;
    using global::Orleans.Serialization.GeneratedCodeHelpers;

    [global::System.CodeDom.Compiler.GeneratedCodeAttribute("OrleansCodeGen", "8.0.0.0")]
    internal sealed class Proxy_IMyGrain : global::Orleans.Runtime.GrainReference, global::ClassLibrary1.IMyGrain
    {
        public Proxy_IMyGrain(global::Orleans.Runtime.GrainReferenceShared arg0, global::Orleans.Runtime.IdSpan arg1) : base(arg0, arg1)
        {
        }
    }

    [global::System.CodeDom.Compiler.GeneratedCodeAttribute("OrleansCodeGen", "8.0.0.0")]
    internal sealed class Metadata_ClassLibrary1 : global::Orleans.Serialization.Configuration.TypeManifestProviderBase
    {
        protected override void ConfigureInner(global::Orleans.Serialization.Configuration.TypeManifestOptions config)
        {
            config.InterfaceProxies.Add(typeof(OrleansCodeGen.ClassLibrary1.Proxy_IMyGrain));
            config.Interfaces.Add(typeof(global::ClassLibrary1.IMyGrain));
            config.WellKnownTypeAliases.Add("I-My@Grain", typeof(global::ClassLibrary1.IMyGrain));
        }
    }
}