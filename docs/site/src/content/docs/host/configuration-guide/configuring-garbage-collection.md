---
title: Configure .NET garbage collection
description: Learn how to configure .NET garbage collection in .NET Orleans.
ms.date: 05/23/2025
ms.topic: how-to
---

# Configure .NET garbage collection

For good performance, it's important to configure .NET garbage collection correctly for the silo process. Based on the team's findings, the best combination of settings is `gcServer=true` and `gcConcurrent=true`. You can configure these values in your C# project (_.csproj_) or an _app.config_ file. For more information, see [Flavors of garbage collection](../../../core/runtime-config/garbage-collector.md#flavors-of-garbage-collection).

## .NET Core and .NET 5+

This method isn't supported for SDK-style projects compiling against the full .NET Framework.

```xml
<PropertyGroup>
    <ServerGarbageCollection>true</ServerGarbageCollection>
    <ConcurrentGarbageCollection>true</ConcurrentGarbageCollection>
</PropertyGroup>
```

## .NET Framework

SDK-style projects compiling against the full .NET Framework should still use this configuration style. Consider an example _app.config_ XML file:

``` xml
<configuration>
    <runtime>
        <gcServer enabled="true"/>
        <gcConcurrent enabled="true"/>
    </runtime>
</configuration>
```

However, this isn't as easy if a silo runs as part of an Azure Worker Role, which defaults to using workstation GC. A relevant blog post discusses how to set the same configuration for an Azure Worker Role; see [Server garbage collection mode in Azure](/archive/blogs/cclayton/server-garbage-collection-mode-in-microsoft-azure).

> [!IMPORTANT]
> Server garbage collection is available only on multiprocessor computers. Therefore, even if you configure garbage collection via the application _.csproj_ file or the scripts in the referred blog post, you won't get the benefits of `gcServer=true` if the silo runs on a (virtual) machine with a single core. For more information, see [GCSettings.IsServerGC remarks](/dotnet/api/system.runtime.gcsettings.isservergc#remarks).
