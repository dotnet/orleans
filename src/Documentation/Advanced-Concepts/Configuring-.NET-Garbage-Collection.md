---
layout: page
title: Configuring .NET Garbage Collection
---

# Configuring .NET Garbage Collection

For good performance, it is important to configure .NET garbage collection for the silo process the right way. The best combination of settings we found is to set gcServer=true and gcConcurrent=false. These are easy to set via the application config file when a silo runs as a standalone process. You can use OrleansHost.exe.config included in the [Microsoft.Orleans.OrleansHost](https://www.nuget.org/packages/Microsoft.Orleans.OrleansHost/) NuGet package as an example.

## .NET Framework

``` xml
<configuration>
  <runtime>
    <gcServer enabled="true"/>
    <gcConcurrent enabled="false"/>
  </runtime>
</configuration>
```

## .NET Core

```xml
// .csproj
<PropertyGroup>
  <ServerGarbageCollection>true</ServerGarbageCollection>
  <ConcurrentGarbageCollection>false</ConcurrentGarbageCollection>
</PropertyGroup>
```

However, this is not as easy to do if a silo runs as part of an Azure Worker Role, which by default is configured to use workstation GC. This blog post shows how to set the same configuration for an Azure Worker Role -  http://blogs.msdn.com/b/cclayton/archive/2014/06/05/server-garbage-collection-mode-in-microsoft-azure.aspx

**IMPORTANT NOTE**

[Server garbage collection is available only on multiprocessor computers](https://msdn.microsoft.com/en-us/library/system.runtime.gcsettings.isservergc(v=vs.110).aspx). Therefore,
even if you configure the Garbage Collection either via Application Configuration file (app.config or web.config) or via the scripts on the referred blog post, if the silo is running on a (virtual) machine with a single core, you will not get the benefits of `gcServer=true`.
