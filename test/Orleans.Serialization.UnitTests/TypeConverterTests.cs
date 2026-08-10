#nullable enable
using System;
using System.Collections.Generic;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Orleans.Serialization.Activators;
using Orleans.Serialization.Configuration;
using Orleans.Serialization.TypeSystem;
using UnitTests.SerializerExternalModels;
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
        public void TypeConverter_AllowAllTypes_TakesPrecedenceOverDenyingFilters()
        {
            var converter = CreateConverter(
                allowAllTypes: true,
                typeNameFilters: [new DelegateTypeNameFilter((_, _) => false)],
                typeFilters: [new DelegateTypeFilter(_ => false)]);

            AssertRoundTrips(converter, typeof(TypeConverterTestsUnconfiguredType));
        }

        [Fact]
        public void TypeConverter_JsonSerializerRegistration_DoesNotAuthorizeAbstractGenericArguments()
        {
            using var services = CreateJsonSerializerServices();
            var converter = services.GetRequiredService<TypeConverter>();
            var type = typeof(IReadOnlyList<JsonPolymorphicBase>);

            AssertTypeNotAllowed(converter, type);

            var exception = Assert.Throws<InvalidOperationException>(() => converter.Format(type));
            Assert.Contains(nameof(TypeManifestOptions.AddAllowedType), exception.Message);
            Assert.Contains(nameof(TypeManifestOptions.AddAllowedAssembly), exception.Message);
            Assert.Contains(nameof(TypeManifestOptions.AllowAllTypes), exception.Message);
        }

        [Fact]
        public void TypeConverter_JsonSerializer_AllowsAbstractGenericArgumentsAddedByType()
        {
            using var services = CreateJsonSerializerServices(
                options => options.AddAllowedType(typeof(JsonPolymorphicBase)));

            AssertRoundTrips(
                services.GetRequiredService<TypeConverter>(),
                typeof(IReadOnlyList<JsonPolymorphicBase>));
        }

        [Fact]
        public void TypeConverter_JsonSerializer_AllowsAbstractGenericArgumentsAddedByFormattedName()
        {
            using var services = CreateJsonSerializerServices(
                options => options.AllowedTypes.Add(typeof(JsonPolymorphicBase).FullName!));

            AssertRoundTrips(
                services.GetRequiredService<TypeConverter>(),
                typeof(IReadOnlyList<JsonPolymorphicBase>));
        }

        [Fact]
        public void TypeConverter_JsonSerializer_AllowsAbstractGenericArgumentsFromAllowedAssembly()
        {
            using var services = CreateJsonSerializerServices(
                options => options.AddAllowedAssembly(typeof(JsonPolymorphicBase).Assembly));

            AssertRoundTrips(
                services.GetRequiredService<TypeConverter>(),
                typeof(IReadOnlyList<JsonPolymorphicBase>));
        }

        [Fact]
        public void TypeConverter_JsonSerializer_AllowsAbstractGenericArgumentsWhenAllTypesAreAllowed()
        {
            using var services = CreateJsonSerializerServices(options => options.AllowAllTypes = true);

            AssertRoundTrips(
                services.GetRequiredService<TypeConverter>(),
                typeof(IReadOnlyList<JsonPolymorphicBase>));
        }

        [Fact]
        public void TypeConverter_JsonSerializer_AllowsAbstractGenericArgumentsUsingTypeNameFilter()
        {
            using var services = CreateJsonSerializerServices(
                configureServices: services => services.AddSingleton<ITypeNameFilter>(
                    new DelegateTypeNameFilter((typeName, _) =>
                        typeName == typeof(JsonPolymorphicBase).FullName ? true : null)));

            AssertRoundTrips(
                services.GetRequiredService<TypeConverter>(),
                typeof(IReadOnlyList<JsonPolymorphicBase>));
        }

        [Fact]
        public void TypeConverter_JsonSerializer_AllowsAbstractGenericArgumentsUsingTypeFilter()
        {
            using var services = CreateJsonSerializerServices(
                configureServices: services => services.AddSingleton<ITypeFilter>(
                    new DelegateTypeFilter(type =>
                        type == typeof(JsonPolymorphicBase) ? true : null)));

            AssertRoundTrips(
                services.GetRequiredService<TypeConverter>(),
                typeof(IReadOnlyList<JsonPolymorphicBase>));
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
        public void TypeConverter_UsesCachedTypeNameFilterResults()
        {
            var typeNameFilterCalls = 0;
            var converter = CreateConverter(
                typeNameFilters:
                [
                    new DelegateTypeNameFilter((typeName, _) =>
                    {
                        if (typeName == typeof(TypeConverterTestsUnconfiguredType).FullName)
                        {
                            typeNameFilterCalls++;
                            return true;
                        }

                        return null;
                    })
                ]);

            AssertRoundTrips(converter, typeof(TypeConverterTestsUnconfiguredType));

            Assert.Equal(1, typeNameFilterCalls);
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
        public void TypeConverter_ConfiguredAllowedTypes_TakePrecedenceOverDenyingFilters()
        {
            var converter = CreateConverter(
                configureOptions: options => options.AddAllowedType(typeof(TypeConverterTestsUnconfiguredType)),
                typeNameFilters: [new DelegateTypeNameFilter((_, _) => false)],
                typeFilters: [new DelegateTypeFilter(_ => false)]);

            AssertRoundTrips(converter, typeof(TypeConverterTestsUnconfiguredType));
        }

        [Fact]
        public void TypeConverter_AllowsConfiguredAllowedTypes()
        {
            var converter = CreateConverter(configureOptions: options => options.AddAllowedType(typeof(TypeConverterTestsUnconfiguredType)));

            AssertRoundTrips(converter, typeof(TypeConverterTestsUnconfiguredType));
        }

        [Fact]
        public void TypeConverter_AddAllowedType_FormatsConstructedNestedGenericTypes()
        {
            var type = typeof(Dictionary<TypeConverterTestsUnconfiguredType, TypeConverterTestsNestedAllowedType.Nested>);
            var options = new TypeManifestOptions();
            options.AddAllowedType(type);

            Assert.Contains(RuntimeTypeNameFormatter.FormatInternalNoCache(type, allowAliases: false), options.AllowedTypes);

            var converter = CreateConverter(configureOptions: options => options.AddAllowedType(type));

            AssertRoundTrips(converter, type);
        }

        [Fact]
        public void TypeConverter_AddAllowedType_FormatsGenericArgumentsWithoutCompoundAliases()
        {
            var type = typeof(List<TypeConverterTestsAttributedCompoundAliasedType>);
            var options = new TypeManifestOptions();
            options.AddAllowedType(type);

            Assert.DoesNotContain(options.AllowedTypes, allowedType => allowedType.Contains("type_converter_attributed_alias", StringComparison.Ordinal));

            var converter = CreateConverter(
                configureOptions: options =>
                {
                    options.AddAllowedType(type);
                    options.CompoundTypeAliases
                        .Add("type_converter_attributed_alias")
                        .Add("v1", typeof(TypeConverterTestsAttributedCompoundAliasedType));
                });

            AssertRoundTrips(converter, type);
        }

        [Fact]
        public void TypeConverter_AllowsConfiguredAllowedAssemblies()
        {
            var converter = CreateConverter(configureOptions: options => options.AddAllowedAssembly(typeof(TypeConverterTestsAssemblyAllowedType).Assembly));

            AssertRoundTrips(converter, typeof(TypeConverterTestsAssemblyAllowedType));
            AssertRoundTrips(converter, typeof(List<TypeConverterTestsAssemblyAllowedType>));
        }

        [Fact]
        public void TypeConverter_RejectsAllowedAssemblyType_WhenTypeNameFilterDeniesIt()
        {
            var converter = CreateConverter(
                configureOptions: options => options.AddAllowedAssembly(typeof(TypeConverterTestsAssemblyAllowedType).Assembly),
                typeNameFilters:
                [
                    new DelegateTypeNameFilter((typeName, _) => typeName == typeof(TypeConverterTestsAssemblyAllowedType).FullName ? false : null)
                ]);

            AssertTypeNotAllowed(converter, typeof(TypeConverterTestsAssemblyAllowedType));
        }

        [Fact]
        public void TypeConverter_RejectsAllowedAssemblyType_WhenTypeFilterDeniesIt()
        {
            var converter = CreateConverter(
                configureOptions: options => options.AddAllowedAssembly(typeof(TypeConverterTestsAssemblyAllowedType).Assembly),
                typeFilters:
                [
                    new DelegateTypeFilter(type => type == typeof(TypeConverterTestsAssemblyAllowedType) ? false : null)
                ]);

            AssertTypeNotAllowed(converter, typeof(TypeConverterTestsAssemblyAllowedType));
        }

        [Fact]
        public void TypeConverter_DoesNotAllowTypesFromSpoofedAllowedAssemblyNames()
        {
            var converter = CreateConverter(configureOptions: options => options.AddAllowedAssembly(typeof(TypeConverterTestsAssemblyAllowedType).Assembly));
            var formatted = $"{typeof(System.Text.StringBuilder).FullName},{CachedTypeResolver.GetName(typeof(TypeConverterTestsAssemblyAllowedType).Assembly)}";

            Assert.Throws<InvalidOperationException>(() => converter.Parse(formatted));
        }

        [Fact]
        public void TypeConverter_DoesNotAllowGenericArgumentsJustBecauseGenericTypeDefinitionAssemblyIsAllowed()
        {
            var converter = CreateConverter(configureOptions: options => options.AddAllowedAssembly(typeof(TypeConverterTestsAssemblyAllowedType<>).Assembly));

            AssertTypeNotAllowed(converter, typeof(TypeConverterTestsAssemblyAllowedType<UriBuilder>));
        }

        [Fact]
        public void TypeConverter_DoesNotAllowGenericArgumentsJustBecauseSiblingGenericArgumentAssemblyIsAllowed()
        {
            var converter = CreateConverter(configureOptions: options => options.AddAllowedAssembly(typeof(TypeConverterTestsAssemblyAllowedType).Assembly));

            AssertTypeNotAllowed(converter, typeof(Dictionary<TypeConverterTestsAssemblyAllowedType, UriBuilder>));
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
        public void TypeConverter_AllowsConstructedGenericTypes_WhenTypeFilterAllowsThem()
        {
            var type = typeof(TypeConverterTestsGenericTypeAllowedByTypeFilter<UriBuilder>);
            var converter = CreateConverter(
                typeFilters:
                [
                    new DelegateTypeFilter(candidate => candidate == type ? true : null)
                ]);

            AssertRoundTrips(converter, type);
        }

        [Fact]
        public void TypeConverter_RejectsConstructedGenericTypes_WhenTypeFilterDeniesGenericArguments()
        {
            var type = typeof(TypeConverterTestsGenericTypeAllowedByTypeFilter<UriBuilder>);
            var converter = CreateConverter(
                typeFilters:
                [
                    new DelegateTypeFilter(candidate =>
                    {
                        if (candidate == type)
                        {
                            return true;
                        }

                        return candidate == typeof(UriBuilder) ? false : null;
                    })
                ]);

            AssertTypeNotAllowed(converter, type);
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
        public void TypeConverter_AllowsEnums_WhenAllFiltersHaveNoOpinion()
        {
            var converter = CreateConverter();

            AssertRoundTrips(converter, typeof(TypeConverterTestsEnum));
        }

        [Fact]
        public void TypeConverter_AllowsEnums_AsGenericArguments_WhenAllFiltersHaveNoOpinion()
        {
            var converter = CreateConverter();

            AssertRoundTrips(converter, typeof(List<TypeConverterTestsEnum>));
        }

        [Fact]
        public void TypeConverter_AllowsEnums_AsArrayElements_WhenAllFiltersHaveNoOpinion()
        {
            var converter = CreateConverter();

            AssertRoundTrips(converter, typeof(TypeConverterTestsEnum[]));
        }

        [Fact]
        public void TypeConverter_RejectsEnums_WhenATypeFilterExplicitlyDeniesThem()
        {
            var converter = CreateConverter(
                typeFilters:
                [
                    new DelegateTypeFilter(type => type == typeof(TypeConverterTestsEnum) ? false : null)
                ]);

            AssertTypeNotAllowed(converter, typeof(TypeConverterTestsEnum));
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
                    options.AddAllowedType(typeof(TypeConverterTestsCompoundAliasedWithComponentType));
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
                    options.AddAllowedType(typeof(TypeConverterTestsCompoundAliasedType));
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

        private static ServiceProvider CreateJsonSerializerServices(
            Action<TypeManifestOptions>? configureOptions = null,
            Action<IServiceCollection>? configureServices = null)
        {
            var services = new ServiceCollection();
            services.AddSerializer(builder =>
            {
                builder.AddJsonSerializer(isSupported: _ => true);
                if (configureOptions is not null)
                {
                    builder.Configure(configureOptions);
                }
            });

            configureServices?.Invoke(services);
            return services.BuildServiceProvider();
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

    internal sealed class TypeConverterTestsAssemblyAllowedType
    {
    }

    internal sealed class TypeConverterTestsAssemblyAllowedType<T>
    {
    }

    internal sealed class TypeConverterTestsNestedAllowedType
    {
        internal sealed class Nested
        {
        }
    }

    internal sealed class TypeConverterTestsGenericArgumentAllowedByTypeFilter
    {
    }

    internal sealed class TypeConverterTestsGenericTypeAllowedByTypeFilter<T>
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

    internal enum TypeConverterTestsEnum
    {
        None,
        First,
        Second,
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

    [global::Orleans.CompoundTypeAlias("type_converter_attributed_alias", "v1")]
    internal sealed class TypeConverterTestsAttributedCompoundAliasedType
    {
    }

    internal sealed class TypeConverterTestsMetadataAllowedTypeActivator : IActivator<TypeConverterTestsMetadataAllowedType>
    {
        public TypeConverterTestsMetadataAllowedType Create() => throw new NotSupportedException();
    }
}
