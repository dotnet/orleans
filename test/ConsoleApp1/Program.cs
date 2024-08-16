using ClassLibrary1;
using Microsoft.Extensions.Hosting;

var host = Host.CreateDefaultBuilder(args).CreateHost(0);
await host.StartAsync();
await host.WaitTillClusterIsUp();

//var allocated = Allocate(arrayCount: 10, arraySizeInMB: 10);

while (true)
{
    await Task.Delay(1000);
    //var count = allocated.Count;
}

/*
static List<byte[]> Allocate(int arrayCount, int arraySizeInMB)
{
    List<byte[]> allocatedMemory = [];

    for (var i = 0; i < arrayCount; i++)
    {
        var array = new byte[arraySizeInMB * 1024 * 1024];

        for (var j = 0; j < array.Length; j++)
        {
            array[j] = (byte)(j % 256);
        }

        allocatedMemory.Add(array);
    }

    return allocatedMemory;
}
*/