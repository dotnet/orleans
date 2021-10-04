using System;
using System.Threading.Tasks;
using Orleans;
using Orleans.CodeGeneration;
using Xunit;

namespace CodeGenerator.Tests
{
    public interface IAnimal
    {
    }

    public class Cat : IAnimal
    {
    }

    public interface ICar
    {
    }

    public class Toyota : ICar
    {
    }

    public interface IDummyGrain : IGrainWithGuidKey
    {
        Task<int> Method<T>(int x);
        Task<int> Method<T>(int x, string y);
        Task<int> Method<T>(int x, string y, T z);
        Task<int> Method<T>(T x, string y, string z);
        Task<int> Method<T>(object[] x);
        Task<int> Method<T1, T2>(T1 x, T2 y);
        ValueTask Method<T>(string s1, string s2);
        ValueTask<int> Method<T>(string s);
        ValueTask<object> Method<T>(int x, int y);
        Task<int> ConstrainedMethod<T>(int a) where T : IAnimal;
        Task<int> ConstrainedMethod<T>(int a, string b) where T : ICar;
    }

    public class FooGrain : IDummyGrain
    {
        public Task<int> Method<T>(int x) => Task.FromResult(1);

        public Task<int> Method<T>(int x, string y) => Task.FromResult(2);

        public Task<int> Method<T>(int x, string y, T z) => Task.FromResult(3);

        public Task<int> Method<T>(T x, string y, string z) => Task.FromResult(4);

        public Task<int> Method<T>(object[] x) => Task.FromResult(5);

        public Task<int> Method<T1, T2>(T1 x, T2 y) => Task.FromResult(6);

        public ValueTask Method<T>(string s1, string s2) => new();

        public ValueTask<object> Method<T>(int x, int y) => new(7);

        public ValueTask<int> Method<T>(string s) => new(8);

        public Task<int> ConstrainedMethod<T>(int a) where T : IAnimal => Task.FromResult(1);

        public Task<int> ConstrainedMethod<T>(int a, string b) where T : ICar => Task.FromResult(2);
    }

    public class GenericMethodInvokerTests
    {
        [Fact]
        public async Task Constraint_Compliant_Animal_Int_Argument()
        {
            var invoker = new GenericMethodInvoker(typeof(IDummyGrain), nameof(IDummyGrain.ConstrainedMethod), 1);
            var mock = new FooGrain();

            // Act<Cat>(42)
            var result = await invoker.Invoke(mock, new object[] { typeof(Cat), typeof(int), 42 });

            Assert.Equal(1, result);
        }

        [Fact]
        public async Task Constraint_Compliant_Car_Int_String_Arguments()
        {

            var invoker = new GenericMethodInvoker(typeof(IDummyGrain), nameof(IDummyGrain.ConstrainedMethod), 1);
            var mock = new FooGrain();

            // Act<Toyota>(42, "b")
            var result = await invoker.Invoke(mock,
                new object[] { typeof(Toyota), typeof(int), typeof(string), 42, "b" });

            Assert.Equal(2, result);
        }

        [Fact]
        public async Task Constraint_Animal_Wrong_Type_Parameter_Type()
        {

            var invoker = new GenericMethodInvoker(typeof(IDummyGrain), nameof(IDummyGrain.ConstrainedMethod), 1);
            var mock = new FooGrain();

            // Act<Toyota>("c") -- violates IAnimal constraint
            await Assert.ThrowsAsync<ArgumentException>(async () => await invoker.Invoke(mock,
                new object[] { typeof(Toyota), typeof(string), "c" }));
        }

        [Fact]
        public async Task Constraint_Animal_Wrong_Argument_Type()
        {

            var invoker = new GenericMethodInvoker(typeof(IDummyGrain), nameof(IDummyGrain.ConstrainedMethod), 1);
            var mock = new FooGrain();

            // Act<Cat>("c") -- string overload does not exist for IAnimal constrained
            await Assert.ThrowsAsync<ArgumentException>(async () =>
                await invoker.Invoke(mock,
                    new object[] { typeof(Cat), typeof(string), "c" }));
        }

        [Fact]
        public async Task Overload_Int()
        {
            var invoker = new GenericMethodInvoker(typeof(IDummyGrain), nameof(IDummyGrain.Method), 1);

            var mock = new FooGrain();

            var result = await invoker.Invoke(mock, new object[]
            {
                typeof(bool), // type parameter(s)
                typeof(int), // argument type(s)
                42 // argument(s)
            });

            Assert.Equal(1, result);
        }

        [Fact]
        public async Task Overload_Int_String()
        {
            var invoker = new GenericMethodInvoker(typeof(IDummyGrain), nameof(IDummyGrain.Method), 1);
            var mock = new FooGrain();

            var result = await invoker.Invoke(mock,
                new object[] { typeof(bool), typeof(int), typeof(string), 42, "bar" });

            Assert.Equal(2, result);
        }

        [Fact]
        public async Task Overload_Int_String_T()
        {
            var invoker = new GenericMethodInvoker(typeof(IDummyGrain), nameof(IDummyGrain.Method), 1);
            var mock = new FooGrain();

            var result = await invoker.Invoke(mock,
                new object[] { typeof(bool), typeof(int), typeof(string), typeof(bool), 42, "bar", true });

            Assert.Equal(3, result);
        }

        [Fact]
        public async Task Overload_T_string_string()
        {
            var invoker = new GenericMethodInvoker(typeof(IDummyGrain), nameof(IDummyGrain.Method), 1);
            var mock = new FooGrain();

            var result = await invoker.Invoke(mock,
                new object[] { typeof(bool), typeof(bool), typeof(string), typeof(string), false, "bar", "foo" });

            Assert.Equal(4, result);
        }

        [Fact]
        public async Task Overload_object_array_covariant_arg()
        {
            var invoker = new GenericMethodInvoker(typeof(IDummyGrain), nameof(IDummyGrain.Method), 1);
            var mock = new FooGrain();

            var result = await invoker.Invoke(mock,
                new object[] { typeof(bool), typeof(string[]), new[] { "a", "b", "c" } });

            Assert.Equal(5, result);
        }

        [Fact]
        public async Task Overload_multiple_type_parameters()
        {
            var invoker = new GenericMethodInvoker(typeof(IDummyGrain), nameof(IDummyGrain.Method), 2);
            var mock = new FooGrain();

            var result = await invoker.Invoke(mock,
                new object[]
                {
                    typeof(string[]), typeof(double), typeof(string[]), typeof(double), new[] { "a", "b", "c" },
                    0.5d
                });

            Assert.Equal(6, result);
        }

        [Fact]
        public async Task Overload_invoke_with_null()
        {
            var invoker = new GenericMethodInvoker(typeof(IDummyGrain), nameof(IDummyGrain.Method), 2);
            var mock = new FooGrain();

            var result = await invoker.Invoke(mock,
                new object[] { typeof(string[]), typeof(double), typeof(string[]), typeof(double), null, 0.5d });

            Assert.Equal(6, result);
        }

        [Fact]
        public async Task Overload_invoke_ValueTask_return()
        {
            var invoker = new GenericMethodInvoker(typeof(IDummyGrain), nameof(IDummyGrain.Method), 1);
            var mock = new FooGrain();

            _ = await invoker.Invoke(mock, new object[] { typeof(bool), typeof(string), typeof(string), "foo", "bar" });

            Assert.True(true);
        }

        [Fact]
        public async Task Overload_invoke_ValueTaskObject_return()
        {
            var invoker = new GenericMethodInvoker(typeof(IDummyGrain), nameof(IDummyGrain.Method), 1);
            var mock = new FooGrain();

            var result = await invoker.Invoke(mock, new object[] { typeof(bool), typeof(int), typeof(int), 42, 43 });

            Assert.Equal(7, result);
        }

        [Fact]
        public async Task Overload_invoke_ValueTaskInt_return()
        {
            var invoker = new GenericMethodInvoker(typeof(IDummyGrain), nameof(IDummyGrain.Method), 1);
            var mock = new FooGrain();

            var result = await invoker.Invoke(mock, new object[] { typeof(bool), typeof(string), "foo" });

            Assert.Equal(8, result);
        }
    }
}

