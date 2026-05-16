using System.Globalization;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Newtonsoft.Json;
using Orleans.Configuration;
using Orleans.Reminders.Redis;
using Orleans.Runtime;
using StackExchange.Redis;
using TestExtensions;
using UnitTests;
using UnitTests.RemindersTest;
using Xunit;

namespace Tester.Redis.Reminders
{
    /// <summary>
    /// Tests for Redis reminder table implementation.
    /// </summary>
    [TestCategory("Redis"), TestCategory("Reminders"), TestCategory("Functional")]
    [Collection(TestEnvironmentFixture.DefaultCollection)]
    public class RedisRemindersTableTests : ReminderTableTestsBase
    {
        public RedisRemindersTableTests(ConnectionStringFixture fixture, CommonFixture clusterFixture) : base (fixture, clusterFixture, CreateFilters())
        {
            TestUtils.CheckForRedis();
        }

        private static LoggerFilterOptions CreateFilters()
        {
            LoggerFilterOptions filters = new LoggerFilterOptions();
            filters.AddFilter(nameof(RedisRemindersTableTests), LogLevel.Trace);
            return filters;
        }

        protected override IReminderTable CreateRemindersTable()
        {
            TestUtils.CheckForRedis();

            RedisReminderTable reminderTable = new(
                this.loggerFactory.CreateLogger<RedisReminderTable>(),
                this.clusterOptions,
                Options.Create(new RedisReminderTableOptions()
                {
                    ConfigurationOptions = ConfigurationOptions.Parse(GetConnectionString().Result),
                    EntryExpiry = TimeSpan.FromHours(1)
                })); 

            if (reminderTable == null)
            {
                throw new InvalidOperationException("RedisReminderTable not configured");
            }

            return reminderTable;
        }

        protected override Task<string> GetConnectionString() => Task.FromResult(TestDefaultConfiguration.RedisConnectionString);

        [SkippableFact]
        public void RemindersTable_Redis_Init()
        {
        }

        [SkippableFact]
        public async Task RemindersTable_Redis_RemindersRange()
        {
            await RemindersRange(iterations: 50);
        }

        [SkippableFact]
        public async Task RemindersTable_Redis_RemindersParallelUpsert()
        {
            await RemindersParallelUpsert();
        }

        [SkippableFact]
        public async Task RemindersTable_Redis_ReminderSimple()
        {
            await ReminderSimple();
        }

        [SkippableFact]
        public async Task RemindersTable_Redis_Upsert_IgnoresNewtonsoftDefaultSettings()
        {
            await RemindersTable.TestOnlyClearTable();

            var previousDefaultSettings = JsonConvert.DefaultSettings;
            JsonConvert.DefaultSettings = CreateHostileJsonSettings;
            try
            {
                var grainId = GrainId.Create("clientaccount", Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture));
                var reminder = new ReminderEntry
                {
                    GrainId = grainId,
                    Period = TimeSpan.FromDays(10),
                    ReminderName = "Balance",
                    StartAt = new DateTime(2026, 05, 13, 18, 55, 08, DateTimeKind.Utc).AddTicks(8620861)
                };

                reminder.ETag = await RemindersTable.UpsertRow(reminder);
                reminder.StartAt = reminder.StartAt.AddMinutes(5);
                reminder.ETag = await RemindersTable.UpsertRow(reminder);

                var rows = await RemindersTable.ReadRows(grainId);
                var matchingRows = rows.Reminders.Where(row => row.ReminderName == reminder.ReminderName).ToArray();

                var row = Assert.Single(matchingRows);
                Assert.Equal(reminder.StartAt, row.StartAt);
                Assert.Equal(reminder.Period, row.Period);
            }
            finally
            {
                JsonConvert.DefaultSettings = previousDefaultSettings;
                await RemindersTable.TestOnlyClearTable();
            }
        }

        private static JsonSerializerSettings CreateHostileJsonSettings()
        {
            return new JsonSerializerSettings
            {
                Culture = CultureInfo.InvariantCulture,
                Formatting = Formatting.Indented,
                StringEscapeHandling = StringEscapeHandling.EscapeHtml,
                Converters = { new HostileStringConverter() }
            };
        }

        private sealed class HostileStringConverter : JsonConverter<string>
        {
            public override void WriteJson(JsonWriter writer, string value, JsonSerializer serializer)
            {
                writer.WriteValue($"converted:{value}");
            }

            public override string ReadJson(JsonReader reader, Type objectType, string existingValue, bool hasExistingValue, JsonSerializer serializer)
            {
                return (string)reader.Value;
            }
        }
    }
}
