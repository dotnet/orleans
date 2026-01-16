using Orleans.Serialization;

namespace Benchmarks.GrainStorage;

[GenerateSerializer]
public sealed class BenchmarkState
{
    [Id(0)]
    public string PayloadA { get; set; } = string.Empty;

    [Id(1)]
    public string PayloadB { get; set; } = string.Empty;

    [Id(2)]
    public string PayloadC { get; set; } = string.Empty;

    [Id(3)]
    public string PayloadD { get; set; } = string.Empty;

    public static BenchmarkState Create(int size)
    {
        var perPropertySize = size / 4;
        var perPropertyChars = Math.Max(0, perPropertySize / 2);
        var payload = CreatePayload(perPropertyChars);

        return new BenchmarkState
        {
            PayloadA = payload,
            PayloadB = payload,
            PayloadC = payload,
            PayloadD = payload
        };
    }

    private static string CreatePayload(int length)
    {
        if (length <= 0)
        {
            return string.Empty;
        }

        var chars = new char[length];
        for (var i = 0; i < chars.Length; i++)
        {
            chars[i] = (char)('a' + Random.Shared.Next(0, 26));
        }

        return new string(chars);
    }
}
