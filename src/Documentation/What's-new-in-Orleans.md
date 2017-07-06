---
layout: page
title: What's new in Orleans
---

# What's new in Orleans?

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

* Notable new features
  * Support for geo-distributed multi-cluster deployments [#1108](https://github.com/dotnet/orleans/pull/1108/) [#1109](https://github.com/dotnet/orleans/pull/1109/) [#1800](https://github.com/dotnet/orleans/pull/1800/)
  * Added new Amazon AWS basic Orleans providers [#2006](https://github.com/dotnet/orleans/issues/2006)
  * Support distributed cancellation tokens in grain methods [#1599](https://github.com/dotnet/orleans/pull/1599/)


## Community Virtual Meetup #10

[The roadmap to Orleans 2.0 with the core team](https://youtu.be/_SbIbYkY88o)
August 25th 2016


## [v1.2.3](https://github.com/dotnet/orleans/releases/tag/v1.2.3) July 11th 2016


## [v1.2.2](https://github.com/dotnet/orleans/releases/tag/v1.2.2) June 15th 2016


## [v1.2.1](https://github.com/dotnet/orleans/releases/tag/v1.2.1) May 19th 2016


## [v1.2.0](https://github.com/dotnet/orleans/releases/tag/v1.2.0) May 4th 2016


## [v1.2.0-beta](https://github.com/dotnet/orleans/releases/tag/v1.2.0-beta) April 18th 2016

* Major improvements
  * Added an EventHub stream provider based on the same code that is used in Halo 5.
  * [Increased throughput by between 5% and 26% depending on the scenario.](https://github.com/dotnet/orleans/pull/1586)
  * Migrated all but 30 functional tests to GitHub.
  * Grain state doesn't have to extend `GrainState` anymore (marked as `[Obsolete]`) and can be a simple POCO class.
  * [Added support for per-grain-class](https://github.com/dotnet/orleans/pull/963) and [global server-side interceptors.](https://github.com/dotnet/orleans/pull/965)
  * [Added support for using Consul 0.6.0 as a Membership Provider.](https://github.com/dotnet/orleans/pull/1267)
  * [Support C# 6.](https://github.com/dotnet/orleans/pull/1479)
  * [Switched to xUnit for testing as a step towards CoreCLR compatibility.](https://github.com/dotnet/orleans/pull/1455)


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
