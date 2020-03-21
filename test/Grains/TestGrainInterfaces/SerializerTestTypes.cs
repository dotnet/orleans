using System;
using System.Runtime.Serialization;
using Orleans.CodeGeneration;
using Orleans.Serialization;

namespace UnitTests.GrainInterfaces
{
    /// <summary>
    /// A type with an <see cref="IOnDeserialized"/> hook, to test that it is correctly called by the internal serializers.
    /// </summary>
    [Serializable]
    public class TypeWithOnDeserializedHook : IOnDeserialized
    {
        [NonSerialized]
        public ISerializerContext Context;

        public int Int { get; set; }

        void IOnDeserialized.OnDeserialized(ISerializerContext context)
        {
            this.Context = context;
        }
    }

    [Serializable]
    public class BaseClassWithAutoProp
    {
        public int AutoProp { get; set; }
    }

    /// <summary>
    /// Code generation test to ensure that an overridden autoprop with a type which differs from
    /// the base autoprop is not used during serializer generation
    /// </summary>
    [Serializable]
    public class SubClassOverridingAutoProp : BaseClassWithAutoProp
    {
        public new string AutoProp { get => base.AutoProp.ToString(); set => base.AutoProp = int.Parse(value); }
    }

    [KnownBaseType]
    public abstract class WellKnownBaseClass { }

    public class DescendantOfWellKnownBaseClass : WellKnownBaseClass
    {
        public int FavouriteNumber { get; set; }
    }

    [KnownBaseType]
    public interface IWellKnownBase { }

    public class ImplementsWellKnownInterface : IWellKnownBase
    {
        public int FavouriteNumber { get; set; }
    }

    public class NotDescendantOfWellKnownBaseType
    {
        public int FavouriteNumber { get; set; }
    }
}
