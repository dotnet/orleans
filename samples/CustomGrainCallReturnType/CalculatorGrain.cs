using Orleans;

namespace CustomGrainCallReturnType;

public interface ICalculatorGrain : IGrainWithStringKey
{
    GrainCall<int> Add(int left, int right);

    GrainCall<int> Fail(string message);
}

public sealed class CalculatorGrain : Grain, ICalculatorGrain
{
    public GrainCall<int> Add(int left, int right) =>
        GrainCall<int>.FromResult(left + right);

    public GrainCall<int> Fail(string message) =>
        throw new InvalidOperationException(message);
}
