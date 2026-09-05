using System.Buffers;

namespace Orleans.Journaling.Tests;

[TestSuite("BVT")]
[TestProvider("None")]
[TestCategory("BVT")]
public sealed class ReadOnlySequenceStreamTests
{
    [Fact]
    public void Read_WhenBufferIsNullAndCountIsZero_ThrowsArgumentNullException()
    {
        using var stream = new S3ReadOnlySequenceStream(ReadOnlySequence<byte>.Empty);
        var read = typeof(Stream).GetMethod(nameof(Stream.Read), [typeof(byte[]), typeof(int), typeof(int)])!;

        var invocationException = Assert.Throws<System.Reflection.TargetInvocationException>(
            () => read.Invoke(stream, [null, 0, 0]));
        var exception = Assert.IsType<ArgumentNullException>(invocationException.InnerException);

        Assert.Equal("buffer", exception.ParamName);
    }
}
