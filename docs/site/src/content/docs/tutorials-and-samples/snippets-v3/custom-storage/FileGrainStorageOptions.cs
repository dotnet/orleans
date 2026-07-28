using Microsoft.Extensions.Options;

namespace GrainStorage;

// <file_grain_storage_options>
public class FileGrainStorageOptions
{
    public string RootDirectory { get; set; }
}
// </file_grain_storage_options>
