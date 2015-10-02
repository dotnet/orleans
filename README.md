Orleans - Distributed Actor Model
=======

![Orleans logo](https://github.com/dotnet/orleans/blob/gh-pages/Icons/Orleans/OrleansSDK_128x.png)


[![Build status](http://dotnet-ci.cloudapp.net/job/dotnet_orleans/job/innerloop/badge/icon)](http://dotnet-ci.cloudapp.net/job/dotnet_orleans/job/innerloop)
[![NuGet](https://img.shields.io/nuget/v/Microsoft.Orleans.Core.svg?style=flat)](http://www.nuget.org/profiles/Orleans)
[![Issue Stats](http://www.issuestats.com/github/dotnet/orleans/badge/pr)](http://www.issuestats.com/github/dotnet/orleans)
[![Issue Stats](http://www.issuestats.com/github/dotnet/orleans/badge/issue)](http://www.issuestats.com/github/dotnet/orleans)

[![Gitter](https://badges.gitter.im/Join%20Chat.svg)](https://gitter.im/dotnet/orleans?utm_source=badge&utm_medium=badge&utm_campaign=pr-badge)

[![Help Wanted Issues](https://badge.waffle.io/dotnet/orleans.svg?label=help%20wanted&title=Help Wanted Issues)](http://waffle.io/dotnet/orleans)

Orleans is a framework that provides a straight-forward approach to building distributed high-scale computing applications, without the need to learn and apply complex concurrency or other scaling patterns. 
It was created by [Microsoft Research][MSR-ProjectOrleans] and designed for use in the cloud. 
Orleans has been used extensively running in Microsoft Azure by several Microsoft product groups, most notably by 343 Industries as a platform for all of Halo 4 cloud services, as well as by [a number of other projects and companies](http://dotnet.github.io/orleans/Who-Is-Using-Orleans).

Installation
=======
The stable production-quality release is located [here](https://github.com/dotnet/orleans/releases/latest).

The latest clean development branch build from CI is located: [here](http://dotnet-ci.cloudapp.net/job/dotnet_orleans/job/innerloop/lastStableBuild/artifact/)

Alternatively, you can clone the sources from GitHub, and run the Build.cmd script to build Binaries locally. 
Then just xcopy Binaries\Release\* or reference Binaries\NuGet.Packages\* or install Binaries\Release\orleans_setup.msi.

Documentation 
=======
Documentation is located [here][Orleans Documentation]

Contributing To This Project
=======

* List of [Ideas for Contributions]

* [Contributing Guide]

* [CLA - Contribution License Agreement][CLA]

* The coding standards / style guide used for Orleans code is the [.NET Framework Design Guidelines][DotNet Framework Design Guidelines]

* [Orleans Community - Repository of community add-ons to Orleans](https://github.com/OrleansContrib/) Various community projects, including Orleans Monitoring, Design Patterns, Storage Provider, etc.

You are also encouraged to start a discussion by filing an issue.

License
=======
This project is licensed under the [MIT license](https://github.com/dotnet/orleans/blob/master/LICENSE).


[MSR-ProjectOrleans]: http://research.microsoft.com/projects/orleans/
[Orleans Documentation]: http://dotnet.github.io/orleans/
[Ideas for Contributions]: http://dotnet.github.io/orleans/Ideas-for-Contributions
[Contributing Guide]: https://github.com/dotnet/corefx/wiki/Contributing
[CLA]: https://github.com/dotnet/corefx/wiki/Contribution-License-Agreement-%28CLA%29
[DotNet Framework Design Guidelines]: https://github.com/dotnet/corefx/wiki/Framework-Design-Guidelines-Digest
[Download Link]: http://orleans.codeplex.com/releases/view/144111
