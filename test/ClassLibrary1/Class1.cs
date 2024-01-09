using Orleans;

namespace ClassLibrary1;

[GenerateSerializer]
[Alias("Class")]
public class Class
{
    [Id(0)] public string A { get; set; }

    [Id(1)] public string B { get; set; }
    [Id(1)] public string C { get; set; }

    [Id(2)] public string B1 { get; set; }
    [Id(2)] public string C1 { get; set; }
    [Id(2)] public string D1 { get; set; }
}