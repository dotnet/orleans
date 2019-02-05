---
layout: page
title: Orleans Blog
---

## [Solving a Transactions Performance Mystery](solving-a-transactions-performance-mystery.md)
[Reuben Bond](https://github.com/ReubenBond)
12/7/2018 10:08:58 AM

* * * * *

After arriving in Redmond and completing the mandatory New Employee Orientation, my first task on the Orleans team has been to assist with some ongoing performance investigations in order to ensure that Orleans' Transactions support is ready for internal users and hence release.

We were seeing significant performance issues and a large number of transaction failures in the stress/load tests against our test cluster.
A large fraction of transactions were stalling until timeout.


## [Dmitry Vakulenko](dmitry-vakulenko.md)
[Sergey Bykov](https://github.com/sergeybykov) 11/19/2018 1:57:59 PM

* * * * *

Dmitry Vakulenko joined the Orleans open source community three years ago, and started submitting pull requests that focused on improving performance of the Orleans codebase.
He became the most prolific contributor outside of the current and former members of the core team.

Dmitry also contributed other improvements, but his passion continued to be performance.
Because of the compound nature of incremental optimizations, over time these improvements added up to a staggering aggregate factor. Our conservative estimate is that Dmitry's contributions combined increased performance of Orleans by about 2.6 times.


## [Announcing Orleans 2.1](announcing-orleans-2.1.md)

[Reuben Bond](https://github.com/ReubenBond) 10/1/2018 7:17:59 PM

* * * * *

Today, we announced Orleans 2.1.
This release includes significant performance improvements over 2.0, a major refresh of distributed transaction support, a new code generator, and new functionality for co-hosting scenarios as well as smaller fixes & improvements.
Read the [release notes here](https://github.com/dotnet/orleans/releases/tag/v2.1.0).
