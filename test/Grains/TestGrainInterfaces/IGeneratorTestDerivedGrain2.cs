namespace UnitTests.GrainInterfaces
{
    public interface IGeneratorTestDerivedGrain2 : IGeneratorTestGrain
    {
        Task<string> StringConcat(string str1, string str2, string str3);
    }
}