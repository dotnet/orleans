#nullable enable
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using Orleans.Hosting;
using Orleans.Serialization;
using Orleans.Storage;
using Xunit;

namespace UnitTests.StorageTests
{
    public sealed class SystemTextJsonStorageSerializerConfigurationTests
    {
        private sealed record Money(decimal Amount);

        private sealed class MoneyConverter : JsonConverter<Money>
        {
            public override Money Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) => new(decimal.Parse(reader.GetString()!.TrimStart('$')));
            public override void Write(Utf8JsonWriter writer, Money value, JsonSerializerOptions options) => writer.WriteStringValue($"${value.Amount}");
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
        public void ConfigureOptionsDelegateMustNotBeNull()
        {
            Assert.Throws<ArgumentNullException>(() => BuildServices(siloBuilder => siloBuilder.UseSystemTextJsonGrainStorageSerializer(null!)));
        }
    }
}
