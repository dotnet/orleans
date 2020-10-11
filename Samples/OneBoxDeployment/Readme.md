# A sample on developing and testing a reliable Orleans cluster locally

This sample shows how one can develop, test and deploy a cluster in reliable configuration locally.

Orleans is a building block to develop smart, resilient software-intensive cyber-physical systems. Such software systems do not exist in isolation and can fail in many ways, sometimes for technical reasons, because of reasons of the business context in which they operate or even due to socio-economic reasons. The purpose of this sample is to show one way of building a system that allows the developing team to build, debug, test and learn, even "feel" in controlled, everyday working way how their system as a whole functions and behaves and learn its logical limits. This should increase confidence within the team that they understand the limits and can pin-point and solve problems quickly when they eventually emerge. Consequently this enhances the communication with the stakeholders and increase the probability of success of a given projects or via enhanced learning the future projects.

Cyber-physical systems are often large, complex and dynamic, need to cope with external threats and optimize their behaviour and the software reflects this reality. It is likely cost and time prohibitive to asses risk of components in separation if possible at all. Also it is not possible to prepare and prevent foreseeable events, but it is possible to build resilience in systems and to the teams building such systems so they can quickly recover by locating faults and adapting. This should in turn instil confidence to communications when faced with time limits or address [cross-cutting concerns](https://en.wikipedia.org/wiki/Cross-cutting_concern) and consequently increase project success factor. Actor systems such as Orleans can make this easier.

There are excellent general resources about complex systems development in Internet. Prominent examples are [Resilience In Complex Adaptive Systems](https://www.youtube.com/watch?v=PGLYEDpNu60) [How Complex Systems Fail](https://www.youtube.com/watch?v=2S0k12uZR14) and associated paper [How Complex Systems fail (pdf)](https://web.mit.edu/2.75/resources/random/How%20Complex%20Systems%20Fail.pdf) by **Richard Cook**, [Antics, drift, and chaos](https://www.youtube.com/watch?v=SM2uXpmyJmA) by **Lorin Hochstein** and articles such as [Simple testing can prevent most critical failures](https://blog.acolyer.org/2016/10/06/simple-testing-can-prevent-most-critical-failures/) by **Yuan et al. OSDI 2014** (via **Adrian Colyer**). Other worthwhile resources are [Entities, Identities & Registries: Gaps in Corporate and IoT Identity](https://ssimeetup.org/gaps-corporate-iot-identity-heather-vescent-webinar-35/) by **Heather Vescent** and Ethics in Action: The IEEE Global Initiative of Ethics of Autonomous and Intelligent Systems, the P7000™ titled standards, at [Ethics in Action](https://ethicsinaction.ieee.org/) and [IPSO Smart Objects](https://www.omaspecworks.org/develop-with-oma-specworks/ipso-smart-objects/) for identities, [STELLA: report from the SNAFU-catchers workshop on coping with complexity](https://blog.acolyer.org/2020/01/20/stella-coping-with-complexity-2/) on humans within complex (agent) systems and [Challenges of real-world reinforcement learning](https://blog.acolyer.org/2020/01/13/challenges-of-real-world-rl/) [Assurance Monitoring of Cyber-Physical Systems with Machine Learning Components](https://deepai.org/publication/assurance-monitoring-of-cyber-physical-systems-with-machine-learning-components) what comes to using AI methods in cyber-physical, human-in-the-loop kind of systems and [Systems Thinking for Safety: Ten Principles](https://www.skybrary.aero/index.php/Toolkit:Systems_Thinking_for_Safety:_Ten_Principles) as a supplemental to core safety principles and [POTs: Protective Optimization Technologies](https://arxiv.org/abs/1806.02711) as a broad, general study.

As for this sample and a concrete case, consider an API can record `string` fields to database which have `NVARCHAR(MAX)` as their SQL Server type. This can lead to high resource consumption on the API but it can also quickly add to storage costs. If the number of rows increase rapidly, query times are probably affected as well. Mitigation likely involves limiting data field size, frequency of calls as well as database column constraints and perhaps sanitation measures. One should take measures to also protect against other systems under own administration in case they malfunction.

The sample shows a way to test the API together with storage on a developer machine, the tests could use a fuzzing library such as [SharpFuzz](https://github.com/Metalnem/sharpfuzz). This is also an opportunity to add a test to check no cross-site script (XSS) attacks exist, or even add [trusted types](https://github.com/WICG/trusted-types) to a web front-end and test its existence. The tests can also collect logs from the system and assert on them as part of normal testing, maybe to meet particular [SIEM process](https://en.wikipedia.org/wiki/Security_information_and_event_management) requirements. When these problems are found, they are the very least known and tested and can be mitigated. Also problematic inputs in production can be used as input in tests and debugged locally.

## Development, debugging and testing cycle

The development idea is that the developer can reset the development databases in case of relational database by either running the database project or publishing to it. Otherwise the tests make a differential backup in the beginning and restore it in the end. This way the developer can arrange data as per development and check using tests if they still pass, this allows for developing with data and even do exploratory testing as part of the development cycle. On the continuous integration server this is likely the opposite, one may want to restore the test database from a backup or a snapshot. Then when changes to other developers, continuous integration or even production are desired, they can be done on the `.sqlproj` and reviewed as per normal development flow, e.g. _reviewed in pull requets_.

At any time the database can be reset or snapshots deleted. There shouldn't be any snapshots visible unless the tests have been cancelled abruptly so that the test dispose haven't had the time to clean them off. In this case the tests do will fail unless the snapshots are deleted.

## Setting up the SQL Server Database

0. (_optional_) If you are using Visual Studio 2019 Community Edition, you need to edit the `OneBoxDeployment.Database.sqlproj` file, and set the `ArtifactReference` and `HintPath` to point to `Community`, instead of `Enterprise`.

1. This needs to be done only the first time
Open the _OneBoxDeployment.Database_ project and open _Debug_ tab. If the target connection string isn't set, put _Data Source=(localdb)\MSSQLLocalDB;Initial Catalog=OneBoxDeployment.Database;Integrated Security=True;Persist Security Info=False;Pooling=True;MultipleActiveResultSets=True;Connect Timeout=60;Encrypt=False;TrustServerCertificate=True_ to it. This deploys the database to LocalDb with the current user rights.

2. Set _OneBoxDeployment.Database_ from the project pages to allow incompatible database to be deployed. This is needed if the latest LocalDb bits (e.g. Azure SQL Server 2019) aren't installed.

3. Set _OneBoxDeployment.Database_ temporarily as a startup project. Run it. Running this project deploys the database with the connection string to LocalDb.

4. Set _OneBoxDeployment.Api_ and _OneBoxDeployment.OrleansHost_ as the startup project and run it. It should be able to connect to the LocalDb database using the connection string used in point _1._

This project gathers the scripts from all of the ADO.NET packages into _CreateOrleansTables_SQLServer.sql_. The script has been modified to create the Orleans tables to custom schemas and filegroups and the queries have been modified accordingly. This has been done in order to provide a more real world example of performance, disaster recovery and GDPR like constraints.

The SQL Server project includes an example how to provide constant data via merge expressions and how to bulk load data. The bulk load sample is inadequate and provided merely as an example. The benefits emerge when there are over some thousands of rows with multiple columns. Bulk load like this can quickly set the system to a known good state for debugging and testing purposes.

## Debugging and testing

To debug the API also Orleans silo needs to be started. To do that select solution and choose _Set startup projects_, then _Multiple startup projects_ and from there choose _OneBoxDeployment.Api_ and _OneBoxDeployment.OrleansHost_. Once the system has started, it should open a Swagger web page and also log its address to the command line. Testing should function as usually.

## Future feature development (ideas)

- [ ] Add Azure Storage Emulator with the same running principle as database. Add automatic startup to tests and development environment debugging.

- [ ] Add a web frontend using Razor or client side Blazor and web components and tests using [PuppeteerSharp](https://github.com/kblok/puppeteer-sharp) and [Razor Live Reload](https://weblog.west-wind.com/posts/2019/Jun/03/Building-Live-Reload-Middleware-for-ASPNET-Core) for enhanced development experience. Integrated testing like is done currently with the API.

- [ ] Add health checks with user interface and access control and appropriate message format (e.g. [Health Check Response Format for HTTP APIs](https://tools.ietf.org/html/draft-inadarei-api-health-check-02)).

- [ ] Add asynchronous startup tasks. See _StartupTask_ in _OneBoxDeployment.Api_.

- [ ] Add [Trill](https://github.com/microsoft/Trill).

- [ ] Add [FASTER](https://github.com/microsoft/FASTER).

- [ ] Add [BenchmarkDotNet](https://github.com/dotnet/BenchmarkDotNet) to (integration) tests. Can the results be saved in a well comparable form between runs?

- [ ] Add example and tests of a conflation case.

- [ ] Extend log testing also to Orleans silo so messages from that environment in addition to the API can also be collected.

- [ ] Combine the current in-memory logger with a XUnit one. See [ASP.NET Extensions XUnitLogger](https://github.com/aspnet/Extensions/blob/f162f1006bf8954f0102af8ff98c04077cf21b04/src/Logging/Logging.Testing/src/XunitLoggerProvider.cs) for an example.

- [ ] Make event formatting with functions instead of the current mechanism.

- [ ] Make the snapshotting mechanism more robusts by first removing leftovers from previous rounds if any.

- [ ] Improve API documention using [Rapidoc](https://github.com/mrin9/RapiDoc) or [Redoc](https://github.com/Redocly/redoc). JSON-LD capabability would be nice.

- [ ] Add more continuous integration logic as described earlier in the document. If concurrent testing is needed, the concurrent test databases need to have a different name. It may make sense to do this part by publishing a `dacpac` as would the CI do when publishing a production deployment deployment.

- [ ] Add deploying the runtime infrastructure with [Azure Fluent SDK](https://github.com/Azure/azure-libraries-for-net). The idea is to create a realistic deployment that should also
be fairly easy to manage and cheap. Virtual Machine Scale Sets? Could further be integrated with tests so that if needed, the tests can create the infrastructure, deploy the software and run the tests. This should be possible with configuration changes only to the software, such as connection strings.

- [ ] Add IdentityServer and testing complex access logic as an integral part of tests. This can be so that the clients retrieve the access token transparently.

- [ ] Use managed identity where appropriate and show how to lock down production environments and integrate security best practices.

- [ ] Lock down database schema so that only the Orleans cluster managed identity can
access it in production. In tests insert the user and CI priviledges automatically as part of
the database building process.

- [ ] Consider adding [ML.NET](https://github.com/dotnet/machinelearning) and enhancing it with a [NVidia Jetson](https://www.nvidia.com/en-us/autonomous-machines/embedded-systems/jetson-nano/) demo on the continuous integration when local agent is used.

- [ ] Make a heterogenous siloes example. Maybe with ML.NET hyperparameter tuning?
