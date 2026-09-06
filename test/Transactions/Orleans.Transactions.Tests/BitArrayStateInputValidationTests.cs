using Orleans.Transactions.TestKit.Correctnesss;
using TestExtensions;
using Xunit;

namespace Orleans.Transactions.Tests;

[TestSuite("BVT")]
[TestProvider("None")]
[TestArea("Transactions")]
[TestCategory("BVT"), TestCategory("Transactions")]
public class BitArrayStateInputValidationTests
{
    [Fact]
    public void Equals_TypedNull_ThrowsArgumentNullExceptionWithOtherParamName()
    {
        var state = new TestableBitArrayState();

        var exception = Assert.Throws<ArgumentNullException>(() => state.TypedEquals(null!));

        Assert.Equal("other", exception.ParamName);
        Assert.False(state.Equals((object?)null));
    }

    [Fact]
    public void CopyConstructor_NullOther_ThrowsArgumentNullExceptionWithOtherParamName()
    {
        var exception = Assert.Throws<ArgumentNullException>(() => new BitArrayState(null!));

        Assert.Equal("other", exception.ParamName);
    }

    [Fact]
    public void Apply_NullLeft_ThrowsWithLeftParamNameBeforeOtherValidationOrOperation()
    {
        var operationCallCount = 0;
        var right = new BitArrayState();
        right.Set(3, true);
        right.Set(36, true);
        var originalRight = right.Value.ToArray();

        int Operation(int left, int rightValue)
        {
            operationCallCount++;
            return left | rightValue;
        }

        var exception = Assert.Throws<ArgumentNullException>(
            () => BitArrayState.Apply(null!, right, Operation));
        var allNullException = Assert.Throws<ArgumentNullException>(
            () => BitArrayState.Apply(null!, null!, null!));

        Assert.Equal("left", exception.ParamName);
        Assert.Equal("left", allNullException.ParamName);
        Assert.Equal(0, operationCallCount);
        Assert.Equal(originalRight, right.Value);
    }

    [Fact]
    public void Apply_NullRight_ThrowsWithRightParamNameBeforeOperation()
    {
        var operationCallCount = 0;
        var left = new BitArrayState();
        left.Set(7, true);
        left.Set(39, true);
        var originalLeft = left.Value.ToArray();

        int Operation(int leftValue, int right)
        {
            operationCallCount++;
            return leftValue | right;
        }

        var exception = Assert.Throws<ArgumentNullException>(
            () => BitArrayState.Apply(left, null!, Operation));
        var nullOperationException = Assert.Throws<ArgumentNullException>(
            () => BitArrayState.Apply(left, null!, null!));

        Assert.Equal("right", exception.ParamName);
        Assert.Equal("right", nullOperationException.ParamName);
        Assert.Equal(0, operationCallCount);
        Assert.Equal(originalLeft, left.Value);
    }

    [Fact]
    public void Apply_NullOperation_ThrowsWithOpParamNameWithoutMutatingOperands()
    {
        var left = new BitArrayState();
        left.Set(1, true);
        left.Set(34, true);
        var right = new BitArrayState();
        right.Set(2, true);
        right.Set(35, true);
        var originalLeft = left.Value.ToArray();
        var originalRight = right.Value.ToArray();

        var exception = Assert.Throws<ArgumentNullException>(
            () => BitArrayState.Apply(left, right, null!));

        Assert.Equal("op", exception.ParamName);
        Assert.Equal(originalLeft, left.Value);
        Assert.Equal(originalRight, right.Value);
    }

    [Fact]
    public void SetAndClear_UpdatesOnlyTheRequestedBit()
    {
        var state = new BitArrayState();

        Assert.Equal(new[] { 0 }, state.Value);

        state.Set(4, true);
        Assert.Equal(new[] { 1 << 4 }, state.Value);

        state.Set(5, true);
        Assert.Equal(new[] { (1 << 4) | (1 << 5) }, state.Value);

        state.Set(5, false);
        Assert.Equal(new[] { 1 << 4 }, state.Value);
    }

    [Fact]
    public void Set_BitBeyondThirtyTwo_GrowsAndPreservesExistingBits()
    {
        var state = new BitArrayState();

        state.Set(31, true);
        state.Set(32, true);

        Assert.Equal(2, state.Length);
        Assert.Equal(new[] { int.MinValue, 1 }, state.Value);
    }

    [Fact]
    public void CopyConstructor_CreatesIndependentEquivalentCopy()
    {
        var source = new TestableBitArrayState();
        source.Set(2, true);
        source.Set(35, true);
        var originalSource = source.Value.ToArray();

        var copy = new TestableBitArrayState(source);

        Assert.True(source.TypedEquals(copy));
        Assert.True(source.Equals((object)copy));
        Assert.NotSame(source.Value, copy.Value);

        copy.Set(2, false);

        Assert.False(source.TypedEquals(copy));
        Assert.False(source.Equals((object)copy));
        Assert.Equal(originalSource, source.Value);

        copy.Set(2, true);

        Assert.True(source.TypedEquals(copy));
        Assert.True(source.Equals((object)copy));

        copy.Set(64, true);

        Assert.Equal(originalSource, source.Value);
        Assert.Equal(new[] { 1 << 2, 1 << 3, 1 }, copy.Value);
        Assert.False(source.TypedEquals(copy));
        Assert.False(source.Equals((object)copy));
    }

    [Theory]
    [InlineData("^", false, 0b0110, 0b0101)]
    [InlineData("|", false, 0b1110, 0b0101)]
    [InlineData("&", false, 0b1000, 0)]
    [InlineData("^", true, 0b0110, 0b0101)]
    [InlineData("|", true, 0b1110, 0b0101)]
    [InlineData("&", true, 0b1000, 0)]
    public void Apply_UnequalLengthsInEitherOperand_UsesMissingWordsAsZero(
        string operation,
        bool longerOnLeft,
        int expectedOverlappingWord,
        int expectedTrailingWord)
    {
        var shorter = new BitArrayState();
        shorter[0] = longerOnLeft ? 0b1100 : 0b1010;
        var longer = new BitArrayState();
        longer[0] = longerOnLeft ? 0b1010 : 0b1100;
        longer.Set(32, true);
        longer.Set(34, true);
        var left = longerOnLeft ? longer : shorter;
        var right = longerOnLeft ? shorter : longer;
        var originalLeft = left.Value.ToArray();
        var originalRight = right.Value.ToArray();

        var result = operation switch
        {
            "^" => left ^ right,
            "|" => left | right,
            "&" => left & right,
            _ => throw new ArgumentOutOfRangeException(nameof(operation)),
        };

        Assert.Equal(2, result.Length);
        Assert.Equal(new[] { expectedOverlappingWord, expectedTrailingWord }, result.Value);
        Assert.Equal(originalLeft, left.Value);
        Assert.Equal(originalRight, right.Value);
        Assert.NotSame(left.Value, result.Value);
        Assert.NotSame(right.Value, result.Value);
    }

    private sealed class TestableBitArrayState : BitArrayState
    {
        public TestableBitArrayState()
        {
        }

        public TestableBitArrayState(BitArrayState other)
            : base(other)
        {
        }

        public bool TypedEquals(BitArrayState other) => base.Equals(other);
    }
}
