using UnitTests.GrainInterfaces;

namespace UnitTests.Grains
{
    public class PolymorphicTestGrain : Grain, IPolymorphicTestGrain
    {
        public Task<string> F1Method()
        {
            System.Diagnostics.Trace.WriteLine("Calling F1Method");
            return Task.FromResult("F1");
        }
        public Task<string> F2Method()
        {
            System.Diagnostics.Trace.WriteLine("Calling F2Method");
            return Task.FromResult("F2");
        }
        public Task<string> F3Method()
        {
            System.Diagnostics.Trace.WriteLine("Calling F3Method");
            return Task.FromResult("F3");
        }
        public Task<string> E1Method()
        {
            System.Diagnostics.Trace.WriteLine("Calling E1Method");
            return Task.FromResult("E1");
        }
        public Task<string> E2Method()
        {
            System.Diagnostics.Trace.WriteLine("Calling E2Method");
            return Task.FromResult("E2");
        }
        public Task<string> E3Method()
        {
            System.Diagnostics.Trace.WriteLine("Calling E3Method");
            return Task.FromResult("E3");
        }
        public Task<string> D1Method()
        {
            System.Diagnostics.Trace.WriteLine("Calling D1Method");
            return Task.FromResult("D1");
        }
        public Task<string> D2Method()
        {
            System.Diagnostics.Trace.WriteLine("Calling D2Method");
            return Task.FromResult("D2");
        }
        public Task<string> D3Method()
        {
            System.Diagnostics.Trace.WriteLine("Calling D3Method");
            return Task.FromResult("D3");
        }
        public Task<string> C1Method()
        {
            System.Diagnostics.Trace.WriteLine("Calling C1Method");
            return Task.FromResult("C1");
        }
        public Task<string> C2Method()
        {
            System.Diagnostics.Trace.WriteLine("Calling C2Method");
            return Task.FromResult("C2");
        }
        public Task<string> C3Method()
        {
            System.Diagnostics.Trace.WriteLine("Calling C3Method");
            return Task.FromResult("C3");
        }
        public Task<string> B1Method()
        {
            System.Diagnostics.Trace.WriteLine("Calling B1Method");
            return Task.FromResult("B1");
        }
        public Task<string> B2Method()
        {
            System.Diagnostics.Trace.WriteLine("Calling B2Method");
            return Task.FromResult("B2");
        }
        public Task<string> B3Method()
        {
            System.Diagnostics.Trace.WriteLine("Calling B3Method");
            return Task.FromResult("B3");
        }
        public Task<string> A1Method()
        {
            System.Diagnostics.Trace.WriteLine("Calling A1Method");
            return Task.FromResult("A1");
        }
        public Task<string> A2Method()
        {
            System.Diagnostics.Trace.WriteLine("Calling A2Method");
            return Task.FromResult("A2");
        }
        public Task<string> A3Method()
        {
            System.Diagnostics.Trace.WriteLine("Calling A3Method");
            return Task.FromResult("A3");
        }

    }
}
