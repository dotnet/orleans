using System.Collections.Specialized;
using Orleans.Serialization.TypeSystem;
using Xunit;
using Xunit.Abstractions;

namespace NonSilo.Tests
{
    /// <summary>
    /// Tests for <see cref="RuntimeTypeNameFormatter"/>.
    /// </summary>
    [TestCategory("BVT")]
    public class RuntimeTypeNameFormatterTests
    {
        public interface IMyBaseType<T> { }
        public interface IMyArrayType<T> : IMyBaseType <T[]> { }
        private readonly ITestOutputHelper _output;
        private readonly List<Type> _types = new()
            {
                typeof(NameValueCollection),
                typeof(int),
                typeof(int[]),
                typeof(int*[]),
                typeof(int[]),
                typeof(List<>),
                typeof(List<int>),
                typeof(List<int*[]>),
                typeof(Inner<int[,,]>.InnerInner<string, List<int>>.Bottom[,]),
                typeof(Inner<>.InnerInner<,>.Bottom),
                typeof(RuntimeTypeNameFormatterTests),
                typeof(TestGrainInterfaces.CircularStateTestState),
                typeof(int).MakeByRefType(),
                typeof(Inner<int[]>.InnerInner<string, List<int>>.Bottom[,])
                    .MakePointerType()
                    .MakePointerType()
                    .MakeArrayType(10)
                    .MakeByRefType(),
                typeof(NameValueCollection)
            };

        public RuntimeTypeNameFormatterTests(ITestOutputHelper output)
        {
            _output = output;
            _types.Add(typeof(List<>).MakeGenericType(typeof(Inner<int>.Middle).MakeArrayType()));
            _types.Add(typeof(List<>).MakeGenericType(typeof(Inner<>.Middle).MakeArrayType()));
            typeof(IMyArrayType<>).MakeGenericType(typeof(Inner<>.Middle)).GetInterfaces().ToList().ForEach(_types.Add);
            typeof(IMyArrayType<>).GetInterfaces().ToList().ForEach(_types.Add);
        }

        /// <summary>
        /// Tests that various strings formatted with <see cref="RuntimeTypeNameFormatter"/> can be loaded using <see cref="Type.GetType(string)"/>.
        /// </summary>
        [Fact]
        public void FormattedTypeNamesAreRecoverable()
        {
            var resolver = new CachedTypeResolver();
            foreach (var type in _types)
            {
                if (string.IsNullOrWhiteSpace(type.FullName)) continue;
                var formatted = RuntimeTypeNameFormatter.Format(type);
                _output.WriteLine($"Full Name: {type.FullName}");
                _output.WriteLine($"Formatted: {formatted}");
                var isRecoverable = resolver.TryResolveType(formatted, out var resolved) && resolved == type;
                var resolvedFormatted = resolved is not null ? RuntimeTypeNameFormatter.Format(resolved) : "null";
                Assert.True(isRecoverable, $"Type.GetType(\"{formatted}\") must be equal to the original type. Got: {resolvedFormatted}");
            }
        }

        /// <summary>
        /// Tests that various strings parsed by <see cref="RuntimeTypeNameParser"/> are reformatted identically to their input, when the input was produced by <see cref="RuntimeTypeNameFormatter"/>.
        /// </summary>
        [Fact]
        public void ParsedTypeNamesAreIdenticalToFormattedNames()

        {
            foreach (var type in _types)
            {
                var formatted = RuntimeTypeNameFormatter.Format(type);
                var parsed = RuntimeTypeNameParser.Parse(formatted);
                _output.WriteLine($"Type.FullName: {type.FullName}");
                _output.WriteLine($"Formatted    : {formatted}");
                _output.WriteLine($"Parsed       : {parsed}");
                Assert.Equal(formatted, parsed.Format());

                var reparsed = RuntimeTypeNameParser.Parse(parsed.Format());
                _output.WriteLine($"Reparsed     : {reparsed}");
                Assert.Equal(formatted, reparsed.Format());
            }
        } 
        
        public class Inner<T>
        {
            public class Middle { }
            public class InnerInner<U, V>
            {
                public class Bottom { }
            }
        }
    }
}
