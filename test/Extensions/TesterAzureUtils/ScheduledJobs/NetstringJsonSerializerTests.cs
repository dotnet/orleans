using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using FluentAssertions;
using Orleans.Runtime;
using Orleans.ScheduledJobs.AzureStorage;
using Xunit;

namespace Tester.AzureUtils.ScheduledJobs;

[TestCategory("ScheduledJobs"), TestCategory("BVT")]
public class NetstringJsonSerializerTests
{
    [Fact]
    public void Encode_RemoveOperation_ProducesCorrectFormat()
    {
        var operation = JobOperation.CreateRemoveOperation("job123");
        var result = NetstringJsonSerializer.Encode(operation, JobOperationJsonContext.Default.JobOperation);
        var resultString = Encoding.UTF8.GetString(result);
        
        resultString.Should().EndWith("\n");
        resultString.Should().Match("*:*\n");
        resultString.Should().Contain("\"type\":1");
        resultString.Should().Contain("\"id\":\"job123\"");
    }

    [Fact]
    public void Encode_AddOperation_ProducesCorrectFormat()
    {
        var dueTime = new DateTimeOffset(2025, 10, 31, 12, 0, 0, TimeSpan.Zero);
        var grainId = GrainId.Create("test", "grain1");
        var operation = JobOperation.CreateAddOperation("job456", "TestJob", dueTime, grainId, null);
        var result = NetstringJsonSerializer.Encode(operation, JobOperationJsonContext.Default.JobOperation);
        var resultString = Encoding.UTF8.GetString(result);
        
        resultString.Should().EndWith("\n");
        resultString.Should().Match("*:*\n");
        resultString.Should().Contain("\"id\":\"job456\"");
        resultString.Should().Contain("\"name\":\"TestJob\"");
    }

    [Fact]
    public void Encode_RetryOperation_ProducesCorrectFormat()
    {
        var dueTime = new DateTimeOffset(2025, 10, 31, 12, 0, 0, TimeSpan.Zero);
        var operation = JobOperation.CreateRetryOperation("job789", dueTime);
        var result = NetstringJsonSerializer.Encode(operation, JobOperationJsonContext.Default.JobOperation);
        var resultString = Encoding.UTF8.GetString(result);
        
        resultString.Should().EndWith("\n");
        resultString.Should().Match("*:*\n");
        resultString.Should().Contain("\"type\":2");
        resultString.Should().Contain("\"id\":\"job789\"");
    }

    [Fact]
    public void Encode_AddOperationWithMetadata_ProducesCorrectFormat()
    {
        var dueTime = new DateTimeOffset(2025, 10, 31, 12, 0, 0, TimeSpan.Zero);
        var grainId = GrainId.Create("test", "grain1");
        var metadata = new Dictionary<string, string> { ["key1"] = "value1", ["key2"] = "value2" };
        var operation = JobOperation.CreateAddOperation("job999", "MetaJob", dueTime, grainId, metadata);
        var result = NetstringJsonSerializer.Encode(operation, JobOperationJsonContext.Default.JobOperation);
        var resultString = Encoding.UTF8.GetString(result);
        
        resultString.Should().EndWith("\n");
        resultString.Should().Contain("\"metadata\"");
        resultString.Should().Contain("\"key1\":\"value1\"");
        resultString.Should().Contain("\"key2\":\"value2\"");
    }

    [Fact]
    public void Encode_VerifiesNetstringFormat()
    {
        var operation = JobOperation.CreateRemoveOperation("test");
        var result = NetstringJsonSerializer.Encode(operation, JobOperationJsonContext.Default.JobOperation);
        var resultString = Encoding.UTF8.GetString(result);
        
        var parts = resultString.Split(':', 2);
        parts.Should().HaveCount(2);
        
        var lengthStr = parts[0];
        int.TryParse(lengthStr, out var length).Should().BeTrue();
        length.Should().BeGreaterThan(0);
        
        var dataAndNewline = parts[1];
        dataAndNewline.Should().EndWith("\n");
        
        var jsonData = dataAndNewline[..^1];
        var jsonBytes = Encoding.UTF8.GetBytes(jsonData);
        jsonBytes.Length.Should().Be(length);
    }

    [Fact]
    public async Task DecodeAsync_RemoveOperation_DecodesCorrectly()
    {
        var operation = JobOperation.CreateRemoveOperation("job123");
        var encoded = NetstringJsonSerializer.Encode(operation, JobOperationJsonContext.Default.JobOperation);
        var stream = new MemoryStream(encoded);
        
        var results = new List<JobOperation>();
        await foreach (var item in NetstringJsonSerializer.DecodeAsync(stream, JobOperationJsonContext.Default.JobOperation))
        {
            results.Add(item);
        }
        
        results.Should().HaveCount(1);
        results[0].Type.Should().Be(JobOperation.OperationType.Remove);
        results[0].Id.Should().Be("job123");
    }

    [Fact]
    public async Task DecodeAsync_AddOperation_DecodesCorrectly()
    {
        var dueTime = new DateTimeOffset(2025, 10, 31, 12, 0, 0, TimeSpan.Zero);
        var grainId = GrainId.Create("test", "grain1");
        var operation = JobOperation.CreateAddOperation("job456", "TestJob", dueTime, grainId, null);
        var encoded = NetstringJsonSerializer.Encode(operation, JobOperationJsonContext.Default.JobOperation);
        var stream = new MemoryStream(encoded);
        
        var results = new List<JobOperation>();
        await foreach (var item in NetstringJsonSerializer.DecodeAsync(stream, JobOperationJsonContext.Default.JobOperation))
        {
            results.Add(item);
        }
        
        results.Should().HaveCount(1);
        results[0].Type.Should().Be(JobOperation.OperationType.Add);
        results[0].Id.Should().Be("job456");
        results[0].Name.Should().Be("TestJob");
        results[0].DueTime.Should().Be(dueTime);
        results[0].TargetGrainId.Should().Be(grainId);
    }

    [Fact]
    public async Task DecodeAsync_MultipleOperations_DecodesCorrectly()
    {
        var dueTime = new DateTimeOffset(2025, 10, 31, 12, 0, 0, TimeSpan.Zero);
        var grainId = GrainId.Create("test", "grain1");
        var op1 = JobOperation.CreateAddOperation("job1", "Job1", dueTime, grainId, null);
        var op2 = JobOperation.CreateRemoveOperation("job2");
        var op3 = JobOperation.CreateRetryOperation("job3", dueTime.AddHours(1));
        
        var stream = new MemoryStream();
        await stream.WriteAsync(NetstringJsonSerializer.Encode(op1, JobOperationJsonContext.Default.JobOperation));
        await stream.WriteAsync(NetstringJsonSerializer.Encode(op2, JobOperationJsonContext.Default.JobOperation));
        await stream.WriteAsync(NetstringJsonSerializer.Encode(op3, JobOperationJsonContext.Default.JobOperation));
        stream.Position = 0;
        
        var results = new List<JobOperation>();
        await foreach (var item in NetstringJsonSerializer.DecodeAsync(stream, JobOperationJsonContext.Default.JobOperation))
        {
            results.Add(item);
        }
        
        results.Should().HaveCount(3);
        results[0].Type.Should().Be(JobOperation.OperationType.Add);
        results[0].Id.Should().Be("job1");
        results[1].Type.Should().Be(JobOperation.OperationType.Remove);
        results[1].Id.Should().Be("job2");
        results[2].Type.Should().Be(JobOperation.OperationType.Retry);
        results[2].Id.Should().Be("job3");
    }

    [Fact]
    public async Task DecodeAsync_AddOperationWithMetadata_DecodesCorrectly()
    {
        var dueTime = new DateTimeOffset(2025, 10, 31, 12, 0, 0, TimeSpan.Zero);
        var grainId = GrainId.Create("test", "grain1");
        var metadata = new Dictionary<string, string> { ["key1"] = "value1", ["key2"] = "value2" };
        var operation = JobOperation.CreateAddOperation("job999", "MetaJob", dueTime, grainId, metadata);
        var encoded = NetstringJsonSerializer.Encode(operation, JobOperationJsonContext.Default.JobOperation);
        var stream = new MemoryStream(encoded);
        
        var results = new List<JobOperation>();
        await foreach (var item in NetstringJsonSerializer.DecodeAsync(stream, JobOperationJsonContext.Default.JobOperation))
        {
            results.Add(item);
        }
        
        results.Should().HaveCount(1);
        results[0].Metadata.Should().NotBeNull();
        results[0].Metadata.Should().ContainKey("key1").WhoseValue.Should().Be("value1");
        results[0].Metadata.Should().ContainKey("key2").WhoseValue.Should().Be("value2");
    }

    [Fact]
    public async Task DecodeAsync_EmptyStream_ReturnsEmpty()
    {
        var stream = new MemoryStream();
        
        var results = new List<JobOperation>();
        await foreach (var item in NetstringJsonSerializer.DecodeAsync(stream, JobOperationJsonContext.Default.JobOperation))
        {
            results.Add(item);
        }
        
        results.Should().BeEmpty();
    }

    [Fact]
    public async Task DecodeAsync_InvalidLength_ThrowsInvalidDataException()
    {
        var encoded = "abc:{\"type\":1,\"id\":\"test\"}\n";
        var stream = new MemoryStream(Encoding.UTF8.GetBytes(encoded));
        
        var act = async () =>
        {
            await foreach (var item in NetstringJsonSerializer.DecodeAsync(stream, JobOperationJsonContext.Default.JobOperation))
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
        var encoded = "-5:{\"type\":1}\n";
        var stream = new MemoryStream(Encoding.UTF8.GetBytes(encoded));
        
        var act = async () =>
        {
            await foreach (var item in NetstringJsonSerializer.DecodeAsync(stream, JobOperationJsonContext.Default.JobOperation))
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
        var json = "{\"type\":1,\"id\":\"test\"}";
        var jsonBytes = Encoding.UTF8.GetBytes(json);
        var encoded = $"{jsonBytes.Length}:{json}";
        var stream = new MemoryStream(Encoding.UTF8.GetBytes(encoded));
        
        var act = async () =>
        {
            await foreach (var item in NetstringJsonSerializer.DecodeAsync(stream, JobOperationJsonContext.Default.JobOperation))
            {
                // Should throw after reading the data
            }
        };
        
        await act.Should().ThrowAsync<InvalidDataException>()
            .WithMessage("Expected newline at end of netstring, got byte value *");
    }

    [Fact]
    public async Task DecodeAsync_IncompleteData_ThrowsInvalidDataException()
    {
        var encoded = "100:{\"type\":1}";
        var stream = new MemoryStream(Encoding.UTF8.GetBytes(encoded));
        
        var act = async () =>
        {
            await foreach (var item in NetstringJsonSerializer.DecodeAsync(stream, JobOperationJsonContext.Default.JobOperation))
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
        var json = "{\"type\":1,\"id\":\"test\"}";
        var jsonBytes = Encoding.UTF8.GetBytes(json);
        var encoded = $"{jsonBytes.Length}:{json}X";
        var stream = new MemoryStream(Encoding.UTF8.GetBytes(encoded));
        
        var act = async () =>
        {
            await foreach (var item in NetstringJsonSerializer.DecodeAsync(stream, JobOperationJsonContext.Default.JobOperation))
            {
                // Should throw after reading the data
            }
        };
        
        await act.Should().ThrowAsync<InvalidDataException>()
            .WithMessage("Expected newline at end of netstring, got byte value *");
    }

    [Fact]
    public async Task DecodeAsync_InvalidJson_ThrowsJsonException()
    {
        var invalidJson = "{invalid json}";
        var jsonBytes = Encoding.UTF8.GetBytes(invalidJson);
        var encoded = $"{jsonBytes.Length}:{invalidJson}\n";
        var stream = new MemoryStream(Encoding.UTF8.GetBytes(encoded));
        
        var act = async () =>
        {
            await foreach (var item in NetstringJsonSerializer.DecodeAsync(stream, JobOperationJsonContext.Default.JobOperation))
            {
                // Should throw when deserializing
            }
        };
        
        await act.Should().ThrowAsync<JsonException>();
    }

    [Fact]
    public async Task EncodeAndDecode_RoundTrip_PreservesData()
    {
        var dueTime1 = new DateTimeOffset(2025, 10, 31, 12, 0, 0, TimeSpan.Zero);
        var dueTime2 = new DateTimeOffset(2025, 11, 1, 14, 30, 0, TimeSpan.Zero);
        var grainId = GrainId.Create("test", "grain1");
        var metadata = new Dictionary<string, string> { ["env"] = "prod", ["region"] = "us-east" };
        
        var testOperations = new[]
        {
            JobOperation.CreateRemoveOperation("remove-job"),
            JobOperation.CreateAddOperation("add-job", "MyJob", dueTime1, grainId, null),
            JobOperation.CreateRetryOperation("retry-job", dueTime2),
            JobOperation.CreateAddOperation("meta-job", "MetaJob", dueTime1, grainId, metadata)
        };

        foreach (var operation in testOperations)
        {
            var encoded = NetstringJsonSerializer.Encode(operation, JobOperationJsonContext.Default.JobOperation);
            var stream = new MemoryStream(encoded);
            
            var results = new List<JobOperation>();
            await foreach (var item in NetstringJsonSerializer.DecodeAsync(stream, JobOperationJsonContext.Default.JobOperation))
            {
                results.Add(item);
            }
            
            results.Should().HaveCount(1);
            results[0].Type.Should().Be(operation.Type);
            results[0].Id.Should().Be(operation.Id);
            results[0].Name.Should().Be(operation.Name);
            results[0].DueTime.Should().Be(operation.DueTime);
            results[0].TargetGrainId.Should().Be(operation.TargetGrainId);
            
            if (operation.Metadata is not null)
            {
                results[0].Metadata.Should().NotBeNull();
                results[0].Metadata.Should().BeEquivalentTo(operation.Metadata);
            }
        }
    }

    [Fact]
    public async Task EncodeAndDecode_MultipleOperations_RoundTrip()
    {
        var dueTime = new DateTimeOffset(2025, 10, 31, 12, 0, 0, TimeSpan.Zero);
        var grainId = GrainId.Create("test", "grain1");
        
        var testOperations = new[]
        {
            JobOperation.CreateAddOperation("job1", "First", dueTime, grainId, null),
            JobOperation.CreateRemoveOperation("job2"),
            JobOperation.CreateRetryOperation("job3", dueTime.AddHours(1)),
            JobOperation.CreateAddOperation("job4", "Fourth", dueTime.AddDays(1), grainId, null)
        };

        var memoryStream = new MemoryStream();
        foreach (var operation in testOperations)
        {
            var encoded = NetstringJsonSerializer.Encode(operation, JobOperationJsonContext.Default.JobOperation);
            await memoryStream.WriteAsync(encoded);
        }
        
        memoryStream.Position = 0;
        
        var results = new List<JobOperation>();
        await foreach (var item in NetstringJsonSerializer.DecodeAsync(memoryStream, JobOperationJsonContext.Default.JobOperation))
        {
            results.Add(item);
        }
        
        results.Should().HaveCount(4);
        for (var i = 0; i < testOperations.Length; i++)
        {
            results[i].Type.Should().Be(testOperations[i].Type);
            results[i].Id.Should().Be(testOperations[i].Id);
        }
    }

    [Fact]
    public async Task DecodeAsync_StreamPosition_IsPreserved()
    {
        var operation = JobOperation.CreateRemoveOperation("test");
        var encoded = NetstringJsonSerializer.Encode(operation, JobOperationJsonContext.Default.JobOperation);
        var stream = new MemoryStream(encoded);
        
        await foreach (var item in NetstringJsonSerializer.DecodeAsync(stream, JobOperationJsonContext.Default.JobOperation))
        {
            // Stream should be at the end after reading
        }
        
        stream.Position.Should().Be(stream.Length);
    }

    [Fact]
    public async Task EncodeAndDecode_LargeMetadata_HandlesCorrectly()
    {
        var dueTime = new DateTimeOffset(2025, 10, 31, 12, 0, 0, TimeSpan.Zero);
        var grainId = GrainId.Create("test", "grain1");
        
        var largeMetadata = new Dictionary<string, string>();
        for (var i = 0; i < 100; i++)
        {
            largeMetadata[$"key{i}"] = new string('x', 1000);
        }
        
        var operation = JobOperation.CreateAddOperation("large-job", "LargeMetaJob", dueTime, grainId, largeMetadata);
        var encoded = NetstringJsonSerializer.Encode(operation, JobOperationJsonContext.Default.JobOperation);
        var stream = new MemoryStream(encoded);
        
        var results = new List<JobOperation>();
        await foreach (var item in NetstringJsonSerializer.DecodeAsync(stream, JobOperationJsonContext.Default.JobOperation))
        {
            results.Add(item);
        }
        
        results.Should().HaveCount(1);
        results[0].Metadata.Should().NotBeNull();
        results[0].Metadata.Should().HaveCount(100);
    }
}
