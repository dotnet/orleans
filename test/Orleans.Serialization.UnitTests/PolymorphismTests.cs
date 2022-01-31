using Orleans.Serialization.Configuration;
using Orleans.Serialization.Session;
using Orleans.Serialization.Utilities;
using Microsoft.Extensions.DependencyInjection;
using System;
using Xunit;
using Microsoft.Extensions.Options;

namespace Orleans.Serialization.UnitTests
{
    public class PolymorphismTests
    {
        private readonly ServiceProvider _serviceProvider;

        public PolymorphismTests()
        {
            _serviceProvider = new ServiceCollection().AddSerializer()
                .AddSingleton<IConfigureOptions<TypeManifestOptions>, TypeConfigurationProvider>()
                .BuildServiceProvider();
        }

        private class TypeConfigurationProvider : IConfigureOptions<TypeManifestOptions>
        {
            public void Configure(TypeManifestOptions configuration)
            {
                configuration.WellKnownTypeIds[1000] = typeof(SomeBaseClass);
                configuration.WellKnownTypeIds[1001] = typeof(SomeSubClass);
                configuration.WellKnownTypeIds[1002] = typeof(OtherSubClass);
                configuration.WellKnownTypeIds[1003] = typeof(SomeSubClassChild);
            }
        }

        [Fact]
        public void ExceptionsAreSerializable()
        {
            InvalidOperationException exception;
            AggregateException aggregateException;
            try
            {
                throw new InvalidOperationException("This is exceptional!");
            }
            catch (InvalidOperationException ex)
            {
                exception = ex;
                exception.Data.Add("Hi", "yes?");
                try
                {
                    throw new AggregateException("This is insane!", ex);
                }
                catch (AggregateException ag)
                {
                    aggregateException = ag;
                }
            }

            var result = RoundTripToExpectedType<Exception, InvalidOperationException>(exception);
            Assert.Equal(exception.Message, result.Message);
            Assert.Contains(exception.StackTrace, result.StackTrace);
            Assert.Equal(exception.InnerException, result.InnerException);
            Assert.NotNull(result.Data);
            var data = result.Data;
            Assert.True(data.Count == 1);
            Assert.Equal("yes?", data["Hi"]);

            var agResult = RoundTripToExpectedType<Exception, AggregateException>(aggregateException);
            Assert.Equal(aggregateException.Message, agResult.Message);
            Assert.Contains(aggregateException.StackTrace, agResult.StackTrace);
            var inner = Assert.IsType<InvalidOperationException>(agResult.InnerException);
            Assert.Equal(exception.Message, inner.Message);
        }

        [Fact]
        public void GeneratedSerializersRoundTripThroughSerializer_Polymorphic()
        {
            var original = new SomeSubClass
            { SbcString = "Shaggy", SbcInteger = 13, SscString = "Zoinks!", SscInteger = -1 };

            var getSubClassSerializerResult = RoundTripToExpectedType<SomeSubClass, SomeSubClass>(original);
            Assert.Equal(original.SscString, getSubClassSerializerResult.SscString);
            Assert.Equal(original.SscInteger, getSubClassSerializerResult.SscInteger);

            var getBaseClassSerializerResult = RoundTripToExpectedType<SomeBaseClass, SomeSubClass>(original);
            Assert.Equal(original.SscString, getBaseClassSerializerResult.SscString);
            Assert.Equal(original.SscInteger, getBaseClassSerializerResult.SscInteger);
        }

        [Fact]
        public void GeneratedSerializersRoundTripThroughSerializer_PolymorphicMultiHierarchy()
        {
            var someSubClass = new SomeSubClass
            { SbcString = "Shaggy", SbcInteger = 13, SscString = "Zoinks!", SscInteger = -1 };

            var otherSubClass = new OtherSubClass
            { SbcString = "sbcs", SbcInteger = 2000, OtherSubClassString = "oscs", OtherSubClassInt = 1000 };

            var someSubClassChild = new SomeSubClassChild
            { SbcString = "a", SbcInteger = 0, SscString = "Zoinks!", SscInteger = -1, SomeSubClassChildString = "string!", SomeSubClassChildInt = 5858 };

            var someSubClassResult = RoundTripToExpectedType<SomeBaseClass, SomeSubClass>(someSubClass);
            Assert.Equal(someSubClass.SscString, someSubClassResult.SscString);
            Assert.Equal(someSubClass.SscInteger, someSubClassResult.SscInteger);
            Assert.Equal(someSubClass.SbcString, someSubClassResult.SbcString);
            Assert.Equal(someSubClass.SbcInteger, someSubClassResult.SbcInteger);

            var otherSubClassResult = RoundTripToExpectedType<SomeBaseClass, OtherSubClass>(otherSubClass);
            Assert.Equal(otherSubClass.OtherSubClassString, otherSubClassResult.OtherSubClassString);
            Assert.Equal(otherSubClass.OtherSubClassInt, otherSubClassResult.OtherSubClassInt);
            Assert.Equal(otherSubClass.SbcString, otherSubClassResult.SbcString);
            Assert.Equal(otherSubClass.SbcInteger, otherSubClassResult.SbcInteger);

            var someSubClassChildResult = RoundTripToExpectedType<SomeBaseClass, SomeSubClassChild>(someSubClassChild);
            Assert.Equal(someSubClassChild.SomeSubClassChildString, someSubClassChildResult.SomeSubClassChildString);
            Assert.Equal(someSubClassChild.SomeSubClassChildInt, someSubClassChildResult.SomeSubClassChildInt);
            Assert.Equal(someSubClassChild.SscString, someSubClassChildResult.SscString);
            Assert.Equal(someSubClassChild.SscInteger, someSubClassChildResult.SscInteger);
            Assert.Equal(someSubClassChild.SbcString, someSubClassChildResult.SbcString);
            Assert.Equal(someSubClassChild.SbcInteger, someSubClassChildResult.SbcInteger);
        }

        [Fact]
        public void DeepCopyPolymorphicTypes()
        {
            var someBaseClass = new SomeBaseClass
            { SbcString = "Shaggy", SbcInteger = 13 };

            var someSubClass = new SomeSubClass
            { SbcString = "Shaggy", SbcInteger = 13, SscString = "Zoinks!", SscInteger = -1 };

            var otherSubClass = new OtherSubClass
            { SbcString = "sbcs", SbcInteger = 2000, OtherSubClassString = "oscs", OtherSubClassInt = 1000 };

            var someSubClassChild = new SomeSubClassChild
            { SbcString = "a", SbcInteger = 0, SscString = "Zoinks!", SscInteger = -1, SomeSubClassChildString = "string!", SomeSubClassChildInt = 5858 };

            var someBaseClassResult = DeepCopy(someBaseClass);
            Assert.Equal(someBaseClass.SbcString, someBaseClassResult.SbcString);
            Assert.Equal(someBaseClass.SbcInteger, someBaseClassResult.SbcInteger);

            var someSubClassResult = DeepCopy(someSubClass);
            Assert.Equal(someSubClass.SscString, someSubClassResult.SscString);
            Assert.Equal(someSubClass.SscInteger, someSubClassResult.SscInteger);
            Assert.Equal(someSubClass.SbcString, someSubClassResult.SbcString);
            Assert.Equal(someSubClass.SbcInteger, someSubClassResult.SbcInteger);

            var otherSubClassResult = DeepCopy(otherSubClass);
            Assert.Equal(otherSubClass.OtherSubClassString, otherSubClassResult.OtherSubClassString);
            Assert.Equal(otherSubClass.OtherSubClassInt, otherSubClassResult.OtherSubClassInt);
            Assert.Equal(otherSubClass.SbcString, otherSubClassResult.SbcString);
            Assert.Equal(otherSubClass.SbcInteger, otherSubClassResult.SbcInteger);

            var someSubClassChildResult = DeepCopy(someSubClassChild);
            Assert.Equal(someSubClassChild.SomeSubClassChildString, someSubClassChildResult.SomeSubClassChildString);
            Assert.Equal(someSubClassChild.SomeSubClassChildInt, someSubClassChildResult.SomeSubClassChildInt);
            Assert.Equal(someSubClassChild.SscString, someSubClassChildResult.SscString);
            Assert.Equal(someSubClassChild.SscInteger, someSubClassChildResult.SscInteger);
            Assert.Equal(someSubClassChild.SbcString, someSubClassChildResult.SbcString);
            Assert.Equal(someSubClassChild.SbcInteger, someSubClassChildResult.SbcInteger);

        }

        private TActual RoundTripToExpectedType<TBase, TActual>(TActual original)
            where TActual : TBase
        {
            var serializer = _serviceProvider.GetService<Serializer<TBase>>();
            var array = serializer.SerializeToArray(original);

            string formatted;
            {
                using var session = _serviceProvider.GetRequiredService<SerializerSessionPool>().GetSession();
                formatted = BitStreamFormatter.Format(array, session);
            }

            return (TActual)serializer.Deserialize(array);
        }

        private T DeepCopy<T>(T original)
        {
            var deepCopier = _serviceProvider.GetService<DeepCopier<T>>();
            return deepCopier.Copy(original);
        }

        [Id(1000)]
        [GenerateSerializer]
        public class SomeBaseClass
        {
            [Id(0)]
            public string SbcString { get; set; }

            [Id(1)]
            public int SbcInteger { get; set; }
        }

        [Id(1001)]
        [GenerateSerializer]
        public class SomeSubClass : SomeBaseClass
        {
            [Id(0)]
            public int SscInteger { get; set; }

            [Id(1)]
            public string SscString { get; set; }
        }

        [Id(1002)]
        [GenerateSerializer]
        public class OtherSubClass : SomeBaseClass
        {
            [Id(0)]
            public int OtherSubClassInt { get; set; }

            [Id(1)]
            public string OtherSubClassString { get; set; }
        }

        [Id(1003)]
        [GenerateSerializer]
        public class SomeSubClassChild : SomeSubClass
        {
            [Id(0)]
            public int SomeSubClassChildInt { get; set; }

            [Id(1)]
            public string SomeSubClassChildString { get; set; }
        }
    }
}