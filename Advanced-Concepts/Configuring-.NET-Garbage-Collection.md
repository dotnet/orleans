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

*** **IMPORTANT NOTE** ***
Even configuring the Garbage Collection by Application Configuration file (app.config or web.config) or by the scripts on the refered blog bost, if the silo is running on a (virtual)machine with a single core, you will not have the benefits of `gcServer=true` and Orleans Runtime will still print warnings on log at silo startup, just as if the config wasn't set. The reason for that, is that those settings only take effect if you are running on a multi-core (virtual)machine.
