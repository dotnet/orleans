using System.Diagnostics.CodeAnalysis;

[assembly: SuppressMessage(
    "Style",
    "IDE0130:Namespace does not match folder structure",
    Justification = "Public builder extensions follow the repository convention of using Orleans.Hosting.",
    Scope = "namespace",
    Target = "~N:Orleans.Hosting")]
