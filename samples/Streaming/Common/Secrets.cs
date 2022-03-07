using System.Text.Json;

namespace Common;

public class Secrets
{
    public string DataConnectionString { get; set; } = null!;

    public string EventHubConnectionString { get; set; } = null!;

    internal Secrets()
    {
    }

    public Secrets(string dataConnectionString!!, string eventHubConnectionString!!)
    {
        DataConnectionString = dataConnectionString;
        EventHubConnectionString = eventHubConnectionString;
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
}
