PERFTEST DOCUMENTATION, 2011-05-04

This file documents the proper use of the TestClients.ps1 powershell script and associated config files.


0. PURPOSE:

The purpose of these scripts is to automate the testing of client programs under various settings.
Different numbers of clients and silos, as well as different configuration settings, can be set
for each test.  Tests will be run in sequence, and logs will be collected for each one.

This architecture can be used to measure performance data of a client program with different settings.
(The ChirperClient test does this.)


1. EXPLANATION:

Each client package has a name, a pair of scripts that run at the start and termination of its test,
and a location to copy its files from and to deploy them to on the client machines.  Client packages
also provide a list of files that should be collected and copied back to the machine that runs the
test (logfiles, for example), as well as a list of filenames of the tasks that may need to be killed
to terminate a stray execution.

The tests to run are provided in a list, where each test has a name, a client package to run,
a number of silos and clients to use, and a time to run for.  Each test can also define changes to
its configuration files, or special flags to pass to its scripts, for that test alone.  All of this
data is generally contained within the ClientDeployment.xml file.


2. INSTALLATION:

Install an SDK build (\SDK-DROP).
Copy the contents of the \Prototype\Test\PerfTests into the directory \SDK-DROP\RemoteDeployment.


3. CONFIGURATION:

Configure OrleansConfiguration.xml and ClientConfiguration.xml as normal, including the addresses
that they will attempt to connect to.  Modified versions of these files will be created for each test.

Deployment.xml should be configured as normal.  All available silos should be listed in Deployment.xml.
If a test specifies fewer silos for use than are provided in Deployment.xml, only the correct number
will be used.

ClientDeployment.xml contains specifications for the client packages to deploy, and for the tests to run.
All xml tags are under the top level <Deployment>.

<TargetLocation Path="C:\Orleans" />

	The TargetLocation tag determines the root path where clients will be installed on the clients.
	In this case, all client files will be installed into subdirectories of C:\Orleans\ on the
	client machines.


<Packages>
    <Package Name="ChirperClient" StartScript=".\Start-ChirperClient.ps1" PostScript=".\Post-ChirperClient.ps1" SourcePath="..\Binaries\ChirperClient" TargetPath="ChirperClient" >
        <CopyFiles FileFilter="*.graphml" />
        <TaskToKill Filename="ChirperNetworkGenerator" />
    </Package
</Packages>

	The Packages element contains a list of Package elements.  Each Package represents one client
	program that can be used in any number of tests.

	Each Package contains:
		- Name, which will be referred to when defining tests.
		- StartScript, which is called to execute the client program.  It is run locally, and
		  is responsible for remotely starting the actual program on the client machines.
		- PostScript, which runs after the clients have been terminated and all files have
		  been copied back to the local machine.  It is commonly used to parse logfiles.
		- SourcePath, the path to copy the client from locally.
		- TargetPath, the subdirectory to place the client package on the client machines.
		  This will be placed underneath the TargetLocation specified for the whole file.
		
		- Zero or more CopyFiles elements, which specify a filter of files to copy back to the
		  local machine.  The location of these files will be discussed in section 5.
		- Zero or more TaskToKill elements, which will be terminated on all clients whenever the
		  script cleans up the client environments.


<RuntimeConfiguration Path=".\OrleansConfiguration.xml" />
<ClientConfiguration Path=".\ClientConfiguration.xml" />

	These two elements specify which configuration files to use when deploying.  By changing these,
	it's possible to create multiple ClientDeployment files that are easy to swap out.


<Clients>
  <Client HostName="XCG-Azure-30" />
  <Client HostName="XCG-Azure-31" />
  <Client HostName="XCG-Azure-32" />
</Clients>

	The Clients element contains a list of Client elements.  Each one specifies a client machine
	that can be used to test.  If a test specifies that it will use two clients, the first two from
	the top of this list will be used.  Do not request more client machines than are provided in
	this list, and do not duplicate entries.


<TestSettings>
  <TestSetting TestSettingName="Network100" Nodes="100" Edges="2700" />
</TestSettings>

	TestSettings are essentially lists of key-value pairs that can be passed to a client script.
	They are only understood by the client script.  Each test can have zero or more test settings,
	which will be looked up in this table by their name.  The other attributes in each element
	are inserted into a dictionary for use by the client script, and each TestSetting element
	may contain any number of attributes.


<ServerOverrides DefaultNamespace="urn:orleans">
  <Override Name="TasksDisabled">
    <Delete XPath="/namespace:OrleansConfiguration/namespace:Globals/namespace:Tasks" />
    <Delete XPath="/namespace:OrleansConfiguration/namespace:Globals/namespace:Persistence" />
    <AddNode XPath="/namespace:OrleansConfiguration/namespace:Globals">
      <Tasks Disabled="true" /> 
    </AddNode>
  </Override>
</ServerOverrides>

	ServerOverrides define changes to the OrleansConfiguration file that will be in use for some
	tests and not for others.  Any number of Overrides may be provided, and each Override
	may contain any number of Delete and AddNode directives.  Each test may specify zero or more
	ServerOverrides, and will find the matching entries in this list to modify their own
	OrleansConfiguration file before deployment.  The Delete and AddNode elements have the
	following effects:
		- Delete:  Locates a given tag in the OrleansConfiguration.xml file, and removes
		  it entirely.
		- AddNode:  Locates a given path in the OrleansConfiguration.xml file, and then adds
		  all sub-elements verbatim.  Any elements contained inside AddNode will be added to the
		  OrleansConfiguration.xml file.

	One common use of ServerOverrides is to delete an element from the OrleansConfiguration.xml
	file, and then replace it with a similar element that has slightly different settings.
	Caution:  All paths specified in Delete or AddNode directives must be fully qualified with
	their "namespace:" parts.  DefaultNamespace in ServerOverrides is used for this.


<ClientOverrides DefaultNamespace="urn:orleans">
  <Override Name="Standard">
      <Delete XPath="/namespace:ClientConfiguration/namespace:Messaging" />
      <AddNode XPath="/namespace:ClientConfiguration">
          <Messaging ResponseTimeout="6000" UseStandardSerializer="true" MessageEncoding="Binary" SenderQueues="4" /> 
      </AddNode>
  </Override>
</ClientOverrides>

	ClientOverrides are functionally identical in behavior to ServerOverrides, except that they
	modify the ClientConfiguration file instead.


<Tests>
  <Test Name="MediumFirst" Package="ChirperClient" Silos="5" Clients="1" Time="900">
      <ServerOverride name="Standard" />
      <ClientOverride name="Standard" /> 
      <TestSetting Name="Network10000" />
  </Test>
</Tests>

	The Tests element contains any number of Test elements, which each specify a test to be
	performed in order.

	Each Test has the following attributes and elements:
		- Name, which is used to generate the results (see Section 5).
		- Package, the name of the client package to use.
		- Silos, the number of silos to use, taken from Deployment.xml.  Silos are chosen
		  in order, from the top of the list.
		- Clients, the number of clients to use, taken from the list at the top of
		  this file.  Chosen in order from the top of the list.
		  Caution: Behavior is unspecified if more clients or silos are specified than
		  are available!  The system will attempt to detect if not allsilos are not running,
		  and will terminate the test if they are not.
		- Time, the number of seconds that the client should attempt to run for.  This is not
		  a hard limit, and it is only enforced by the client script!  This number is intended
		  to compare actual client performance.  If one run takes 30 seconds to set up, and
		  another run takes 5 seconds to set up, they should still both spend "Time" seconds
		  running the actual test itself to generate a fair comparison.  This means that runs
		  will often take longer than "Time" specifies.

		- ServerOverride, zero or more elements, which will be applied to the OrleansConfiguration.xml
		  file for this test.
		- ClientOverride, zero or more elements, which will be applied to all ClientConfiguration.xml
		  files that are used for this test.
		- TestSetting, zero or more elements, which will be looked up and have their data
		  accessible to the test scripts.
		

4. RUNNING:

To run the tests after configuring them, execute .\TestClients.ps1 from Powershell.  All tests will
run in series, and control will be returned to the Powershell prompt afterwards.  All hosts are
started and stopped automatically, and cleanup is performed by the system:  There is no need for
any user interaction while the tests are running.
	

5. RESULTS:

All results are stored in subdirectories directly within the location that TestClients.ps1 was run from.
Each run is assigned an ID as its folder name, and contains multiple folders inside it for its tests.
Normally, the run ID is a date/timestamp for when the test was initiated:  YY-MM-DD.hh.mm.ss
There is a special case for run IDs.  If a run is started, and detects that either a previous run was in
progress or was terminated abnormally, it will attempt to copy data but will not know what run ID to use.
In this case, it uses the pattern "Bucket-#", where # starts at 1 and increases by 1 each time so as not
to overlap.  Buckets will contain all files that are flagged to be copied by each client, but will not
execute scripts on them.

Each run contains a folder for each test, and each test contains a folder for each client.
A sample directory structure might look like this:

\11-05-03.19.05.54\			- No files by default
\11-05-03.19.05.54\First		- Contains logfiles for every silo, and the generated configuration files used        
\11-05-03.19.05.54\First\17xcg1044	- Contains the files that were copied from that client
\11-05-03.19.05.54\First\17xcg1045	- Contains the files that were copied from that client
\11-05-03.19.05.54\Second		- Contains logfiles for every silo, and the generated configuration files used        
\11-05-03.19.05.54\Second\17xcg1044	- Contains the files that were copied from that client
\11-05-03.19.05.54\Second\17xcg1045	- Contains the files that were copied from that client

Scripts are allowed and encouraged to add additional files to this structure for ease of reading.
For example, the Chirper package adds a summary.txt file to each test's directory that summarizes the test results,
and also compiles all of those results into a single summary.txt file in the run's root.