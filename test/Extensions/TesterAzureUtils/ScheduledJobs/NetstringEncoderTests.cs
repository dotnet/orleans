using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using FluentAssertions;
using Orleans.ScheduledJobs.AzureStorage;
using Xunit;

namespace Tester.AzureUtils.ScheduledJobs;

[TestCategory("ScheduledJobs")]
public class NetstringEncoderTests
{
    [Fact]
    public void Encode_SimpleString_ProducesCorrectFormat()
    {
        var input = "hello";
        var result = NetstringEncoder.Encode(input);
        var resultString = Encoding.UTF8.GetString(result);
        
        resultString.Should().Be("5:hello\n");
    }

    [Fact]
    public void Encode_EmptyString_ProducesCorrectFormat()
    {
        var input = "";
        var result = NetstringEncoder.Encode(input);
        var resultString = Encoding.UTF8.GetString(result);
        
        resultString.Should().Be("0:\n");
    }

    [Fact]
    public void Encode_StringWithSpecialCharacters_ProducesCorrectFormat()
    {
        var input = "hello\nworld";
        var result = NetstringEncoder.Encode(input);
        var resultString = Encoding.UTF8.GetString(result);
        
        resultString.Should().Be("11:hello\nworld\n");
    }

    [Fact]
    public void Encode_StringWithUnicode_ProducesCorrectByteCount()
    {
        var input = "Hello 世界";
        var result = NetstringEncoder.Encode(input);
        var expectedBytes = Encoding.UTF8.GetBytes(input);
        var resultString = Encoding.UTF8.GetString(result);
        
        resultString.Should().StartWith($"{expectedBytes.Length}:");
        resultString.Should().EndWith("\n");
        resultString.Should().Contain(input);
    }

    [Fact]
    public void Encode_LongString_ProducesCorrectFormat()
    {
        var input = new string('a', 1000);
        var result = NetstringEncoder.Encode(input);
        var resultString = Encoding.UTF8.GetString(result);
        
        resultString.Should().StartWith("1000:");
        resultString.Should().EndWith("\n");
    }

    [Fact]
    public void Encode_JsonString_ProducesCorrectFormat()
    {
        var input = "{\"name\":\"test\",\"value\":123}";
        var result = NetstringEncoder.Encode(input);
        var resultString = Encoding.UTF8.GetString(result);
        
        resultString.Should().StartWith("27:");
        resultString.Should().Contain(input);
        resultString.Should().EndWith("\n");
    }

    [Fact]
    public async Task DecodeAsync_SimpleString_DecodesCorrectly()
    {
        var encoded = "5:hello\n";
        var stream = new MemoryStream(Encoding.UTF8.GetBytes(encoded));
        
        var results = new List<string>();
        await foreach (var item in NetstringEncoder.DecodeAsync(stream))
        {
            results.Add(item);
        }
        
        results.Should().HaveCount(1);
        results[0].Should().Be("hello");
    }

    [Fact]
    public async Task DecodeAsync_EmptyString_DecodesCorrectly()
    {
        var encoded = "0:\n";
        var stream = new MemoryStream(Encoding.UTF8.GetBytes(encoded));
        
        var results = new List<string>();
        await foreach (var item in NetstringEncoder.DecodeAsync(stream))
        {
            results.Add(item);
        }
        
        results.Should().HaveCount(1);
        results[0].Should().Be("");
    }

    [Fact]
    public async Task DecodeAsync_MultipleStrings_DecodesCorrectly()
    {
        var encoded = "5:hello\n5:world\n3:foo\n";
        var stream = new MemoryStream(Encoding.UTF8.GetBytes(encoded));
        
        var results = new List<string>();
        await foreach (var item in NetstringEncoder.DecodeAsync(stream))
        {
            results.Add(item);
        }
        
        results.Should().HaveCount(3);
        results[0].Should().Be("hello");
        results[1].Should().Be("world");
        results[2].Should().Be("foo");
    }

    [Fact]
    public async Task DecodeAsync_StringWithNewline_DecodesCorrectly()
    {
        var encoded = "11:hello\nworld\n";
        var stream = new MemoryStream(Encoding.UTF8.GetBytes(encoded));
        
        var results = new List<string>();
        await foreach (var item in NetstringEncoder.DecodeAsync(stream))
        {
            results.Add(item);
        }
        
        results.Should().HaveCount(1);
        results[0].Should().Be("hello\nworld");
    }

    [Fact]
    public async Task DecodeAsync_UnicodeString_DecodesCorrectly()
    {
        var input = "Hello 世界";
        var encoded = NetstringEncoder.Encode(input);
        var stream = new MemoryStream(encoded);
        
        var results = new List<string>();
        await foreach (var item in NetstringEncoder.DecodeAsync(stream))
        {
            results.Add(item);
        }
        
        results.Should().HaveCount(1);
        results[0].Should().Be(input);
    }

    [Fact]
    public async Task DecodeAsync_EmptyStream_ReturnsEmpty()
    {
        var stream = new MemoryStream();
        
        var results = new List<string>();
        await foreach (var item in NetstringEncoder.DecodeAsync(stream))
        {
            results.Add(item);
        }
        
        results.Should().BeEmpty();
    }

    [Fact]
    public async Task DecodeAsync_InvalidLength_ThrowsInvalidDataException()
    {
        var encoded = "abc:hello\n";
        var stream = new MemoryStream(Encoding.UTF8.GetBytes(encoded));
        
        var act = async () =>
        {
            await foreach (var item in NetstringEncoder.DecodeAsync(stream))
            {
                // Should throw before yielding any items
            }
        };
        
        await act.Should().ThrowAsync<InvalidDataException>()
            .WithMessage("Invalid netstring length: abc");
    }

    [Fact]
    public async Task DecodeAsync_NegativeLength_ThrowsInvalidDataException()
    {
        var encoded = "-5:hello\n";
        var stream = new MemoryStream(Encoding.UTF8.GetBytes(encoded));
        
        var act = async () =>
        {
            await foreach (var item in NetstringEncoder.DecodeAsync(stream))
            {
                // Should throw before yielding any items
            }
        };
        
        await act.Should().ThrowAsync<InvalidDataException>()
            .WithMessage("Netstring length cannot be negative: -5");
    }

    [Fact]
    public async Task DecodeAsync_MissingTrailingNewline_ThrowsInvalidDataException()
    {
        var encoded = "5:hello";
        var stream = new MemoryStream(Encoding.UTF8.GetBytes(encoded));
        
        var act = async () =>
        {
            await foreach (var item in NetstringEncoder.DecodeAsync(stream))
            {
                // Should throw after reading the data
            }
        };
        
        await act.Should().ThrowAsync<InvalidDataException>()
            .WithMessage("Expected newline at end of netstring, got '*'");
    }

    [Fact]
    public async Task DecodeAsync_IncompleteData_ThrowsInvalidDataException()
    {
        var encoded = "10:hello";
        var stream = new MemoryStream(Encoding.UTF8.GetBytes(encoded));
        
        var act = async () =>
        {
            await foreach (var item in NetstringEncoder.DecodeAsync(stream))
            {
                // Should throw before yielding any items
            }
        };
        
        await act.Should().ThrowAsync<InvalidDataException>()
            .WithMessage("Unexpected end of stream while reading netstring data");
    }

    [Fact]
    public async Task DecodeAsync_WrongTrailingCharacter_ThrowsInvalidDataException()
    {
        var encoded = "5:helloX";
        var stream = new MemoryStream(Encoding.UTF8.GetBytes(encoded));
        
        var act = async () =>
        {
            await foreach (var item in NetstringEncoder.DecodeAsync(stream))
            {
                // Should throw after reading the data
            }
        };
        
        await act.Should().ThrowAsync<InvalidDataException>()
            .WithMessage("Expected newline at end of netstring, got 'X'");
    }

    [Fact]
    public async Task EncodeAndDecode_RoundTrip_PreservesData()
    {
        var testStrings = new[]
        {
            "",
            "hello",
            "hello world",
            "line1\nline2\nline3",
            "{\"json\":\"data\"}",
            "Hello 世界 🌍",
            new string('x', 10000)
        };

        foreach (var testString in testStrings)
        {
            var encoded = NetstringEncoder.Encode(testString);
            var stream = new MemoryStream(encoded);
            
            var results = new List<string>();
            await foreach (var item in NetstringEncoder.DecodeAsync(stream))
            {
                results.Add(item);
            }
            
            results.Should().HaveCount(1);
            results[0].Should().Be(testString);
        }
    }

    [Fact]
    public async Task EncodeAndDecode_MultipleStrings_RoundTrip()
    {
        var testStrings = new[]
        {
            "first",
            "second",
            "third with\nnewlines",
            "fourth",
            ""
        };

        var memoryStream = new MemoryStream();
        foreach (var testString in testStrings)
        {
            var encoded = NetstringEncoder.Encode(testString);
            await memoryStream.WriteAsync(encoded);
        }
        
        memoryStream.Position = 0;
        
        var results = new List<string>();
        await foreach (var item in NetstringEncoder.DecodeAsync(memoryStream))
        {
            results.Add(item);
        }
        
        results.Should().Equal(testStrings);
    }

    [Fact]
    public async Task DecodeAsync_StreamPosition_IsPreserved()
    {
        var encoded = "5:hello\n";
        var stream = new MemoryStream(Encoding.UTF8.GetBytes(encoded));
        
        await foreach (var item in NetstringEncoder.DecodeAsync(stream))
        {
            // Stream should be at the end after reading
        }
        
        stream.Position.Should().Be(stream.Length);
    }

    [Fact]
    public async Task DecodeAsync_LargeLength_HandlesCorrectly()
    {
        var largeString = new string('a', 100000);
        var encoded = NetstringEncoder.Encode(largeString);
        var stream = new MemoryStream(encoded);
        
        var results = new List<string>();
        await foreach (var item in NetstringEncoder.DecodeAsync(stream))
        {
            results.Add(item);
        }
        
        results.Should().HaveCount(1);
        results[0].Should().Be(largeString);
        results[0].Length.Should().Be(100000);
    }
}
