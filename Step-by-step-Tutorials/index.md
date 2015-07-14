---
layout: page
title: Step by Step Tutorials
---
{% include JB/setup %}

The collection of technology walkthrough tutorials found here is intended to introduce you to the features of this exciting technology, spanning the programming model, its configuration, deployment, and customization. 
They build on each other, so it is best to go through them in the order outlined below.

Each walkthrough is focused narrowly on just a couple of specific concepts in isolation from the scenarios that motivate your using Orleans in the first place, they are intended to teach you the mechanics of Orleans, not to explain when or why you should be using them.

## [My First Orleans Application](My-First-Orleans-Application)

This walkthrough shows you how to create a "Hello World" application using Orleans and run it in the simplest possible single-process environment, one that is convenient for debugging your code. 

## [Running in a Stand-Alone Silo](Running-in-a-Stand-alone-Silo)

In this walkthrough, the simple "Hello World" application is modified to use a more typical environment for services: separate processes for the client and service code. 
It is still a development and debugging environment, simpler than a production configuration, which would involve multiple processes on multiple computers.

## [Actor Identity](Actor-Identity)

Actors are a lot like regular objects, but there are a couple of quirks that make them different. 
One is the notion of actor identity, which surfaces in Orleans in the form of grains' primary keys.

## [A Service is a Collection of Communicating Actors](A-Service-is-a-Collection-of-Communicating-Actors)

The previous examples used only a single actor type and instance to demonstrate their concepts.
In almost all real systems, this is the opposite of what you would want to do; actors are intended to be as light-weight as objects and you would expect hundreds of thousands or millions of them to be active on a single system simultaneously, with as potentially billions waiting inactive in persistent store.

This walkthrough explains the actor lifecycle and identity, how actors are activated and deactivated.

## [Concurrency](Concurrency)

What distinguishes the actor model from most other (distributed) object models is that it enforces a specific set of rules for concurrent access to state, allowing it to be free of data races by exchanging data between actors using message-passing and only allowing a single thread of execution to access each actor's internal state at any given point in time.

On the other hand, there are many situations where data races are not a risk, and the single-threaded model is too conservative. 
Further, single-threaded execution can cause other problems, such as deadlocks. 
Orleans offers a few tools that allow developers to control this behavior, explained in this walkthrough.

## [Interaction with Libraries and Services](Interaction-with-Libraries-and-Services)

Applications using Orleans are regular .NET applications, and can interact freely with other .NET components. 
In order to not undermine the scalability inherent in the actor model, programmers have to take care to follow a few rules, mostly related to using asynchronous APIs whenever they are available. 
This walkthrough demonstrates the basic principles.

## [Declarative Persistence](Declarative-Persistence)

Actors are often transient, i.e., their state lives only for a short period of time, or is reconstructible at will from other actors. 
In many circumstances, however, actor state needs to persist for longer periods of time and be stored in an external database of some sort. 
Orleans offers the developer flexible options on where to store actor state, and this walkthrough will introduce the simplest way to deal with long-lived actor state: declaratively.

## [Front Ends for Orleans Services](Front-Ends-for-Orleans-Services)

Many Orleans services will be private and available only to front-end services that rely on the Orleans-based code as one of several backend services.
In some circumstances, what is needed is to put a thin HTTP layer in front of the backend service, essentially making the Orleans service itself publically available via HTTP. 
In this walk-through, the steps of producing a thin HTTP layer based on ASP.NET Web API is described.

## [Cloud Deployment](Cloud-Deployment)

The next walkthrough demonstrates how to get your Orleans application deployed in the cloud using Azure.

## [On-Premise Deployment](On-Premise-Deployment)

Orleans applications can be deployed both on your server equipment as well as in the cloud. 
In this walkthrough, you will see how to set up an on premise cluster and deploy your application to it.

## [Custom Storage Providers](Custom-Storage-Providers)

Defining your own storage provider is the easiest way to extend the persistence choices of your Orleans application. 
While Orleans comes with a couple of storage providers in the box, they are not intended to be the only choices or constrain you unnecessarily. 
Therefore, the library offers a way to extend the set of storage providers to include This walkthrough demonstrates how to extend the choices by building a storage provider based on regular files.
