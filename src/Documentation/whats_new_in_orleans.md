---
layout: page
title: What's new in Orleans
---

# What's new in Orleans?

## [v2.1.0](https://github.com/dotnet/orleans/releases/tag/v2.1.0) September 28th 2018

- Major changes
  - New scheduler ([#3792](https://github.com/dotnet/orleans/pull/3792))
  - Hosted Client ([#3362](https://github.com/dotnet/orleans/pull/3362))
  - Distributed Transaction Manager ([#3820](https://github.com/dotnet/orleans/pull/3820), [#4502](https://github.com/dotnet/orleans/pull/4502), [#4538](https://github.com/dotnet/orleans/pull/4538), [#4566](https://github.com/dotnet/orleans/pull/4566), [#4568](https://github.com/dotnet/orleans/pull/4568), [#4591](https://github.com/dotnet/orleans/pull/4591), [#4599](https://github.com/dotnet/orleans/pull/4599), [#4613](https://github.com/dotnet/orleans/pull/4613), [#4609](https://github.com/dotnet/orleans/pull/4609), [#4616](https://github.com/dotnet/orleans/pull/4616), [#4608](https://github.com/dotnet/orleans/pull/4608), [#4628](https://github.com/dotnet/orleans/pull/4628), [#4638](https://github.com/dotnet/orleans/pull/4638), [#4685](https://github.com/dotnet/orleans/pull/4685), [#4714](https://github.com/dotnet/orleans/pull/4714), [#4739](https://github.com/dotnet/orleans/pull/4739), [#4768](https://github.com/dotnet/orleans/pull/4768), [#4799](https://github.com/dotnet/orleans/pull/4799), [#4781](https://github.com/dotnet/orleans/pull/4781), [#4810](https://github.com/dotnet/orleans/pull/4810), [#4820](https://github.com/dotnet/orleans/pull/4820), [#4838](https://github.com/dotnet/orleans/pull/4838), [#4831](https://github.com/dotnet/orleans/pull/4831), [#4871](https://github.com/dotnet/orleans/pull/4871), [#4887](https://github.com/dotnet/orleans/pull/4887))
  - New Code Generator ([#4934](https://github.com/dotnet/orleans/pull/4934), [#5010](https://github.com/dotnet/orleans/pull/5010), [#5011](https://github.com/dotnet/orleans/pull/5011))
  - Support for Tansfer of Coordination in transaction ([#4860](https://github.com/dotnet/orleans/pull/4860), [#4894](https://github.com/dotnet/orleans/pull/4894), [#4949](https://github.com/dotnet/orleans/pull/4949), [#5026](https://github.com/dotnet/orleans/pull/5026), [#5024](https://github.com/dotnet/orleans/pull/5024))

## [v1.5.6](https://github.com/dotnet/orleans/releases/tag/v1.5.6) September 27th 2018

Improvements and bug fixes since 1.5.5.

- Non-breaking improvements
  - Make MaxSockets in SocketManager configurable [#5033](https://github.com/dotnet/orleans/pull/5033).

## [v2.1.0-rc2](https://github.com/dotnet/orleans/releases/tag/v2.1.0-rc2) September 21st 2018

- Major changes
  - New Code Generator ([#4934](https://github.com/dotnet/orleans/pull/4934), [#5010](https://github.com/dotnet/orleans/pull/5010), [#5011](https://github.com/dotnet/orleans/pull/5011)).

## [v2.1.0-rc1](https://github.com/dotnet/orleans/releases/tag/v2.1.0-rc1) September 14th 2018

- Major changes
  - Transactions (beta2) ([#4851](https://github.com/dotnet/orleans/pull/4851), [#4923](https://github.com/dotnet/orleans/pull/4923), [#4951](https://github.com/dotnet/orleans/pull/4951), [#4950](https://github.com/dotnet/orleans/pull/4950), [#4953](https://github.com/dotnet/orleans/pull/4953))
  - Support for Transfer of Coordination in transaction ([#4860](https://github.com/dotnet/orleans/pull/4860), [#4894](https://github.com/dotnet/orleans/pull/4894), [#4949](https://github.com/dotnet/orleans/pull/4949))

## [v1.5.5](https://github.com/dotnet/orleans/releases/tag/v1.5.5) September 7th 2018

Improvements and bug fixes since 1.5.4.

- Non-breaking bug fixes
  - Fix programmatic subscribe bugs ([#4943](https://github.com/dotnet/orleans/pull/4943) - [#3843](https://github.com/dotnet/orleans/pull/3843))
  - Propagate message serialization errors to callers ([#4944](https://github.com/dotnet/orleans/pull/4944) - [#4907](https://github.com/dotnet/orleans/pull/4907))

- Breaking bug fixes
  - Add StreamSubscriptionHandleFactory to subscribe on behalf feature ([#4943](https://github.com/dotnet/orleans/pull/4943) - [#3843](https://github.com/dotnet/orleans/pull/3843)). While technically a breaking change, it only impacts users of the programmatic subscriptions feature that tried to use it with SMS stream by fixing that scenario (along with [#3843](https://github.com/dotnet/orleans/pull/3843)).

## [v2.0.4](https://github.com/dotnet/orleans/releases/tag/v2.0.4) August 7th 2018

- Non-breaking bug fixes
  - Use netcoreapp2.0 for msbuild target dll if using dotnet core msbuild but targeting full .net ([#4895](https://github.com/dotnet/orleans/pull/4895))

## [v2.1.0](https://github.com/dotnet/orleans/releases/tag/v2.1.0) August 28th 2018

- Major changes
  - New scheduler ([#3792](https://github.com/dotnet/orleans/pull/3792))
  - Hosted Client ([#3362](https://github.com/dotnet/orleans/pull/3362))
  - Distributed Transaction Manager (beta)([#3820](https://github.com/dotnet/orleans/pull/3820), [#4502](https://github.com/dotnet/orleans/pull/4502), [#4538](https://github.com/dotnet/orleans/pull/4538), [#4566](https://github.com/dotnet/orleans/pull/4566), [#4568](https://github.com/dotnet/orleans/pull/4568), [#4591](https://github.com/dotnet/orleans/pull/4591), [#4599](https://github.com/dotnet/orleans/pull/4599), [#4613](https://github.com/dotnet/orleans/pull/4613), [#4609](https://github.com/dotnet/orleans/pull/4609), [#4616](https://github.com/dotnet/orleans/pull/4616), [#4608](https://github.com/dotnet/orleans/pull/4608), [#4628](https://github.com/dotnet/orleans/pull/4628), [#4638](https://github.com/dotnet/orleans/pull/4638), [#4685](https://github.com/dotnet/orleans/pull/4685), [#4714](https://github.com/dotnet/orleans/pull/4714), [#4739](https://github.com/dotnet/orleans/pull/4739), [#4768](https://github.com/dotnet/orleans/pull/4768), [#4799](https://github.com/dotnet/orleans/pull/4799), [#4781](https://github.com/dotnet/orleans/pull/4781), [#4810](https://github.com/dotnet/orleans/pull/4810), [#4820](https://github.com/dotnet/orleans/pull/4820), [#4838](https://github.com/dotnet/orleans/pull/4838), [#4831](https://github.com/dotnet/orleans/pull/4831), [#4871](https://github.com/dotnet/orleans/pull/4871), 
[#4887](https://github.com/dotnet/orleans/pull/4887))

## [v2.0.4](https://github.com/dotnet/orleans/releases/tag/v2.0.4) August 7th 2018

Improvements and bug fixes since 2.0.3.

- Non-breaking bug fixes
  - Workaround for CoreFx/#30781 when running on .NET Core ([#4736](https://github.com/dotnet/orleans/pull/4736))
  - Fix for .NET Core 2.1 build-time code generation ([#4673](https://github.com/dotnet/orleans/pull/4673))

## [v1.5.4](https://github.com/dotnet/orleans/releases/tag/v1.5.4) June 13th 2018

## [v2.0.3](https://github.com/dotnet/orleans/releases/tag/v2.0.3) May 14th 2018

- This is a first patch release with a partial build -- only 9 NuGet packages got updated:
  - Microsoft.Orleans.OrleansRuntime
  - Microsoft.Orleans.OrleansServiceBus
  - Microsoft.Orleans.Runtime.Legacy
  - Microsoft.Orleans.OrleansCodeGenerator.Build
  - Microsoft.Orleans.Core.Legacy
  - Microsoft.Orleans.Transactions
  - Microsoft.Orleans.OrleansCodeGenerator
  - Microsoft.Orleans.Core
  - Microsoft.Orleans.TestingHost

The rest of the packages stayed unchanged at 2.0.0, except for the `Microsoft.Orleans.ServiceFabric` meta-package which is at 2.0.2.

## [v2.0.0](https://github.com/dotnet/orleans/releases/tag/v2.0.0) March 28th 2018

- Major changes (since 2.0.0-rc2)
  - All included providers obtain ServiceId and ClusterId from the global ClusterOptions and do not have those properties on their own options classes (#4235, #4277, 4290)
  - Use string for ServiceId instead of Guid (#4262)

## [v2.0.0-rc2](https://github.com/dotnet/orleans/releases/tag/v2.0.0-rc2) March 12th 2018

- Major changes (since 2.0.0-rc1)
  - A new "facade" API for easier configuration of various aspects of stream providers: Persistent stream configurators

## [v2.0.0-rc1](https://github.com/dotnet/orleans/releases/tag/v2.0.0-rc1) February 27th 2018

- Major changes (since 2.0.0-beta3)
  - New provider lifecycle model to replace the old one
  - Builder pattern and options-based configuration of components and extension

## [v2.0.0-beta3](https://github.com/dotnet/orleans/releases/tag/v2.0.0-beta3) December 21st 2017

## Community Virtual Meetup #15

[Orleans 2.0 with the core team](https://youtu.be/d3ufDsZcW4k) 
December 13th 2017
[Presentation](https://github.com/dotnet/orleans/blob/gh-pages/Presentations/VM-15%20-%20Orleans%202.0.pdf)

## [v2.0.0-beta2](https://github.com/dotnet/orleans/releases/tag/v2.0.0-beta2) December 12th 2017

## [v1.5.3](https://github.com/dotnet/orleans/releases/tag/v1.5.3) December 8th 2017

## [v2.0.0-beta1](https://github.com/dotnet/orleans/releases/tag/v2.0.0-beta1) October 26th 2017

- Major new features
  - Most packages are now targeting .NET Standard 2.0 (which mean they can be used from either .NET Framework or .NET Core 2.0) and on non-Windows platforms.

## [v1.5.2](https://github.com/dotnet/orleans/releases/tag/v1.5.2) October 17th 2017

## [v1.5.1](https://github.com/dotnet/orleans/releases/tag/v1.5.1) August 28th 2017

## [v1.5.0](https://github.com/dotnet/orleans/releases/tag/v1.5.0) July 6th 2017

- Major new features
  - Non-static grain client via ClientBuilder enables connecting to multiple Orleans cluster from the same app domain and connecting to other clusters from within a silo.
  - Support for versioning of grain interfaces for non-downtime upgrades.
  - Support for custom grain placement strategies and directors.
  - Support for hash-based grain placement.

## [v1.4.2](https://github.com/dotnet/orleans/releases/tag/v1.4.2) June 9th 2017

## [v1.4.1](https://github.com/dotnet/orleans/releases/tag/v1.4.1) March 27th 2017


## Community Virtual Meetup #14

[Orleans FSM](https://youtu.be/XmsVYLfNHjI) with [John Azariah](https://github.com/johnazariah)
March 22nd 2017


## [v1.4.0](https://github.com/dotnet/orleans/releases/tag/v1.4.0) February 21st 2017

- Major new features
  - Revamped JournaledGrain for event sourcing with support for geo-distributed log-based consistency providers.
  - Abstraction of Grain Services with fixed-placed per-silo application components with their workload partitioned via cluster consistency ring.
  - Support for heterogeneous silos with non-uniform distribution of available grain classes.
  - Cluster membership provider for Service Fabric.

## Community Virtual Meetup #13

[Upgrading Orleans Applications](https://youtu.be/_5hWNVccKeQ) with [Sergey Bykov](https://github.com/sergeybykov) and team
February 8th 2017
[Presentation](https://github.com/dotnet/orleans/raw/gh-pages/Presentations/VM-13%20-%20Orleans%20%26%20versioning.pptx)

## [v1.4.0-beta](https://github.com/dotnet/orleans/releases/tag/v1.4.0-beta) February 1st 2017

- Major new features
  - Revamped JournaledGrain for event sourcing with support for geo-distributed log-based consistency providers.
  - Abstraction of Grain Services with fixed-placed per-silo application components with their workload partitioned via cluster consistency ring.
  - Support for heterogeneous silos with non-uniform distribution of available grain classes.
  - Cluster membership provider for Service Fabric.

## Community Virtual Meetup #12

[Deploying Orleans](https://youtu.be/JrmHfbZH11M) with [Jakub Konecki](https://github.com/jkonecki)
December 8th 2016
[Presentation](https://github.com/dotnet/orleans/raw/gh-pages/Presentations/VM-12%20Orleans-YAMS.pdf)

## [v1.3.1](https://github.com/dotnet/orleans/releases/tag/v1.3.1) November 15th 2016

## Community Virtual Meetup #11

[A monitoring and visualisation show](https://youtu.be/WiAX_eGEuyo) with [Richard Astbury](https://github.com/richorama), [Dan Vanderboom](https://github.com/danvanderboom) and [Roger Creyke](https://github.com/creyke)
October 13th 2016

## [v1.3.0](https://github.com/dotnet/orleans/releases/tag/v1.3.0) October 11th 2016

## [v1.2.4](https://github.com/dotnet/orleans/releases/tag/v1.2.4) October 5th 2016

## [v1.3.0-beta2](https://github.com/dotnet/orleans/releases/tag/v1.3.0-beta2) September 27th 2016

- Notable new features
  - Support for geo-distributed multi-cluster deployments [#1108](https://github.com/dotnet/orleans/pull/1108/) [#1109](https://github.com/dotnet/orleans/pull/1109/) [#1800](https://github.com/dotnet/orleans/pull/1800/)
  - Added new Amazon AWS basic Orleans providers [#2006](https://github.com/dotnet/orleans/issues/2006)
  - Support distributed cancellation tokens in grain methods [#1599](https://github.com/dotnet/orleans/pull/1599/)

## Community Virtual Meetup #10

[The roadmap to Orleans 2.0 with the core team](https://youtu.be/_SbIbYkY88o)
August 25th 2016

## [v1.2.3](https://github.com/dotnet/orleans/releases/tag/v1.2.3) July 11th 2016

## [v1.2.2](https://github.com/dotnet/orleans/releases/tag/v1.2.2) June 15th 2016

## [v1.2.1](https://github.com/dotnet/orleans/releases/tag/v1.2.1) May 19th 2016

## [v1.2.0](https://github.com/dotnet/orleans/releases/tag/v1.2.0) May 4th 2016

## [v1.2.0-beta](https://github.com/dotnet/orleans/releases/tag/v1.2.0-beta) April 18th 2016

- Major improvements
  - Added an EventHub stream provider based on the same code that is used in Halo 5.
  - [Increased throughput by between 5% and 26% depending on the scenario.](https://github.com/dotnet/orleans/pull/1586)
  - Migrated all but 30 functional tests to GitHub.
  - Grain state doesn't have to extend `GrainState` anymore (marked as `[Obsolete]`) and can be a simple POCO class.
  - [Added support for per-grain-class](https://github.com/dotnet/orleans/pull/963) and [global server-side interceptors.](https://github.com/dotnet/orleans/pull/965)
  - [Added support for using Consul 0.6.0 as a Membership Provider.](https://github.com/dotnet/orleans/pull/1267)
  - [Support C# 6.](https://github.com/dotnet/orleans/pull/1479)
  - [Switched to xUnit for testing as a step towards CoreCLR compatibility.](https://github.com/dotnet/orleans/pull/1455)

## [v1.1.3](https://github.com/dotnet/orleans/releases/tag/v1.1.3) March 9th 2016

## Community Virtual Meetup #9
[Nehme Bilal](https://github.com/nehmebilal) and [Reuben Bond](https://github.com/ReubenBond) [talk about deploying Orleans](https://youtu.be/w__D7gnqeZ0) with [YAMS](https://github.com/Microsoft/Yams) and [Service Fabric](https://azure.microsoft.com/en-gb/documentation/articles/service-fabric-overview/)
Fabruary 26st 2016

## Community Virtual Meetup #8.5
[Networking discussion](https://youtu.be/F1Yoe88HEvg) hosted by [Jason Bragg](https://github.com/jason-bragg)
February 11th 2016

## Community Virtual Meetup #8
[Orleans core team present the roadmap](https://www.youtube.com/watch?v=4BiCyhvSOs4)
January 21st 2016

## [v1.1.2](https://github.com/dotnet/orleans/releases/tag/v1.1.2) January 20th 2016

## [v1.1.1](https://github.com/dotnet/orleans/releases/tag/v1.1.1) January 11th 2016

## [Community Virtual Meetup #7](https://www.youtube.com/watch?v=FKL-PS8Q9ac)
Christmas Special - [Yevhen Bobrov](https://github.com/yevhen) on [Orleankka](https://github.com/yevhen/Orleankka)
December 17th 2015

## [v1.1.0](https://github.com/dotnet/orleans/releases/tag/v1.1.0) December 14nd 2015

## Community Virtual Meetup #6
[MSR PhDs on Geo Distributed Orleansp](https://www.youtube.com/watch?v=fOl8ophHtug)
October 23rd 2015

## [v1.0.10](https://github.com/dotnet/orleans/releases/tag/v1.0.10) September 22nd 2015

## [v1.0.9](https://github.com/dotnet/orleans/releases/tag/v1.0.9) July 15th  2015

## [v1.0.8](https://github.com/dotnet/orleans/releases/tag/v1.0.8) May 26th  2015

## [Community Virtual Meetup #5](https://www.youtube.com/watch?v=eSepBlfY554)
[Gabriel Kliot](https://github.com/gabikliot) on the new Orleans Streaming API
May 22nd 2015

## [v1.0.7](https://github.com/dotnet/orleans/releases/tag/v1.0.7) May 15th  2015

## [Community Virtual Meetup #4](https://www.youtube.com/watch?v=56Xz68lTB9c)
[Reuben Bond](https://github.com/ReubenBond) on using Orleans at FreeBay
April 15th 2015

## [v1.0.5](https://github.com/dotnet/orleans/releases/tag/v1.0.5) March 30th  2015

## [Community Virtual Meetup #3](https://www.youtube.com/watch?v=07Up88bpl20)
[Yevhen Bobrov](https://github.com/yevhen) on a Uniform API for Orleans
March 6th 2015

## [Community Virtual Meetup #2](https://www.youtube.com/watch?v=D4kJKSFfNjI)
Orleans team live Q&A and roadmap
January 12th 2015

## Orleans Open Source v1.0 Update (January 2015)

## [Community Virtual Meetup #1](http://www.youtube.com/watch?v=6COQ8XzloPg)
[Jakub Konecki](https://github.com/jkonecki) on Event Sourced Grains
December 18th 2014
