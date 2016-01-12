---
layout: page
title: Configuring .NET Garbage Collection
---
{% include JB/setup %}

For good performance, is it important to configure .NET garbage collection for the silo process the right way. The best combination of settings we found if to set gcServer=true and gcConcurrent=false. The are easy to set via the application config file in case a silo runs as a standalone process. You can use OrleansHost.exe.config included in the [Microsoft.Orleans.OrleansHost](https://www.nuget.org/packages/Microsoft.Orleans.OrleansHost/) NuGet package as an example.

``` xml
<configuration>
  <runtime>
    <gcServer enabled="true"/>
    <gcConcurrent enabled="false"/>
  </runtime>
</configuration>
```

However, this is not as easy to do if a silo runs as part of an Azure Worker Role, which by default is configured to use workstation GC. This blog post shows how to set the same configuration for an Azure Worker Role -  http://blogs.msdn.com/b/cclayton/archive/2014/06/05/server-garbage-collection-mode-in-microsoft-azure.aspx
