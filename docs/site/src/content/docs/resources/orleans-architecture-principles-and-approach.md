---
title: Orleans architecture design principles
description: Explore the architecture design principles for .NET Orleans.
ms.date: 07/03/2024
---

# Orleans architecture design principles

Orleans is an open-source project, and it's important to be clear about the goals and principles. These goals and principles have motivated the design decisions behind Orleans so that new changes either fit within that framework or explicitly and intentionally revise those goals and principles.

The goal for the Orleans project was to produce a framework that would allow mainstream developers to easily build scalable distributed (cloud) applications. To break this down a bit:

- The target audience shouldn't exclude programmers who haven't done distributed systems development. We want to enable all developers, whether cloud experts or cloud beginners, to focus on their application logic and features&mdash;which is to say, what provides business value&mdash;rather than on generic distributed systems issues.

- The goal is to allow mainstream developers to build cloud applications easily. *Easily* means that they shouldn't have to think about distribution any more than is required. *Easily* also means that Orleans should present as familiar a facade to the developer as possible; in a .NET context, that means C# objects and interfaces.

- Those applications should be _scalable by default_. Since the target users aren't necessarily distributed systems experts, we want to provide them with a framework that leads them to build scalable applications without explicitly thinking about it. This means that the framework has to make a lot of decisions for them to guarantee an acceptable degree of scalability, even if that means that the scalability isn't optimal for every application.

We supplemented this goal with a set of architectural principles:

- We're focused on the 80% case. There are certain applications that Orleans isn't appropriate for; that's OK. There are applications that Orleans is a reasonable fit for, but where you can get somewhat better performance by a bunch of hand-tuning that Orleans doesn't allow; that's OK too. The 80% that Orleans fits well and performs well enough on covers a lot of interesting applications, and we'd rather do a great job on 80% than a lousy job on 99%.

- Scalability is paramount. We'll trade off raw performance if that gets us better scaling.

- Availability is paramount. A cloud application should be like a utility: always there when you want it.

- Detect and fix problems, don't assume you can 100% prevent them. At cloud scale, bad things happen often, and even impossible bad things happen, just less often. This has led us to what is often termed "recovery-oriented computing", rather than trying to be fault-tolerant. Experience has shown that fault tolerance is fragile and often illusory. Even mathematically proven protocols don't protect against random bit flips in memory or disk controllers that fail while reporting success&mdash;both examples that have occurred in the real world.

The previous principles have led us to certain practices:

- API-first design: if we don't know how we're going to expose a feature to the developer, then we don't build it. Of course, the best way is for a feature to have no developer exposure at all.

- Make it easy to do the right thing: keep things as simple as possible (but no simpler), don't provide a hammer if a screwdriver is the right tool. As one of our early adopters put it, we try to help our customers "fall into the pit of success". If there is a standard pattern that will work well for 80% of the applications out there, then don't worry about enabling every possible alternative. Orleans' embrace of asynchrony is a good example of this.

- Make it easy for developers to extend the framework without breaking it. Custom serialization and persistence providers are a couple of examples of this. A custom task scheduling extension would be an anti-example.

- Follow the principle of least surprise: as much as possible, things should be as familiar, but everything should behave the way it looks.
