using System.Threading.Tasks;

namespace UnitTests.Grains
{
    public class CodeGenTestPoco
    {
        public int SomeProperty { get; set; }
    }

    // This class forms a code generation test case.
    // If the code generator does generate any code for the async state machine in the Product
    // method, it must generate valid C# syntax. See: https://github.com/dotnet/orleans/pull/3639
    public class FeaturePopulatorCodeGenTestClass : CodeGenTestPoco, FeaturePopulatorCodeGenTestClass.IFactory<int, double>
    {
        public interface IFactory<TInput, TOutput>
        {
            Task<TOutput> Product(TInput input);
        }

        async Task<double> IFactory<int, double>.Product(int input)
        {
            await Task.Delay(100);
            return input;
        }
    }
}
