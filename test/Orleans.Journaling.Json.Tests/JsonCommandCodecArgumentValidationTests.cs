using System.Text;
using System.Text.Json;
using Orleans.Journaling.Tests;
using Xunit;

namespace Orleans.Journaling.Json.Tests;

[TestSuite("BVT")]
[TestProvider("None")]
[TestCategory("BVT")]
public sealed class JsonCommandCodecArgumentValidationTests
{
    private static readonly JsonSerializerOptions Options = new() { TypeInfoResolver = JsonCodecTestJsonContext.Default };

    [Fact]
    public void DictionaryApply_NullConsumer_ThrowsBeforeReadingInput()
    {
        var codec = new JsonDurableDictionaryCommandCodec<string, int>(Options);

        AssertNullConsumerRejected(input => codec.Apply(input, null!));
    }

    [Fact]
    public void ListApply_NullConsumer_ThrowsBeforeReadingInput()
    {
        var codec = new JsonDurableListCommandCodec<int>(Options);

        AssertNullConsumerRejected(input => codec.Apply(input, null!));
    }

    [Fact]
    public void QueueApply_NullConsumer_ThrowsBeforeReadingInput()
    {
        var codec = new JsonDurableQueueCommandCodec<int>(Options);

        AssertNullConsumerRejected(input => codec.Apply(input, null!));
    }

    [Fact]
    public void SetApply_NullConsumer_ThrowsBeforeReadingInput()
    {
        var codec = new JsonDurableSetCommandCodec<int>(Options);

        AssertNullConsumerRejected(input => codec.Apply(input, null!));
    }

    [Fact]
    public void ValueApply_NullConsumer_ThrowsBeforeReadingInput()
    {
        var codec = new JsonDurableValueCommandCodec<int>(Options);

        AssertNullConsumerRejected(input => codec.Apply(input, null!));
    }

    [Fact]
    public void PersistentStateApply_NullConsumer_ThrowsBeforeReadingInput()
    {
        var codec = new JsonPersistentStateCommandCodec<int>(Options);

        AssertNullConsumerRejected(input => codec.Apply(input, null!));
    }

    [Fact]
    public void TaskCompletionSourceApply_NullConsumer_ThrowsBeforeReadingInput()
    {
        var codec = new JsonDurableTaskCompletionSourceCommandCodec<int>(Options);

        AssertNullConsumerRejected(input => codec.Apply(input, null!));
    }

    [Fact]
    public void TaskCompletionSourceWriteFaulted_NullException_ThrowsWithoutMutatingWriter()
    {
        var codec = new JsonDurableTaskCompletionSourceCommandCodec<int>(Options);
        using var writer = new JsonLinesJournalFormat().CreateWriter();
        var streamWriter = writer.CreateJournalStreamWriter(new JournalStreamId(1));

        var exception = Assert.Throws<ArgumentNullException>(() => codec.WriteFaulted(null!, streamWriter));

        Assert.Equal("exception", exception.ParamName);
        using (var buffer = writer.GetBuffer())
        {
            Assert.Equal(0, buffer.Length);
        }

        codec.WritePending(streamWriter);
        using var committed = writer.GetBuffer();
        Assert.Equal("[1,[\"pending\"]]\n", Encoding.UTF8.GetString(committed.ToArray()));
    }

    private static void AssertNullConsumerRejected(Action<JournalBufferReader> apply)
    {
        var input = CodecTestHelpers.ReadBuffer("not-json"u8.ToArray());
        var expected = input.ToArray();

        var exception = Assert.Throws<ArgumentNullException>(() => apply(input));

        Assert.Equal("consumer", exception.ParamName);
        Assert.Equal(expected, input.ToArray());
    }
}
