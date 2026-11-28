using Orleans.AdvancedReminders.Redis;
using TestExtensions;
using Xunit;

namespace Tester.Redis.AdvancedReminders;

[TestSuite("BVT")]
[TestProvider("None")]
[TestArea("Reminders")]
public class RedisAdvancedReminderExceptionTests
{
    [Fact]
    public void Serialization_PreservesMessageAndCauseWithoutFormatterConstructor()
    {
        using var environment = SerializationTestEnvironment.InitializeWithDefaults();
        var original = new RedisRemindersException("Redis write failed.", new InvalidOperationException("Connection closed."));
        var copy = environment.Serializer.Deserialize<RedisRemindersException>(environment.Serializer.SerializeToArray(original));

        Assert.NotNull(copy);
        Assert.Equal(original.Message, copy.Message);
        Assert.Equal(original.InnerException!.Message, Assert.IsType<InvalidOperationException>(copy.InnerException).Message);
    }
}
