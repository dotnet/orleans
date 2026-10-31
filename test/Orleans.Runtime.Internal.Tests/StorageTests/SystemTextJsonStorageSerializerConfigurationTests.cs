#nullable enable
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using Orleans.Hosting;
using Orleans.Serialization;
using Orleans.Storage;
using TestExtensions;
using Xunit;

namespace UnitTests.StorageTests
{
    [TestSuite("BVT")]
    [TestProvider("None")]
    [TestArea("Runtime")]
    public sealed class SystemTextJsonStorageSerializerConfigurationTests
    {
        private sealed record Money(decimal Amount);

        private sealed class MoneyConverter : JsonConverter<Money>
        {
            public override Money Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) => new(decimal.Parse(reader.GetString()!.TrimStart('$'), CultureInfo.InvariantCulture));
            public override void Write(Utf8JsonWriter writer, Money value, JsonSerializerOptions options) => writer.WriteStringValue(string.Create(CultureInfo.InvariantCulture, $"${value.Amount}"));
        }

        private static IServiceProvider BuildServices(Action<ISiloBuilder> configureSilo)
        {
            var builder = Host.CreateApplicationBuilder();
            builder.UseOrleans(siloBuilder =>
            {
                siloBuilder.UseLocalhostClustering();
                configureSilo(siloBuilder);
            });
            return builder.Build().Services;
        }

        [Fact]
        public void ConfigureOptionsDelegateIsApplied()
        {
            var services = BuildServices(siloBuilder => siloBuilder.UseSystemTextJsonGrainStorageSerializer(options =>
            {
                options.JsonSerializerOptions.WriteIndented = true;
                options.JsonSerializerOptions.Converters.Add(new MoneyConverter());
            }));

            var serializer = Assert.IsType<SystemTextJsonGrainStorageSerializer>(services.GetRequiredService<IGrainStorageSerializer>());
            var jsonOptions = services.GetRequiredService<IOptions<SystemTextJsonGrainStorageSerializerOptions>>().Value.JsonSerializerOptions;

            Assert.True(jsonOptions.WriteIndented);
            Assert.Contains(jsonOptions.Converters, converter => converter is MoneyConverter);

            // Orleans' own converters are still registered alongside the user's converters.
            Assert.Contains(jsonOptions.Converters, converter => converter is GrainReferenceConverter);

            var serialized = serializer.Serialize(new Money(12.5m));
            Assert.Equal("\"$12.5\"", serialized.ToString());
            Assert.Equal(new Money(12.5m), serializer.Deserialize<Money>(serialized));
        }

        [Fact]
        public void ConfigureOptionsDelegateRunsBeforeOrleansConverters()
        {
            var services = BuildServices(siloBuilder => siloBuilder.UseSystemTextJsonGrainStorageSerializer(options =>
                options.JsonSerializerOptions.Converters.Add(new MoneyConverter())));

            var converters = services.GetRequiredService<IOptions<SystemTextJsonGrainStorageSerializerOptions>>().Value.JsonSerializerOptions.Converters;
            var userConverterIndex = converters.ToList().FindIndex(converter => converter is MoneyConverter);
            var orleansConverterIndex = converters.ToList().FindIndex(converter => converter is GrainReferenceConverter);

            Assert.True(userConverterIndex >= 0);
            Assert.True(userConverterIndex < orleansConverterIndex);
        }

        [Fact]
        public void ConfigureOptionsBuilderDelegateCanUseServices()
        {
            var converter = new MoneyConverter();
            var services = BuildServices(siloBuilder =>
            {
                siloBuilder.Services.AddSingleton(converter);
                siloBuilder.UseSystemTextJsonGrainStorageSerializer(optionsBuilder =>
                    optionsBuilder.Configure<MoneyConverter>((options, moneyConverter) => options.JsonSerializerOptions.Converters.Add(moneyConverter)));
            });

            var serializer = Assert.IsType<SystemTextJsonGrainStorageSerializer>(services.GetRequiredService<IGrainStorageSerializer>());
            var converters = services.GetRequiredService<IOptions<SystemTextJsonGrainStorageSerializerOptions>>().Value.JsonSerializerOptions.Converters;

            Assert.Contains(converter, converters);
            Assert.Contains(converters, c => c is GrainReferenceConverter);
            Assert.Equal("\"$12.5\"", serializer.Serialize(new Money(12.5m)).ToString());
        }

        [Fact]
        public void ConfigureOptionsDelegateMustNotBeNull()
        {
            Assert.Throws<ArgumentNullException>(() => BuildServices(siloBuilder =>
                siloBuilder.UseSystemTextJsonGrainStorageSerializer((Action<SystemTextJsonGrainStorageSerializerOptions>)null!)));
            Assert.Throws<ArgumentNullException>(() => BuildServices(siloBuilder =>
                siloBuilder.UseSystemTextJsonGrainStorageSerializer((Action<OptionsBuilder<SystemTextJsonGrainStorageSerializerOptions>>)null!)));
        }
    }
}
