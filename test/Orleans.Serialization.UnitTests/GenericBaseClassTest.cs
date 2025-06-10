using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Orleans.Serialization.UnitTests
{
    [GenerateSerializer]
    abstract class Base<T>;

    class Derived : Base<int>;

    /// <summary>
    /// Tests for serialization behavior with generic base classes.
    /// 
    /// Orleans requires that serializable types are explicitly marked with [GenerateSerializer].
    /// When a base class is generic and marked for serialization, derived classes that close
    /// the generic type parameters do not automatically inherit serialization capability
    /// unless they are also marked with [GenerateSerializer].
    /// 
    /// This test verifies that the serializer correctly identifies which types can be serialized
    /// based on explicit attribute marking rather than inheritance.
    /// </summary>
    [Trait("Category", "BVT")]
    public class GenericBaseClassTest
    {
        private readonly ServiceProvider _services;
        private readonly Serializer _serializer;
        public GenericBaseClassTest()
        {
            _services = new ServiceCollection()
                .AddSerializer()
                .BuildServiceProvider();
            _serializer = _services.GetRequiredService<Serializer>();
        }

        [Fact]
        public void DerivedNoGeneric()
        {
            Assert.False(_serializer.CanSerialize(typeof(Derived)));
        }
    }
}
