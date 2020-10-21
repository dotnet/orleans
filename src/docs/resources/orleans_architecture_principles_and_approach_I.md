---
layout: page
title: Orleans Architecture - Principles and Approach I
---

Now that Orleans is (finally) available as open source, it's important to be clear about the goals and principles that has motivated the design decisions behind Orleans so that new changes either fit within that framework or explicitly and intentionally revise those goals and principles.

About the time I joined the Orleans project, we agreed that the goal was to produce a framework that would allow mainstream developers to easily build scalable distributed (cloud) applications. To break this down a bit:

* **The target audience shouldn't exclude programmers who haven't done distributed systems development**. We want to enable all developers, whether cloud experts or cloud beginners, to focus on their application logic and features -- which is to say, what actually provides business value -- rather than on generic distributed systems issues.

* **The goal is to allow them to build cloud applications easily**. Easily means that they shouldn't have to think about distribution any more than is absolutely required. Easily also means that Orleans should present as familiar a façade to the developer as possible; in a .NET context, that means C# objects and interfaces.

* **Those applications should be _"scalable by default"_**. Since our target users aren't necessarily distributed systems experts, we want to provide them a framework that will lead them to build scalable applications without explicitly thinking about it. This means that the framework has to make a lot of decisions for them in order to guarantee an acceptable degree of scalability, even if that means that the scalability isn't optimal for every application.


We supplemented this goal with a set of architectural principles:

* **We're focused on the 80% case**. There are certainly applications that Orleans isn't appropriate for; that's OK. There are applications that Orleans is a reasonable fit for, but where you can get somewhat better performance by a bunch of hand-tuning that Orleans doesn't allow; that's OK too. The 80% that Orleans fits well and performs well enough on covers a lot of interesting applications, and we'd rather do a great job on 80% than a lousy job on 99%.


* **Scalability is paramount**. We'll trade off raw performance if that gets us better scaling.


* **Availability is paramount**. A cloud application should be like a utility: always there when you want it.


* **Detect and fix problems**, don't assume you can 100% prevent them. At cloud scale, bad things happen often, and even impossible bad things happen, just less often. This has led us to what is often termed "recovery-oriented computing", rather than trying to be fault-tolerant; our experience has shown that fault tolerance is fragile and often illusory. Even mathematically proven protocols are no protection against random bit flips in memory or disk controllers that fail while reporting success -- both real examples I've seen in production in my career.


The above has led us to certain practices:

* **API-first design**: if we don't know how we're going to expose a feature to the developer, then we don't build it. Of course, the best way is for a feature have no developer exposure at all...

* **Make it easy to do the right thing**: keep things as simple as possible (but no simpler), don't provide a hammer if a screwdriver is the right tool. As one of our early adopters put it, we try to help our customers "fall into the pit of success". If there is a standard pattern that will work well for 80% of the applications out there, then don't worry about enabling every possible alternative. Orleans' embrace of asynchrony is a good example of this.

* **Make it easy for developers to extend the framework without breaking it**. Custom serialization and persistence providers are a couple of examples of this. Some sort of custom task scheduling extension would be an anti-example.

* **Follow the principle of least surprise**: as much as possible, things should be as familiar, but everything should behave the way it looks.


The next post will start applying these principles to the current Orleans design and walk through the motivations for some specific decisions we made.

Thanks for reading!

Alan Geller

_Alan Geller, http://research.microsoft.com/en-us/people/ageller/, works on quantum computing at Microsoft Research. He was one of the primary architects of Orleans from 2008 until 2012. Earlier, he was the platform architect for Amazon Web Services from 2004 to 2008, and before that built a wide variety of large-scale production distributed systems in telecommunications and financial services._



