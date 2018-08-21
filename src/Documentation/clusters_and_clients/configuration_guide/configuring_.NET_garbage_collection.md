---
layout: page
title: Configuring .NET Garbage Collection
---

# Configuring .NET Garbage Collection

For good performance, it is important to configure .NET garbage collection for the silo process the right way.
The best combination of settings we found is to set gcServer=true and gcConcurrent=true.
These are easy to set via the application csproj file.
See below as an example:

## .NET Framework and .NET Core

``` csproj
// .csproj
<PropertyGroup>
  <ServerGarbageCollection>true</ServerGarbageCollection>
  <ConcurrentGarbageCollection>true</ConcurrentGarbageCollection>
</PropertyGroup>
```

## .NET Framework with old .csproj project format

``` xml
// App.config
<configuration>
  <runtime>
    <gcServer enabled="true"/>
    <gcConcurrent enabled="true"/>
  </runtime>
</configuration>
```

However, this is not as easy to do if a silo runs as part of an Azure Worker Role, which by default is configured to use workstation GC. This blog post shows how to set the same configuration for an Azure Worker Role -  https://blogs.msdn.microsoft.com/cclayton/2014/06/05/server-garbage-collection-mode-in-microsoft-azure/

**IMPORTANT NOTE**

[Server garbage collection is available only on multiprocessor computers](https://msdn.microsoft.com/en-us/library/system.runtime.gcsettings.isservergc(v=vs.110).aspx).
Therefore, even if you configure the Garbage Collection either via application csproj file or via the scripts on the referred blog post, if the silo is running on a (virtual) machine with a single core, you will not get the benefits of `gcServer=true`.
