using UnitTests.GrainInterfaces;

namespace UnitTests.Grains
{
    public class ServiceType : Grain, IServiceType
    {
        public Task<string> A1Method()
        {
            return Task.FromResult("A1");
        }
        public Task<string> A2Method()
        {
            return Task.FromResult("A2");
        }
        public Task<string> A3Method()
        {
            return Task.FromResult("A3");
        }

        public Task<string> B1Method()
        {
            return Task.FromResult("B1");
        }
        public Task<string> B2Method()
        {
            return Task.FromResult("B2");
        }
        public Task<string> B3Method()
        {
            return Task.FromResult("B3");
        }

        public Task<string> C1Method()
        {
            return Task.FromResult("C1");
        }
        public Task<string> C2Method()
        {
            return Task.FromResult("C2");
        }
        public Task<string> C3Method()
        {
            return Task.FromResult("C3");
        }

        public Task<string> D1Method()
        {
            return Task.FromResult("D1");
        }
        public Task<string> D2Method()
        {
            return Task.FromResult("D2");
        }
        public Task<string> D3Method()
        {
            return Task.FromResult("D3");
        }

        public Task<string> E1Method()
        {
            return Task.FromResult("E1");
        }
        public Task<string> E2Method()
        {
            return Task.FromResult("E2");
        }
        public Task<string> E3Method()
        {
            return Task.FromResult("E3");
        }

        public Task<string> F1Method()
        {
            return Task.FromResult("F1");
        }
        public Task<string> F2Method()
        {
            return Task.FromResult("F2");
        }
        public Task<string> F3Method()
        {
            return Task.FromResult("F3");
        }

        public Task<string> ServiceTypeMethod1()
        {
            return Task.FromResult("ServiceTypeMethod1");
        }

        public Task<string> ServiceTypeMethod2()
        {
            return Task.FromResult("ServiceTypeMethod2");
        }

        public Task<string> ServiceTypeMethod3()
        {
            return Task.FromResult("ServiceTypeMethod3");
        }
    }
}
