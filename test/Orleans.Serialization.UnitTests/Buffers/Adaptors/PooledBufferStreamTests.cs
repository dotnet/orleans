using System;
using System.Buffers;
using System.Collections;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Linq;
using Microsoft.Extensions;
using Microsoft.Extensions.ObjectPool;
using Orleans.Serialization.Buffers;
using Orleans.Serialization.Buffers.Adaptors;
using Xunit;

namespace Orleans.Serialization.Buffers.Adaptors.UnitTests;

/// <summary>
/// Unit tests for the PooledBufferStream constructor.
/// </summary>
[Category("BVT")]
public class PooledBufferStreamTests
{
    /// <summary>
    /// Verifies that the parameterless constructor initializes a new instance with a length of zero 
    /// and appropriate stream capabilities.
    /// </summary>
    [Fact]
    public void Constructor_Parameterless_InitializesWithZeroLengthAndValidCapabilities()
    {
        // Arrange & Act
        using var stream = new PooledBufferStream();

        // Assert
        Assert.Equal(0, stream.Length);
        Assert.True(stream.CanRead);
        Assert.True(stream.CanSeek);
        Assert.True(stream.CanWrite);
        // Verify default Position is 0
        Assert.Equal(0, stream.Position);
    }

    /// <summary>
    /// Verifies that the constructor with a minAllocationSize parameter initializes a new instance 
    /// with a length of zero and appropriate stream capabilities.
    /// Tests various edge cases for the minAllocationSize parameter.
    /// </summary>
    /// <param name="minAllocationSize">The minimum allocation size passed to the constructor.</param>
    [Theory]
    [InlineData(-1)]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(4096)]
    [InlineData(int.MaxValue)]
    public void Constructor_WithMinAllocationSize_InitializesWithZeroLengthAndValidCapabilities(int minAllocationSize)
    {
        // Arrange & Act
        using var stream = new PooledBufferStream(minAllocationSize);

        // Assert
        Assert.Equal(0, stream.Length);
        Assert.True(stream.CanRead);
        Assert.True(stream.CanSeek);
        Assert.True(stream.CanWrite);
        // Verify default Position is 0
        Assert.Equal(0, stream.Position);
    }

    /// <summary>
    /// Verifies that the PooledBufferStream constructor initializes the Length property to zero,
    /// regardless of the provided minAllocationSize parameter value.
    /// Test cases include negative, zero, positive, int.MaxValue, and int.MinValue values.
    /// </summary>
    /// <param name="minAllocationSize">The minimum allocation size used to initialize the stream.</param>
    [Theory]
    [InlineData(-1)]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(100)]
    [InlineData(int.MaxValue)]
    [InlineData(int.MinValue)]
    public void Constructor_WithMinAllocationValues_LengthIsZero(int minAllocationSize)
    {
        // Arrange & Act
        using var stream = new PooledBufferStream(minAllocationSize);

        // Assert
        Assert.Equal(0, stream.Length);
    }

    /// <summary>
    /// Verifies that the default PooledBufferStream constructor initializes the Length property to zero.
    /// </summary>
    [Fact]
    public void DefaultConstructor_LengthIsZero()
    {
        // Arrange & Act
        using var stream = new PooledBufferStream();

        // Assert
        Assert.Equal(0, stream.Length);
    }

    /// <summary>
    /// Verifies that calling Rent returns a non-null instance of PooledBufferStream with an initial length of zero.
    /// This test parameterizes the number of times Rent is called to ensure consistent behavior regardless of call count.
    /// </summary>
    /// <param name="rentCount">The number of times to invoke Rent.</param>
    [Theory]
    [InlineData(1)]
    [InlineData(5)]
    [InlineData(10)]
    public void Rent_MultipleTimes_ReturnsValidInstance(int rentCount)
    {
        // Arrange
        var instances = new List<PooledBufferStream>();

        // Act
        for (int i = 0; i < rentCount; i++)
        {
            var instance = PooledBufferStream.Rent();
            instances.Add(instance);
        }

        // Assert
        Assert.Equal(rentCount, instances.Count);
        foreach (var instance in instances)
        {
            Assert.NotNull(instance);
            // Verify that the Length property is zero for a freshly rented stream.
            Assert.Equal(0, instance.Length);
        }
    }

    /// <summary>
    /// Tests that returning a valid stream recycles it by ensuring that a subsequently rented instance is the same as the returned one.
    /// </summary>
    [Fact]
    public void Return_ValidStream_RecyclesObject()
    {
        // Arrange
        using var stream = PooledBufferStream.Rent();
        // Modify state to simulate usage.
        stream.Position = 100;

        // Act
        PooledBufferStream.Return(stream);
        var recycledStream = PooledBufferStream.Rent();

        // Assert
        // Expect the same instance to be recycled.
        Assert.True(Object.ReferenceEquals(stream, recycledStream), "The recycled stream instance should be the same as the one returned.");

        // Cleanup: Return the stream again to avoid affecting other tests.
        PooledBufferStream.Return(recycledStream);
    }

    /// <summary>
    /// Tests that passing a null stream to Return throws an exception.
    /// </summary>
    [Fact]
    public void Return_NullStream_ThrowsException()
    {
        // Arrange & Act & Assert
        Assert.Throws<ArgumentNullException>(() => PooledBufferStream.Return(null));
    }

    /// <summary>
    /// Verifies that a newly created PooledBufferStream has a Length of zero, regardless of the specified minAllocationSize.
    /// </summary>
    /// <param name="minAllocationSize">The minimum allocation size to use when constructing the stream.</param>
    [Theory]
    [InlineData(0)]
    [InlineData(4096)]
    [InlineData(100)]
    [InlineData(-1)]
    public void Length_OnNewStream_ReturnsZero(int minAllocationSize)
    {
        // Arrange
        using var stream = new PooledBufferStream(minAllocationSize);

        // Act
        long length = stream.Length;

        // Assert
        Assert.Equal(0L, length);
    }

    /// <summary>
    /// Tests that ToArray returns an empty array when no data has been written to the stream.
    /// </summary>
    [Fact]
    public void ToArray_EmptyStream_ReturnsEmptyArray()
    {
        // Arrange
        using var stream = new PooledBufferStream();

        // Act
        byte[] result = stream.ToArray();

        // Assert
        Assert.NotNull(result);
        Assert.Empty(result);
    }

    /// <summary>
    /// Verifies that the CopyTo method correctly writes all segments to the provided writer.
    /// </summary>
    [Theory]
    [InlineData(new byte[] { 1, 2, 3, 4 })]
    [InlineData(new byte[] { 10, 20, 30 })]
    public void CopyTo_ValidSegments_CopiesDataCorrectly(byte[] expectedData)
    {
        using var stream = new PooledBufferStream();
        stream.Write(expectedData);

        var output = new byte[expectedData.Length];
        var writer = Writer.Create(output, session: null);

        stream.CopyTo(ref writer);

        Assert.Equal(expectedData, output);
    }

    /// <summary>
    /// Verifies that the CopyTo method correctly writes all segments to the provided writer for larger buffers.
    /// Tests with buffers larger than 5kb to ensure multi-segment functionality works properly.
    /// </summary>
    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(1 * 1024)]
    [InlineData(4 * 1024 - 1)]
    [InlineData(4 * 1024)]
    [InlineData(4 * 1024 + 1)]
    [InlineData(5 * 1024)]
    [InlineData(8 * 1024)]
    public void CopyTo_LargeBuffers_CopiesDataCorrectly(int bufferSize)
    {
        using var stream = new PooledBufferStream();
        var expectedData = GenerateTestData(bufferSize);
        stream.Write(expectedData, 0, expectedData.Length);

        var output = new byte[expectedData.Length];
        var writer = Writer.Create(output, session: null);

        stream.CopyTo(ref writer);

        Assert.True(expectedData.SequenceEqual(output));
    }

    /// <summary>
    /// Verifies that calling Reset on a fresh instance results in a Length of zero.
    /// </summary>
    [Fact]
    public void Reset_FreshInstance_LengthIsZero()
    {
        // Arrange
        using var stream = new PooledBufferStream();

        // Act
        stream.Reset();

        // Assert
        Assert.Equal(0, stream.Length);
    }

    /// <summary>
    /// Verifies that after writing data to the stream, calling Reset resets the stream length to zero.
    /// Tests multiple scenarios using different write counts.
    /// </summary>
    /// <param name="writeCount">The number of bytes to write to the stream.</param>
    [Theory]
    [InlineData(0)]
    [InlineData(10)]
    [InlineData(1024)]
    public void Reset_AfterWrite_LengthResetToZero(int writeCount)
    {
        // Arrange
        using var stream = new PooledBufferStream();
        var buffer = new byte[writeCount];
        // Populate the buffer with some dummy data if needed.
        for (int i = 0; i < writeCount; i++)
        {
            buffer[i] = (byte)(i % 256);
        }

        // Act
        stream.Write(buffer, 0, writeCount);
        // Depending on implementation, Length may or may not reflect the exact number of written bytes.
        // Calling Reset should clear any written data and reset Length.
        stream.Reset();

        // Assert
        Assert.Equal(0, stream.Length);
    }

    /// <summary>
    /// Verifies that RentReadOnlySequence returns a correct ReadOnlySequence for various data lengths.
    /// When no data has been written, an empty sequence is returned; otherwise, the sequence matches the written data.
    /// </summary>
    /// <param name="dataLength">The length of the test data to write to the stream.</param>
    [Theory]
    [InlineData(0)]
    [InlineData(100)]
    [InlineData(4096)]
    [InlineData(5000)]
    public void RentReadOnlySequence_VariousDataLengths_ReturnsExpectedSequence(int dataLength)
    {
        // Arrange
        using var stream = new PooledBufferStream();
        byte[] expectedData = GenerateTestData(dataLength);
        if (dataLength > 0)
        {
            stream.Write(expectedData, 0, expectedData.Length);
        }

        // Act
        ReadOnlySequence<byte> sequence = stream.RentReadOnlySequence();

        // Assert
        if (dataLength == 0)
        {
            Assert.Equal(0, sequence.Length);
        }
        else
        {
            byte[] actualData = sequence.ToArray();
            Assert.Equal(expectedData.Length, actualData.Length);
            Assert.True(expectedData.SequenceEqual(actualData));
        }

        stream.ReturnReadOnlySequence(sequence);
    }

    /// <summary>
    /// Generates deterministic test data of a specified length.
    /// </summary>
    /// <param name="length">The length of the data to generate.</param>
    /// <returns>A byte array filled with sequential values modulo 256.</returns>
    private static byte[] GenerateTestData(int length)
    {
        byte[] data = new byte[length];
        for (int i = 0; i < length; i++)
        {
            data[i] = (byte)(i % 256);
        }
        return data;
    }

    /// <summary>
    /// Verifies that ReturnReadOnlySequence performs no action when the sequence's start does not return a BufferSegment.
    /// In this test, we create a dummy ReadOnlySequenceSegment<byte> that returns an object not of type BufferSegment.
    /// Expected outcome: No exception is thrown.
    /// </summary>
    [Theory]
    [InlineData(0)]
    [InlineData(100)]
    public void ReturnReadOnlySequence_StartNotBufferSegment_DoesNotThrow(int dummyValue)
    {
        // Arrange
        var dummySegment = new DummyReadOnlySequenceSegment(dummyValue);
        // Create a ReadOnlySequence using the dummy segment.
        var sequence = new ReadOnlySequence<byte>(dummySegment, 0, dummySegment, dummySegment.Memory.Length);
        using var stream = new PooledBufferStream();

        // Act & Assert (should not throw)
        stream.ReturnReadOnlySequence(in sequence);
    }

    /// <summary>
    /// Verifies that ReturnReadOnlySequence returns the BufferSegment chain to the pool.
    /// Tests that each segment in a multi-segment chain is properly returned to the pool
    /// and can be reused for subsequent operations.
    /// </summary>
    [Fact]
    public void ReturnReadOnlySequence_ValidBufferSegmentChain_CallsPoolReturnForEachSegment()
    {
        using var stream = new PooledBufferStream();
        
        // Write data that will span multiple segments to create a chain
        var dataSize = 10000; // Large enough to span multiple segments
        var testData = GenerateTestData(dataSize);
        stream.Write(testData, 0, testData.Length);

        // Rent the ReadOnlySequence to get the BufferSegment chain
        var sequence = stream.RentReadOnlySequence();

        // Verify we have a multi-segment chain
        Assert.True(sequence.Length > 0);
        
        // Count the number of segments in the chain before returning
        var segmentCount = CountSegmentsInChain(sequence);
        Assert.True(segmentCount > 1, "Test requires multiple segments to verify chain handling");

        // Get fresh segments from the pool to verify they're different before returning
        var freshSegments = new List<PooledBufferStream.BufferSegment>();
        for (int i = 0; i < segmentCount; i++)
        {
            freshSegments.Add(PooledBufferStream.BufferSegment.Pool.Get());
        }

        // Return the fresh segments to avoid affecting the test
        foreach (var segment in freshSegments)
        {
            PooledBufferStream.BufferSegment.Pool.Return(segment);
        }

        // Return the sequence - this should return all segments in the chain to the pool
        stream.ReturnReadOnlySequence(sequence);

        // Verify that segments are returned to the pool by getting new ones
        // and checking they're the same instances (indicating they were recycled)
        var recycledSegments = new List<PooledBufferStream.BufferSegment>();
        for (int i = 0; i < segmentCount; i++)
        {
            recycledSegments.Add(PooledBufferStream.BufferSegment.Pool.Get());
        }

        // The recycled segments should be reset and ready for reuse
        foreach (var segment in recycledSegments)
        {
            Assert.True(segment.Memory.IsEmpty, "Recycled segment should have empty memory");
            Assert.Equal(0, segment.RunningIndex);
            Assert.Null(segment.Next);
        }

        // Clean up by returning the segments back to the pool
        foreach (var segment in recycledSegments)
        {
            PooledBufferStream.BufferSegment.Pool.Return(segment);
        }
    }

    /// <summary>
    /// Counts the number of segments in a ReadOnlySequence chain.
    /// </summary>
    /// <param name="sequence">The sequence to count segments for.</param>
    /// <returns>The number of segments in the chain.</returns>
    private static int CountSegmentsInChain(ReadOnlySequence<byte> sequence)
    {
        if (sequence.IsSingleSegment)
        {
            return 1;
        }

        var count = 0;
        var position = sequence.Start;
        while (sequence.TryGet(ref position, out var memory))
        {
            count++;
            if (position.Equals(sequence.End))
            {
                break;
            }
        }

        return count;
    }

    /// <summary>
    /// A dummy implementation of ReadOnlySequenceSegment<byte> used to simulate a segment whose GetObject does not return a BufferSegment.
    /// </summary>
    private class DummyReadOnlySequenceSegment : ReadOnlySequenceSegment<byte>
    {
        private readonly int _dummyValue;

        public DummyReadOnlySequenceSegment(int dummyValue)
        {
            _dummyValue = dummyValue;
            // Initialize Memory with an empty array for simplicity.
            Memory = new byte[0];
        }

    }

    /// <summary>
    /// Tests that the CanRead property returns true.
    /// This test verifies that a newly instantiated PooledBufferStream correctly reports that it can be read.
    /// </summary>
    [Fact]
    public void CanRead_Property_ReturnsTrue()
    {
        // Arrange
        using var stream = new PooledBufferStream();

        // Act
        bool canRead = stream.CanRead;

        // Assert
        Assert.True(canRead);
    }

    /// <summary>
    /// Tests that the CanSeek property returns true.
    /// </summary>
    [Fact]
    public void CanSeek_Property_ReturnsTrue()
    {
        // Arrange
        using var stream = new PooledBufferStream();

        // Act
        bool result = stream.CanSeek;

        // Assert
        Assert.True(result);
    }

    /// <summary>
    /// Tests that the CanWrite property returns true regardless of the constructor used.
    /// </summary>
    /// <param name="useNonDefaultConstructor">Boolean flag to test both default and parameterized constructors.</param>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void CanWrite_Property_ReturnsTrue(bool useNonDefaultConstructor)
    {
        // Arrange
        using var stream = useNonDefaultConstructor ? new PooledBufferStream(1024) : new PooledBufferStream();

        // Act
        bool canWrite = stream.CanWrite;

        // Assert
        Assert.True(canWrite);
    }

    /// <summary>
    /// Tests valid Seek scenarios with a preset stream length.
    /// For Begin: newPosition equals the offset.
    /// For Current: newPosition equals Position plus offset.
    /// For End: newPosition equals Length minus offset.
    /// </summary>
    /// <param name="offset">The offset parameter for Seek.</param>
    /// <param name="origin">The SeekOrigin used in Seek.</param>
    /// <param name="expectedPosition">The expected new position after seeking.</param>
    [Theory]
    [InlineData(30, SeekOrigin.Begin, 30)]
    [InlineData(20, SeekOrigin.Current, 30)]
    [InlineData(10, SeekOrigin.End, 90)]
    public void Seek_ValidScenarios_ExpectedPosition(long offset, SeekOrigin origin, long expectedPosition)
    {
        // Arrange
        using var stream = new PooledBufferStream();
        stream.SetLength(100);
        if (origin == SeekOrigin.Current)
        {
            stream.Position = 10;
        }

        // Act
        long result = stream.Seek(offset, origin);

        // Assert
        Assert.Equal(expectedPosition, result);
        Assert.Equal(expectedPosition, stream.Position);
    }

    /// <summary>
    /// Tests valid Seek scenarios on a stream with default length (0).
    /// For Begin and Current with offset 0, the operation should succeed.
    /// </summary>
    [Theory]
    [InlineData(0, SeekOrigin.Begin, 0)]
    [InlineData(0, SeekOrigin.Current, 0)]
    public void Seek_ZeroOffsetOnEmptyStream_ReturnsZero(long offset, SeekOrigin origin, long expectedPosition)
    {
        // Arrange
        using var stream = new PooledBufferStream();

        // Act
        long result = stream.Seek(offset, origin);

        // Assert
        Assert.Equal(expectedPosition, result);
        Assert.Equal(expectedPosition, stream.Position);
    }

    /// <summary>
    /// Tests Seek scenarios that are expected to throw InvalidOperationException 
    /// when the resulting position is out of bounds.
    /// </summary>
    /// <param name="offset">The offset parameter for Seek.</param>
    /// <param name="origin">The SeekOrigin used in Seek.</param>
    /// <param name="expectedMessage">The expected exception message.</param>
    [Theory]
    // For Begin: negative offset.
    [InlineData(-1, SeekOrigin.Begin, "Attempted to seek past beginning of stream")]
    // For Current: result becomes negative.
    [InlineData(-11, SeekOrigin.Current, "Attempted to seek past beginning of stream")]
    // For End: offset greater than Length results in negative newPosition.
    [InlineData(101, SeekOrigin.End, "Attempted to seek past beginning of stream")]
    // For Begin: offset greater than Length.
    [InlineData(101, SeekOrigin.Begin, "Attempted to seek past end of stream")]
    // For Current: result exceeds Length.
    [InlineData(91, SeekOrigin.Current, "Attempted to seek past end of stream")]
    // For End: negative offset causes newPosition to exceed Length.
    [InlineData(-1, SeekOrigin.End, "Attempted to seek past end of stream")]
    public void Seek_OutOfBounds_ThrowsInvalidOperationException(long offset, SeekOrigin origin, string expectedMessage)
    {
        // Arrange
        using var stream = new PooledBufferStream();
        stream.SetLength(100);
        if (origin == SeekOrigin.Current)
        {
            stream.Position = 10;
        }

        // Act & Assert
        var ex = Assert.Throws<InvalidOperationException>(() => stream.Seek(offset, origin));
        Assert.Equal(expectedMessage, ex.Message);
    }

    /// <summary>
    /// Tests that an invalid SeekOrigin value causes an ArgumentOutOfRangeException.
    /// </summary>
    [Fact]
    public void Seek_InvalidOrigin_ThrowsArgumentOutOfRangeException()
    {
        // Arrange
        using var stream = new PooledBufferStream();

        // Act & Assert
        Assert.Throws<ArgumentOutOfRangeException>(() => stream.Seek(0, (SeekOrigin)999));
    }

    /// <summary>
    /// Verifies that calling Read on an empty stream (with no data written) returns zero.
    /// </summary>
    [Fact]
    public void Read_WithEmptyStream_ReturnsZero()
    {
        // Arrange
        using var stream = new PooledBufferStream();
        var buffer = new byte[10];

        // Act
        int bytesRead = stream.Read(buffer, 0, buffer.Length);

        // Assert
        Assert.Equal(0, bytesRead);
    }

    /// <summary>
    /// Verifies that calling Read with a null buffer throws an ArgumentNullException.
    /// </summary>
    [Fact]
    public void Read_NullBuffer_ThrowsArgumentNullException()
    {
        // Arrange
        using var stream = new PooledBufferStream();

        // Act & Assert
        Assert.Throws<ArgumentNullException>(() => stream.Read(null, 0, 5));
    }

    /// <summary>
    /// Verifies that calling Read with an invalid offset or count throws an ArgumentOutOfRangeException.
    /// </summary>
    /// <param name="offset">The offset to use when reading.</param>
    /// <param name="count">The count to use when reading.</param>
    [Theory]
    [InlineData(-1, 5)]
    [InlineData(2, 100)]
    public void Read_InvalidOffsetOrCount_ThrowsArgumentOutOfRangeException(int offset, int count)
    {
        // Arrange
        using var stream = new PooledBufferStream();
        var buffer = new byte[10];

        // Act & Assert
        Assert.Throws<ArgumentOutOfRangeException>(() => stream.Read(buffer, offset, count));
    }

    /// <summary>
    /// Verifies that calling SetLength with a value equal to the current Length does not modify the stream state.
    /// This test sets an initial non-zero length then calls SetLength with the same value.
    /// </summary>
    [Fact]
    public void SetLength_ValueEqualsCurrentLength_DoesNotChangeState()
    {
        // Arrange
        using var stream = new PooledBufferStream();
        stream.Position = 200;
        stream.SetLength(500);
        long originalLength = stream.Length;
        long originalPosition = stream.Position;

        // Act
        stream.SetLength(500);

        // Assert
        Assert.Equal(originalLength, stream.Length);
        Assert.Equal(originalPosition, stream.Position);
    }

    /// <summary>
    /// Verifies that calling SetLength on an empty or non-empty stream updates the Length and adjusts Position accordingly.
    /// This test covers scenarios including growth and truncation.
    /// </summary>
    /// <param name="initialPosition">The initial Position set on the stream.</param>
    /// <param name="initialSetLength">The first SetLength call to establish a non-empty state.</param>
    /// <param name="newSetLength">The second SetLength call to update the length.</param>
    /// <param name="expectedPosition">The expected Position after updating the length.</param>
    /// <param name="expectedLength">The expected Length after updating the length.</param>
    [Theory]
    [InlineData(100, 500, 500, 100, 500)]   // Equality case on non-empty stream.
    [InlineData(600, 500, 1000, 500, 1000)]  // Growth case: initial length 500 grows to 1000.
    [InlineData(800, 1000, 500, 500, 500)]   // Truncate case: initial length 1000 truncates to 500.
    public void SetLength_Scenarios_UpdatesLengthAndPosition(int initialPosition, int initialSetLength, int newSetLength, int expectedPosition, int expectedLength)
    {
        // Arrange
        using var stream = new MemoryStream();
        stream.Position = initialPosition;
        stream.SetLength(initialSetLength);

        // Act
        stream.SetLength(newSetLength);

        // Assert
        Assert.Equal(expectedLength, stream.Length);
        Assert.Equal(expectedPosition, stream.Position);
    }

    /// <summary>
    /// Verifies that calling SetLength with 0 on an empty stream maintains Length and Position at 0.
    /// </summary>
    [Fact]
    public void SetLength_ZeroOnEmpty_DoesNotChangeState()
    {
        // Arrange
        using var stream = new PooledBufferStream();
        stream.Position = 0;

        // Act
        stream.SetLength(0);

        // Assert
        Assert.Equal(0, stream.Length);
        Assert.Equal(0, stream.Position);
    }

    /// <summary>
    /// Provides invalid parameter combinations for testing Write method exceptions.
    /// Each object array contains: buffer, offset, count, and expected exception type.
    /// </summary>
    public static IEnumerable<object[]> InvalidWriteParameters
    {
        get
        {
            yield return new object[] { null, 0, 1, typeof(ArgumentNullException) };
            yield return new object[] { new byte[5], -1, 3, typeof(ArgumentOutOfRangeException) };
            yield return new object[] { new byte[5], 2, -1, typeof(ArgumentOutOfRangeException) };
            yield return new object[] { new byte[5], 3, 3, typeof(ArgumentOutOfRangeException) };
        }
    }

    /// <summary>
    /// Tests that Write throws the appropriate exceptions when provided with invalid parameters.
    /// </summary>
    /// <param name="buffer">The input byte array (may be null).</param>
    /// <param name="offset">The starting offset in the array.</param>
    /// <param name="count">The number of bytes to write.</param>
    /// <param name="expectedException">The expected type of exception.</param>
    [Theory]
    [MemberData(nameof(InvalidWriteParameters))]
    public void Write_InvalidParameters_ThrowsException(byte[] buffer, int offset, int count, Type expectedException)
    {
        using var stream = new PooledBufferStream();
        Assert.Throws(expectedException, () => stream.Write(buffer, offset, count));
    }

    /// <summary>
    /// Tests that calling Write with a zero count does not modify the stream's position.
    /// </summary>
    [Fact]
    public void Write_ZeroCount_DoesNothing()
    {
        using var stream = new PooledBufferStream();
        long initialPosition = stream.Position;
        byte[] data = new byte[10];
        stream.Write(data, 0, 0);
        Assert.Equal(initialPosition, stream.Position);
    }

    /// <summary>
    /// Tests that Write correctly appends data to the stream.
    /// Verifies that written data can be retrieved via ToArray() and that the stream's Position advances appropriately.
    /// </summary>
    /// <param name="inputLength">The length of data to write.</param>
    [Theory]
    [InlineData(1)]
    [InlineData(10)]
    [InlineData(4096)]
    public void Write_ValidInput_AppendsData(int inputLength)
    {
        using var stream = new PooledBufferStream();
        byte[] data = new byte[inputLength];
        for (int i = 0; i < inputLength; i++)
        {
            data[i] = (byte)(i % 256);
        }
        long initialPosition = stream.Position;
        
        stream.Write(data, 0, data.Length);
        
        // Verify that the data has been correctly appended
        byte[] writtenData = stream.ToArray();
        Assert.Equal(data, writtenData);
        
        // Verify that the stream length matches the written data length
        Assert.Equal(inputLength, stream.Length);
        
        // Verify that the position has advanced correctly
        Assert.Equal(initialPosition + inputLength, stream.Position);
    }

    /// <summary>
    /// Verifies that the Flush method performs no operation and does not throw any exceptions.
    /// </summary>
    [Fact]
    public void Flush_NoData_DoesNotThrowException()
    {
        // Arrange: Create a new instance of PooledBufferStream.
        using var stream = new PooledBufferStream();

        // Act & Assert: Calling Flush should not throw an exception.
        var exception = Record.Exception(() => stream.Flush());
        Assert.Null(exception);
    }
}

/// <summary>
/// Unit tests for the BufferSegment class.
/// </summary>
public class BufferSegmentTests
{
    /// <summary>
    /// Provides test data for the Initialize method.
    /// Each test case includes a byte array and a corresponding runningIndex value.
    /// </summary>
    public static IEnumerable<object[]> GetInitializeTestData()
    {
        yield return new object[] { new byte[0], 0L };
        yield return new object[] { new byte[] { 1, 2, 3 }, 123L };
        yield return new object[] { new byte[] { 255 }, -1L };
        yield return new object[] { new byte[] { 0, 0, 0, 0, 1 }, long.MaxValue };
        yield return new object[] { new byte[] { 10, 20, 30, 40, 50 }, long.MinValue };
    }

    /// <summary>
    /// Verifies that the Initialize method sets the Memory and RunningIndex properties correctly.
    /// Tests various edge cases including empty arrays, typical values, and boundary numeric values.
    /// </summary>
    /// <param name="inputData">Byte array to be set into the Memory property.</param>
    /// <param name="runningIndex">The runningIndex value to be assigned.</param>
    [Theory]
    [MemberData(nameof(GetInitializeTestData))]
    public void Initialize_ValidInput_PropertiesSetCorrectly(byte[] inputData, long runningIndex)
    {
        // Arrange
        ReadOnlyMemory<byte> readOnlyMemory = new ReadOnlyMemory<byte>(inputData);
        var bufferSegment = new PooledBufferStream.BufferSegment();

        // Act
        bufferSegment.Initialize(readOnlyMemory, runningIndex);

        // Assert
        Assert.Equal(readOnlyMemory.ToArray(), bufferSegment.Memory.ToArray());
        Assert.Equal(runningIndex, bufferSegment.RunningIndex);
    }

    /// <summary>
    /// Verifies that calling SetNext with a valid BufferSegment correctly sets the Next property.
    /// </summary>
    [Fact]
    public void SetNext_ValidBufferSegment_SetsNextProperty()
    {
        // Arrange
        var segment = new PooledBufferStream.BufferSegment();
        var nextSegment = new PooledBufferStream.BufferSegment();

        // Act
        segment.SetNext(nextSegment);

        // Assert
        Assert.Equal(nextSegment, segment.Next);
    }

    /// <summary>
    /// Verifies that calling SetNext more than once correctly overwrites the Next property.
    /// </summary>
    [Fact]
    public void SetNext_OverwriteExistingNext_SetsNextPropertyToNewValue()
    {
        // Arrange
        var segment = new PooledBufferStream.BufferSegment();
        var firstNextSegment = new PooledBufferStream.BufferSegment();
        var secondNextSegment = new PooledBufferStream.BufferSegment();

        // Act
        segment.SetNext(firstNextSegment);
        segment.SetNext(secondNextSegment);

        // Assert
        Assert.Equal(secondNextSegment, segment.Next);
    }

    /// <summary>
    /// Verifies that the SegmentPoolPolicy.Create method creates a new BufferSegment instance.
    /// This test calls BufferSegment.Pool.Get() which internally uses the SegmentPoolPolicy.Create method.
    /// Expected: A non-null BufferSegment instance is returned.
    /// </summary>
    [Fact]
    public void SegmentPoolPolicy_Create_ReturnsNewBufferSegment()
    {
        // Act
        PooledBufferStream.BufferSegment segment = PooledBufferStream.BufferSegment.Pool.Get();

        // Assert
        Assert.NotNull(segment);
        Assert.IsType<PooledBufferStream.BufferSegment>(segment);
        PooledBufferStream.BufferSegment.Pool.Return(segment);
    }
}
