---
layout: page
title: Ideas for Contributions
---

# Ideas for Contributions

These is an initial list of some ideas for contributing to Orleans.
It is meant to be a live document.
If you are interested in any of these, or one that is not listed, create an issue to discuss it.

We put them into 3 size categories based roughly on our gut feel, which may be wrong:
 * Small - hours of work
 * Medium - couple of days of work
 * Large - big projects, multiple days up to weeks of work

1. **Project template/wizard for Azure deployment** [Medium/Large]
  * Worker role for silos (in our experience it is better to start a silo as a standalone process and wait on the process handle in the worker roles code)
  * Worker/Web role for frontend clients
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
  * Some APIs from the full .NET got deprecated in coreclr, mainly around files and reflection, but at large the porting effort shouldn't be too big. 
This will allow Orleans to run efficiently cross-platform.

6. **Secure communication between silos and clients**
  * Add support for secure communication mode with certificates used for encryption of messages.