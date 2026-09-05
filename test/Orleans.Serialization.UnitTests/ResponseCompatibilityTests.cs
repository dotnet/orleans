using System;
using Orleans.Serialization.Invocation;
using Xunit;

namespace Orleans.Serialization.UnitTests;

[Trait("Category", "BVT")]
public class ResponseCompatibilityTests
{
    [Fact]
    public void ExternalResponseSubclass_UsesFinalResponseDefault()
    {
        Response response = new ExternalResponse();

        Assert.True(response.IsFinal);
    }

    private sealed class ExternalResponse : Response
    {
        public override object? Result { get; set; }
        public override Exception? Exception { get; set; }
        public override T GetResult<T>() => (T)Result!;
        public override void Dispose() { }
    }
}
