Latest release - 1.3.1
======================

[Sergey Bykov](https://github.com/sergeybykov)
12/1/2016 5:48:39 PM

* * * * *

On November 15th we published our [latest release - 1.3.1](https://github.com/dotnet/orleans/releases/tag/v1.3.1).
It is a patch release with a number of bug fixes and improvements that have been merged into master since 1.3.0.
There were two main reasons for 1.3.1.

- 343 Industries needed a release with a couple of improvements to streaming and the EventHub stream provider to simplify their migration from the pre-released version of the streaming stack they've been running since before Halo 5 launch.
- [Orleankka](https://github.com/OrleansContrib/Orleankka) needed a rather advanced feature that would allow them to control interleaving of requests on a per-message basis.
[@yevhen](https://github.com/yevhen)[submitted a PR](https://github.com/dotnet/orleans/pull/2246) for that after a few design and implementation iterations.

So, 1.3.1 isn't a pure patch release because it includes a new feature.
We thought it was okay here because of how non-impactful to others the feature really is.

If you are upgrading to 1.3.1 from a 1.2.x or earlier release, beware of the subtle breaking change that was made in 1.3.0.
The [1.3.0 release notes](https://github.com/dotnet/orleans/releases/tag/v1.3.0) called it out:

**NB: There is a subtle breaking change in this release, which is
unfortunately easy to miss.**

*If you are using `AzureSilo.Start(ClusterConfiguration config, string deploymentId)` in your code, that overload has been removed, but the new one that replaced it has the same argument signature with a different
second argument: `(ClusterConfiguration config, string connectionString)`.
Deployment ID now has to be passed as part of the config argument:config.Globals.DeploymentId.
This removed the ambiguous possibility of passing two different Deployment IDs, but unfortunately at the cost of the breaking API change.*

1.3.0 was a pretty big release with numerous improvements, bug fixes, and the major new feature of geo-distributed multi-clusters.
Most of its content is listed in the [1.3.0-beta2 release notes](https://github.com/dotnet/orleans/releases/tag/v1.3.0-beta2).
The geo-distribution functionality is described in the [Multi-Cluster Support section](http://dotnet.github.io/orleans/Documentation/Multi-Cluster/Overview.html) of the docs.
