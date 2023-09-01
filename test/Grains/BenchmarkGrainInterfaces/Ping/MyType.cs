namespace BenchmarkGrainInterfaces.Ping;

[GenerateSerializer]
public class UserProfile
{
    [Id(0)]
    public string DisplayName { get; set; }

    [Id(1)]
    public string PreferredLanguage { get; set; }

    [Id(2)]
    public DateTimeOffset AccountCreated { get; set; }
}









