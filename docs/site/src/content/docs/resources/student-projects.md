---
title: Student projects
description: Learn how the Orleans team encourages students to build distributed applications.
ms.date: 07/03/2024
---

# Student projects

We suggest two types of projects for students:

1. The first type includes exploratory, open-ended, research-oriented projects to enable new capabilities in Orleans. These projects would usually have a broad scope and would be suitable for M.S. or Ph.D. students or advanced undergraduate students in their last year of studies. The end goal of these projects would be to contribute ideas and design to Orleans. We do not necessarily expect the code produced in these projects to be directly contributed to this repository, however, this would be nice.

1. The second type includes ideas for student education. These are either ideas for interesting applications that can be built on top of Orleans or some new capabilities for Orleans. These projects are suitable to be given in advanced undergraduate or graduate courses, where students learn about Cloud Computing and modern distributed technologies and want to gain real-world hands-on experience in building Cloud applications. We do not expect the code produced in these projects to be contributed directly to this repository.

## Research projects

1. **Auto-scale**: In this project, students can start by exploring the existing autoscaling mechanisms for controlling resource allocation in Azure [Autoscaling](/azure/architecture/best-practices/auto-scaling). The next step involves exploring various statistics and resource consumption metrics collected by Orleans and using them as an input for Azure Autoscaling. An advanced stage of this project may involve improving the internal Orleans mechanisms for reacting to elasticity changes, for example by implementing live actor migration to reduce the time taken to utilize new resources.

1. **Auto-generated front-ends for Orleans-based cloud services**: This project seamlessly extends the Orleans actor model into the HTTP world. The ramp-up part of the project includes dynamically generating HTTP endpoints for actors based on their .NET interfaces and metadata. The main part involves automatically generating front-ends to support web sockets and bi-directional streaming of data, which requires complex code generation with optimizations for high performance. It also requires attention to fault tolerance, to maintain the high availability of streaming sessions across server reboots and client reconnects and migration&mdash;a significant research challenge.

1. **Storage provider for Entity Framework**: This project involves enabling Orleans objects to store their state in a database and to subsequently query it. This might include adding support for Orleans object persistence on SQL Azure Database using Entity Framework (EF), which is Microsoft's open-source object-relational mapper for .NET, and exposing that data via LINQ queries. The implementation can be evaluated and tuned using standard database benchmarks and/or custom Orleans' applications.

1. **Distributed system benchmark**: Define a list of benchmarks suitable for distributed systems like Orleans. The benchmark applications may be analogous in spirit to the [TPC database benchmark](http://www.tpc.org/) or UC Berkeley "Parallel Dwarfs", and may be used to characterize the performance and scalability of distributed frameworks. Consider developing a new benchmark targeted for Orleans, for example, to compare the performance of storage providers.

1. **Declarative dataflow language over streams**: Define and build a [Trident-Storm](https://storm.apache.org/documentation/Trident-tutorial.html) like declarative language over Orleans streams. Develop an optimizer that configures the stream processing to minimize the overall cost.

1. **Programming model for client devices**: Extend Orleans to client devices, such as sensors, phones, tablets, and desktops. Enable grain logic to execute on the client. Potentially support tier splitting, that is, dynamically deciding which parts of the code execute on the device and which is offloaded to the cloud.

1. **Queries over grain/actor classes, secondary indices**: Build a distributed, scalable, and reliable grain index. This includes formally defining the query model and implementing the distributed index. The index itself can be implemented as Orleans grains and/or stored in a database.

1. **Large scale simulations**: Orleans is a great fit for building large-scale simulations. Explore the usage of Orleans for different simulations, for example, protein interactions, network simulations, simulated annealing, etc.

## Course projects

1. **Internet of Things applications**: For example, the application could enable sensors/devices to report their state to the cloud, where each device is represented in the cloud by an Orleans actor. Users can connect to the actor that represents their device via a web browser and check its status or control it. This project involves mastering several modern cloud technologies, including [Azure](https://azure.microsoft.com), Orleans, [Minimal web API](/aspnet/core/tutorials/min-web-api), [ASP.NET Core SignalR](/training/modules/aspnet-core-signalr) for streaming commands back from the cloud to the device, and writing a sensor/device/phone app.

1. **Twitter-like large scalable chat service in the cloud based on Orleans**: Each user could be represented by an Orleans Actor, which contains its list of followers.

1. **Facebook-like social app based on Orleans**: Each user could be represented by an Orleans Actor, which includes a list of friends and a wall on which friends can write.

1. **Simple storage provider**: Add a storage provider for a storage system, such as a key-value store or database system. A simple one could use the [Orleans serializer](xref:Orleans.Serialization), as in the existing [Azure Table storage provider](xref:Orleans.Storage.AzureTableStorage). A more sophisticated one would map state variables of an Orleans class to fine-grained structures of the storage system. A complex one is the Entity Framework storage provider mentioned above under _Research Projects_. Compare the performance of different storage providers for different types and sizes of actor states.

1. **Comparison with other distributed application frameworks**: Take a sample application written for another application framework, such as [Google App Engine](https://cloud.google.com/appengine/docs) or [Akka](https://akka.io/), and translate it into Orleans. Summarize the relative strengths and weaknesses of each framework by comparing the apps.

## Concluded research projects

Below are several examples of previous successful research projects:

1. **Distributed log analysis, correlation, and debugging**: Debugging large-scale distributed systems is a challenging task due to enormous amounts of data and complex dynamic interactions between the distributed components, running on different processes and different machines. The goal of this project was to analyze prior art on this topic, propose a solution, and then implement prototype tools for collecting, correlating, and analyzing application error log file data across a multi-machine distributed application runtime environment. This involved exploring the problem space from a variety of perspectives, including:

    1. Approaches to efficient logging, collection, and analysis of failure information from various log-capture mechanisms in a distributed Orleans runtime environment.

    1. Possible applications of machine learning to find log patterns that signal serious production issues, and then detect these patterns in near real-time as a production monitoring utility.

    1. Ways to help individual developers perform real-time debugging of run-time issues with their applications.

    This project was performed successfully and resulted in a published paper [PAD: Performance Anomaly Detection in Multi-Server Distributed Systems](https://research.microsoft.com/apps/pubs/?id=217109) and a proof of concept implementation of a distributed log analysis tool.

1. **Horton - Distributed Graph Database**: Horton was a research project to build a system to store, manage and query large-scale distributed graphs. It was implemented entirely as an Orleans application. The project resulted in some [publications](https://research.microsoft.com/projects/ldg/) and several very successful student projects.
