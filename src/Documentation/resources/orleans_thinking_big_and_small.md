---
layout: page
title: Orleans - Thinking Big and Small
---



TL;DR: You don’t need hundreds of servers to benefit from Orleans. A handful is enough.

As we just announced availability of the Project Orleans as a public preview ([Available Now: Preview of Project “Orleans” – Cloud Services at Scale](http://blogs.msdn.com/b/dotnet/archive/2014/04/02/available-now-preview-of-project-orleans-cloud-services-at-scale.aspx)), some of the initial questions and discussions at //build/ were around what type of services the Orleans programming model is suitable for. I heard statements that Orleans is for super-high scale systems. While technically correct, they felt incomplete to me, and compelled me to write this post.

## Applicability Spectrum

One extreme of the Orleans applicability spectrum is a single machine application. Some people see isolation of actors and safe concurrency as big enough benefits that they are worth the price of message passing. That’s not the use case we optimized for while building Orleans, but it’s a legitimate usage pattern, just not very interesting in the cloud context.

At the other end of the spectrum we find massive deployments that span thousands of servers. We tested Orleans on deployments of hundreds of servers, and I’m sure it will run fine on thousands, even if that will require some configuration tweaks. However, I’m personally rather skeptical about how many products actually need a single cloud service spanning thousands of servers as opposed to running multiple related services interacting with each other where each service instance is deployed on tens or hundreds of servers.

## Distributed system – same problems regardless of the size

The moment a system moves from a single server to multiple servers, developers face very much the same set of challenges, regardless of its size -- whether it's a 3-5, 30-50 or 300-500 server system. They now have to deal with distribution of their computations, coordination between them, scalability, fault tolerance and reconfigurations, diagnostics, etc. They are building a distributed system now, which is never easy. And building a stateful distributed system is even harder.

Orleans was designed to help with building such systems by providing an easy to use set of abstractions that greatly simplifies developers’ lives and helps them avoid common distributed systems pitfalls. The distributed runtime was built to perform most of the heavy lifting. Developers can equally benefit from these features of Orleans when building services of different sizes because the problems Orleans solves for them are really the same. The abstraction of grains simplifies reasoning about your system while encouraging fine-grain partitioning of state for scalability. The virtual actor feature helps with resource management and fault tolerance. Automatic propagation of exceptions minimizes error handling code without losing failures. The distributed runtime takes care of server failures, messaging, routing, single-threaded execution, and other system level guarantees. You don’t need hundreds of servers to start reaping the developer productivity gains.

## Elasticity

Predicting load for your future system is hard and often simply impossible. Every startup dreams of getting slashdotted one day, which is a blessing for the business and a curse for the system. Or your CMO may like your BI data so much that she suddenly wants to have it in 5 second aggregates instead of 30 minutes. Building for high load that may never materialize is expensive. The Orleans model helps solve the elasticity problem by encouraging designing your system in a scalable way, so that you can start with a small deployment and stay small or scale out big if needed without changing your application code.

## Bottom Line
You should be able to benefit from Orleans the moment you go from a single-server setup to a distributed system, whether it’s 2 or 3 servers or thousands of them. You can even start building your single-server solution with Orleans if you believe you may need one day scale it out or make fault tolerant. The beauty here is that your code won’t need to change. Just add more servers and your grains will spread across them.

 -Sergey Bykov
