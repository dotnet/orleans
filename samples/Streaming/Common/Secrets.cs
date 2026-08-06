using System.Text.Json;

namespace Common;

public class Secrets
{
    public string DataConnectionString { get; set; } = null!;

    public string EventHubConnectionString { get; set; } = null!;

    internal Secrets()
    {
    }

    public Secrets(string dataConnectionString, string eventHubConnectionString)
    {
        DataConnectionString = dataConnectionString
            ?? throw new ArgumentException(
                "Must provide a dataConnectionString", nameof(dataConnectionString));
        EventHubConnectionString = eventHubConnectionString
            ?? throw new ArgumentException(
                "Must provide an eventHubConnectionString", nameof(eventHubConnectionString));
    }

    public static Secrets? LoadFromFile(string filename = "Secrets.json")
    {
        var currentDir = new DirectoryInfo(Directory.GetCurrentDirectory());
        while (currentDir != null && currentDir.Exists)
        {
            var filePath = Path.Combine(currentDir.FullName, filename);
            if (File.Exists(filePath))
            {
                return JsonSerializer.Deserialize<Secrets>(File.ReadAllText(filePath));
            }

            currentDir = currentDir.Parent;
        }
        throw new FileNotFoundException($"Cannot find file {filename}");
    }

    public static Secrets? TryLoadFromFile(string filename = "Secrets.json")
    {
        var currentDir = new DirectoryInfo(Directory.GetCurrentDirectory());
        while (currentDir != null && currentDir.Exists)
        {
            var filePath = Path.Combine(currentDir.FullName, filename);
            if (File.Exists(filePath))
            {
                var secrets = JsonSerializer.Deserialize<Secrets>(File.ReadAllText(filePath));
                // Return null if secrets file exists but has empty/missing values
                if (secrets is null ||
                    string.IsNullOrWhiteSpace(secrets.DataConnectionString) ||
                    string.IsNullOrWhiteSpace(secrets.EventHubConnectionString))
                {
                    return null;
                }
                return secrets;
            }

            currentDir = currentDir.Parent;
        }
        return null;
    }
}
