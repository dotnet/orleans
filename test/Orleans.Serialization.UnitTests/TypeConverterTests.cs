#nullable enable
using System;
using System.Collections.Generic;
using Microsoft.Extensions.Options;
using Orleans.Serialization.Activators;
using Orleans.Serialization.Configuration;
using Orleans.Serialization.TypeSystem;
using Xunit;

namespace Orleans.Serialization.UnitTests
{
    public class TypeConverterTests
    {
        [Fact]
        public void TypeConverter_FailsClosed_WhenAllFiltersHaveNoOpinion_AndAllowAllTypesIsFalse()
        {
            var converter = CreateConverter();

            AssertTypeNotAllowed(converter, typeof(TypeConverterTestsUnconfiguredType));
        }

        [Fact]
        public void TypeConverter_DefaultsToAllowAllTypes_WhenAllFiltersHaveNoOpinion_AndAllowAllTypesIsTrue()
        {
            var converter = CreateConverter(allowAllTypes: true);

            AssertRoundTrips(converter, typeof(TypeConverterTestsUnconfiguredType));
        }

        [Fact]
        public void TypeConverter_AllowsTypes_WhenATypeNameFilterExplicitlyAllowsThem()
        {
            var converter = CreateConverter(
                typeNameFilters:
                [
                    new DelegateTypeNameFilter((typeName, _) => typeName == typeof(TypeConverterTestsUnconfiguredType).FullName ? true : null)
                ]);

            AssertRoundTrips(converter, typeof(TypeConverterTestsUnconfiguredType));
        }

        [Fact]
        public void TypeConverter_AllowsTypes_WhenATypeFilterExplicitlyAllowsThem()
        {
            var converter = CreateConverter(
                typeFilters:
                [
                    new DelegateTypeFilter(type => type == typeof(TypeConverterTestsUnconfiguredType) ? true : null)
                ]);

            AssertRoundTrips(converter, typeof(TypeConverterTestsUnconfiguredType));
        }

        [Fact]
        public void TypeConverter_RejectsTypes_WhenATypeFilterExplicitlyDeniesThem()
        {
            var converter = CreateConverter(
                typeFilters:
                [
                    new DelegateTypeFilter(type => type == typeof(TypeConverterTestsUnconfiguredType) ? false : null)
                ]);

            AssertTypeNotAllowed(converter, typeof(TypeConverterTestsUnconfiguredType));
        }

        [Fact]
        public void TypeConverter_RejectsTypes_WhenATypeNameFilterDeniesThem_EvenIfATypeFilterAllowsThem()
        {
            var converter = CreateConverter(
                typeNameFilters:
                [
                    new DelegateTypeNameFilter((typeName, _) => typeName == typeof(TypeConverterTestsUnconfiguredType).FullName ? false : null)
                ],
                typeFilters:
                [
                    new DelegateTypeFilter(type => type == typeof(TypeConverterTestsUnconfiguredType) ? true : null)
                ]);

            AssertTypeNotAllowed(converter, typeof(TypeConverterTestsUnconfiguredType));
        }

        [Fact]
        public void TypeConverter_AllowsConfiguredAllowedTypes()
        {
            var converter = CreateConverter(configureOptions: options => options.AllowedTypes.Add(typeof(TypeConverterTestsUnconfiguredType).FullName!));

            AssertRoundTrips(converter, typeof(TypeConverterTestsUnconfiguredType));
        }

        [Fact]
        public void TypeConverter_AllowsBuiltInAliasesUnderFailClosedBehavior()
        {
            var converter = CreateConverter();

            var formatted = converter.Format(typeof(int));

            Assert.Equal("int", formatted);
            Assert.Equal(typeof(int), converter.Parse(formatted));
            Assert.Equal(typeof(int), converter.Parse("int"));
        }

        [Fact]
        public void TypeConverter_DoesNotAllowGenericArgumentsJustBecauseTheGenericTypeDefinitionIsAllowed()
        {
            var converter = CreateConverter(typeNameFilters: [new DefaultTypeFilter()]);

            AssertTypeNotAllowed(converter, typeof(List<UriBuilder>));
        }

        [Fact]
        public void TypeConverter_UsesTypeFiltersForGenericArguments_WhenNameFiltersHaveNoOpinion()
        {
            var converter = CreateConverter(
                typeFilters:
                [
                    new DelegateTypeFilter(type => type == typeof(TypeConverterTestsGenericArgumentAllowedByTypeFilter) ? true : null)
                ]);

            AssertRoundTrips(converter, typeof(List<TypeConverterTestsGenericArgumentAllowedByTypeFilter>));
        }

        [Fact]
        public void TypeConverter_UsesTypeFiltersForArrayElementTypes_WhenNameFiltersHaveNoOpinion()
        {
            var converter = CreateConverter(
                typeFilters:
                [
                    new DelegateTypeFilter(type => type == typeof(TypeConverterTestsArrayElementAllowedByTypeFilter) ? true : null)
                ]);

            AssertRoundTrips(converter, typeof(TypeConverterTestsArrayElementAllowedByTypeFilter[]));
        }

        [Fact]
        public void TypeConverter_AllowsMetadataRegisteredTypes()
        {
            var converter = CreateConverter(configureOptions: options => options.Activators.Add(typeof(TypeConverterTestsMetadataAllowedTypeActivator)));

            AssertRoundTrips(converter, typeof(TypeConverterTestsMetadataAllowedType));
        }

        [Fact]
        public void TypeConverter_RoundTripsConfiguredWellKnownAliases_WithoutSeparatelyAllowingUnderlyingTypes()
        {
            const string alias = "type_converter_alias";
            var converter = CreateConverter(
                configureOptions: options =>
                {
                    options.WellKnownTypeAliases[alias] = typeof(TypeConverterTestsAliasedType);
                });

            var formatted = converter.Format(typeof(TypeConverterTestsAliasedType));

            Assert.Contains(alias, formatted);
            Assert.Equal(typeof(TypeConverterTestsAliasedType), converter.Parse(formatted));
        }

        [Fact]
        public void TypeConverter_ParsesConfiguredCompoundAliases_WithAliasComponentTypesWithoutSeparatelyAllowingThem()
        {
            const string componentAlias = "type_converter_component_alias";
            const string alias = "(\"type_converter_compound_alias_with_component\",[type_converter_component_alias],\"v1\")";
            var converter = CreateConverter(
                configureOptions: options =>
                {
                    options.AllowedTypes.Add(typeof(TypeConverterTestsCompoundAliasedWithComponentType).FullName!);
                    options.WellKnownTypeAliases[componentAlias] = typeof(TypeConverterTestsAliasComponentType);
                    options.CompoundTypeAliases
                        .Add("type_converter_compound_alias_with_component")
                        .Add(typeof(TypeConverterTestsAliasComponentType))
                        .Add("v1", typeof(TypeConverterTestsCompoundAliasedWithComponentType));
                });

            Assert.Equal(typeof(TypeConverterTestsCompoundAliasedWithComponentType), converter.Parse(alias));
        }

        [Fact]
        public void TypeConverter_ParsesConfiguredCompoundAliases_WhenUnderlyingTypeIsAllowed()
        {
            const string alias = "(\"type_converter_compound_alias\",\"v1\")";
            var converter = CreateConverter(
                configureOptions: options =>
                {
                    options.AllowedTypes.Add(typeof(TypeConverterTestsCompoundAliasedType).FullName!);
                    options.CompoundTypeAliases.Add("type_converter_compound_alias").Add("v1", typeof(TypeConverterTestsCompoundAliasedType));
                });

            Assert.Equal(typeof(TypeConverterTestsCompoundAliasedType), converter.Parse(alias));
        }

        private static void AssertRoundTrips(TypeConverter converter, Type type)
        {
            var formatted = converter.Format(type);
            Assert.Equal(type, converter.Parse(formatted));
        }

        private static void AssertTypeNotAllowed(TypeConverter converter, Type type)
        {
            var formatted = RuntimeTypeNameFormatter.Format(type);

            Assert.Throws<InvalidOperationException>(() => converter.Format(type));
            Assert.Throws<InvalidOperationException>(() => converter.Parse(formatted));
        }

        private static TypeConverter CreateConverter(
            bool allowAllTypes = false,
            Action<TypeManifestOptions>? configureOptions = null,
            ITypeNameFilter[]? typeNameFilters = null,
            ITypeFilter[]? typeFilters = null)
        {
            var options = new TypeManifestOptions
            {
                AllowAllTypes = allowAllTypes
            };
            configureOptions?.Invoke(options);

            return new TypeConverter(
                Array.Empty<ITypeConverter>(),
                typeNameFilters ?? Array.Empty<ITypeNameFilter>(),
                typeFilters ?? Array.Empty<ITypeFilter>(),
                Options.Create(options),
                new CachedTypeResolver());
        }

        private sealed class DelegateTypeNameFilter(Func<string, string, bool?> filter) : ITypeNameFilter
        {
            public bool? IsTypeNameAllowed(string typeName, string assemblyName) => filter(typeName, assemblyName);
        }

        private sealed class DelegateTypeFilter(Func<Type, bool?> filter) : ITypeFilter
        {
            public bool? IsTypeAllowed(Type type) => filter(type);
        }
    }

    internal sealed class TypeConverterTestsUnconfiguredType
    {
    }

    internal sealed class TypeConverterTestsGenericArgumentAllowedByTypeFilter
    {
    }

    internal sealed class TypeConverterTestsArrayElementAllowedByTypeFilter
    {
    }

    internal sealed class TypeConverterTestsMetadataAllowedType
    {
    }

    internal sealed class TypeConverterTestsAliasedType
    {
    }

    internal sealed class TypeConverterTestsCompoundAliasedType
    {
    }

    internal sealed class TypeConverterTestsAliasComponentType
    {
    }

    internal sealed class TypeConverterTestsCompoundAliasedWithComponentType
    {
    }

    internal sealed class TypeConverterTestsMetadataAllowedTypeActivator : IActivator<TypeConverterTestsMetadataAllowedType>
    {
        public TypeConverterTestsMetadataAllowedType Create() => throw new NotSupportedException();
    }
}
