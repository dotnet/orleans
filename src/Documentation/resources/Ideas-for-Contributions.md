---
layout: page
title: Ideas for Contributions
---

# Ideas for Contributions

These are some of the ideas for contributing to Orleans. Just an initial list for consideration, meant to be a live document. If you are interested in any of these or one that is not listed, create an issue to discuss it.

We roughly put them into 3 size categories based on our gut feel, which may be wrong:
 * Small - hours of work
 * Medium - couple of days of work
 * Large - big projects, multiple days up to weeks of work

1. **Project template/wizard for Azure deployment** [Medium/Large]
  * Worker role for silos (in our experience it is better to star a silo as a standalone process and wait on the process handle in the worker roles code)
  * Worker/web role for frontend clients
  * Configuration of diagnostics, ETW tracing, etc.
  * Try Azure SDK plug-in as suggested [here](http://richorama.github.io/2015/01/13/thoughts-on-deploying-orleans/) by @richorama.

2. **Cluster monitoring dashboard** [Medium]
  * https://github.com/OrleansContrib/OrleansMonitor may be a good start

3. **Proper support for F#** [Medium/Large]
See [Issue #38](https://github.com/dotnet/orleans/issues/38)

4. **Orleans backplane for SignalR** [Medium]
See [Issue #73](https://github.com/dotnet/orleans/issues/73)

5. **Port Orleans to [coreclr](https://github.com/dotnet/coreclr)** [Medium]
See [Issue #368](https://github.com/dotnet/orleans/issues/368)
  * Some APIs from the full .NET got deprecated in coreclr, mainly around files and reflection, but at large the porting effort shouldn't be too big. This will allow to run Orleans efficiently cross platform.

6. **Secure communication between silos and clients**
  * Add support for secure communication mode with certificates used for encryption of messages.