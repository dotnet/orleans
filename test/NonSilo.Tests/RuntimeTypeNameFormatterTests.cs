using System;
using System.Collections.Generic;
using System.Collections.Specialized;
using Orleans.Runtime;
using Orleans.Serialization.TypeSystem;
using Orleans.Utilities;
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
        private readonly ITestOutputHelper output;
        private readonly Type[] types = new[]
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
            this.output = output;
        }

        /// <summary>
        /// Tests that various strings formatted with <see cref="RuntimeTypeNameFormatter"/> can be loaded using <see cref="Type.GetType(string)"/>.
        /// </summary>
        [Fact]
        public void FormattedTypeNamesAreRecoverable()
        {
            foreach (var type in types)
            {
                var formatted = RuntimeTypeNameFormatter.Format(type);
                this.output.WriteLine($"Full Name: {type.FullName}");
                this.output.WriteLine($"Formatted: {formatted}");
                var isRecoverable = new CachedTypeResolver().TryResolveType(formatted, out var resolved) && resolved == type;
                Assert.True(isRecoverable, $"Type.GetType(\"{formatted}\") must be equal to the original type.");
            }
        }

        /// <summary>
        /// Tests that various strings parsed by <see cref="RuntimeTypeNameParser"/> are reformatted identically to their input, when the input was produced by <see cref="RuntimeTypeNameFormatter"/>.
        /// </summary>
        [Fact]
        public void ParsedTypeNamesAreIdenticalToFormattedNames()
        {
            foreach (var type in types)
            {
                var formatted = RuntimeTypeNameFormatter.Format(type);
                var parsed = RuntimeTypeNameParser.Parse(formatted);
                this.output.WriteLine($"Type.FullName: {type.FullName}");
                this.output.WriteLine($"Formatted    : {formatted}");
                this.output.WriteLine($"Parsed       : {parsed}");
                Assert.Equal(formatted, parsed.Format());

                var reparsed = RuntimeTypeNameParser.Parse(parsed.Format());
                this.output.WriteLine($"Reparsed     : {reparsed}");
                Assert.Equal(formatted, reparsed.Format());
            }
        } 
        
        public class Inner<T>
        {
            public class InnerInner<U, V>
            {
                public class Bottom { }
            }
        }
    }
}
