using UnitTests.GrainInterfaces;

namespace UnitTests.Grains
{
    public class PolymorphicTestGrain : Grain, IPolymorphicTestGrain
    {
        public Task<string> F1Method() => Task.FromResult("F1");
        public Task<string> F2Method() => Task.FromResult("F2");
        public Task<string> F3Method() => Task.FromResult("F3");
        public Task<string> E1Method() => Task.FromResult("E1");
        public Task<string> E2Method() => Task.FromResult("E2");
        public Task<string> E3Method() => Task.FromResult("E3");
        public Task<string> D1Method() => Task.FromResult("D1");
        public Task<string> D2Method() => Task.FromResult("D2");
        public Task<string> D3Method() => Task.FromResult("D3");
        public Task<string> C1Method() => Task.FromResult("C1");
        public Task<string> C2Method() => Task.FromResult("C2");
        public Task<string> C3Method() => Task.FromResult("C3");
        public Task<string> B1Method() => Task.FromResult("B1");
        public Task<string> B2Method() => Task.FromResult("B2");
        public Task<string> B3Method() => Task.FromResult("B3");
        public Task<string> A1Method() => Task.FromResult("A1");
        public Task<string> A2Method() => Task.FromResult("A2");
        public Task<string> A3Method() => Task.FromResult("A3");
        Task<string> IC.CommonMethod() => Task.FromResult("IC");
        Task<string> IA.CommonMethod() => Task.FromResult("IA");
        Task<string> IB.CommonMethod() => Task.FromResult("IB");
    }
}
