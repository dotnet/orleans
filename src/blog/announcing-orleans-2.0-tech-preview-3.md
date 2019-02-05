Announcing Orleans 2.0 Tech Preview 3
=====================================

[Julian Dominguez](https://github.com/jdom)
9/13/2017 1:17:21 PM

* * * * *

We just released a big update on the Orleans 2.0 tech preview train.
The new binaries now target .NET Standard 2.0, so they have almost full parity with the .NET version of Orleans.
This means that the current state is not only functional for people writing new applications in .NET Core, but also as an upgrade path for people with applications already running in .NET Framework.

Are we done yet?
----------------

This is an exciting milestone for our team, as it brings the 2.0 release a lot closer.
There are a few remaining things we would like to do for this version before we release, such as:

- Enable build-time codegen for .NET Core apps (only runtime codegen is supported for .NET Core still)
- Migrate to [Microsoft.Extensions.Logging](https://www.nuget.org/packages/Microsoft.Extensions.Logging/) for all of our logging
- Flesh out the silo builder APIs more
- Improve startup by scanning only the assemblies defined by the end user
- [Restructure](https://github.com/dotnet/orleans/issues/3353) some of our types to have an Abstractions project that we don't version often
- Have an initial version of the Transactions feature working
- Provide a level of backwards compatibility for types commonly serialized and persisted to storage
- and some other minor things

How can I try this?
-------------------

We just published the packages to MyGet: [https://dotnet.myget.org/gallery/orleans-ci](https://dotnet.myget.org/gallery/orleans-ci)

Please follow the link for instructions on how to configure NuGet to download packages from that feed.

Please help us
--------------

We plan to start releasing updated packages to the MyGet feed a lot more often, so please try the preview, and if there's issues, let us know, so that we can fix it and send an update your way shortly after.

Is Orleans 2.0 TP3 production ready?
------------------------------------

Not yet. 
Big disclaimer: We do our CI testing in .NET (because our tests heavily rely on AppDomains to create an in-memory cluster of silos, and those are not supported in .NET Core, but we plan to tackle that soon).
We have done some basic manual testing in .NET Core (both Windows and Linux), and we have some of our contributors using it to develop new services.
Getting feedback (and PRs!) is one of the main goals of this release, and not to be used in production yet.
Also, there is no guarantee that this technical preview is entirely backwards compatible with Orleans 1.5, even for the features that were fully ported.
Once we are closer to a stable release, we’ll list all the breaking changes that we know of in case you are interested in upgrading your application from 1.X to 2.0.

Notes
-----

If you were using an older tech preview, notice that assembly loading changed a little, and we expect to continue to change it.
In the meantime, please note that in .NET Core you might be required to publish your app so that all the assemblies to scan are in the same folder (you can achieve this by calling \`*dotnet publish*\` on your silo host).
