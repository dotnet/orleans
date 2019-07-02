## Orleans Unit Testing Sample

Orleans Unit Testing Sample targetting:

* .NET Core 2.2
* Orleans 2.3.5
* xUnit 2.4.1
* Moq 4.12

This sample demonstrates how to mock and fake Orleans services using both *isolated* and *hosted* models.

### How To Run

* Open the `UnitTesting` solution with your favourite IDE.
* Run all unit tests with the IDE.

or...

* Run `dotnet test` in the solution folder.

### How It Works

The solution comprises several projects to mimic a typical Orleans setup.

* `Silo`: Skeleton silo host project to ensure the grains load. Has no relevance to the sample otherwise.
* `Grains`: Contains the implementations of the grains under test.
* `Grains.Interfaces`: Contains the interfaces of the grains under test.
* `Grains.Tests`: Contains the tests for this sample.

The `Grains.Tests` project contains equivalent tests in two flavours, *isolated* and *hosted*, grouped by folder.

There are advantages and trade-offs to each approach, depending on what the developer needs to accomplish.

#### Isolated

Tests where both dependencies and Orleans services are mocked on a test-by-test basis.
This style favours low-level isolated unit testing of the code under question.
However, it leads to more verbose test code due to redundant mocking constructs.

#### Hosted

Tests using the Orleans Test Cluster, where fake Orleans services are used.
This style favours high-level integration testing of multiple components, using fake data sources for higher coverage.
It leads to both shorter and more reliable test code due to better reproduction of live running conditions.
However, the developer must take care to prepare shared fake components and partition data as appropriate to avoid clashes during parallel testing.

### Tests

Both *isolated* and *hosted* folders contain the same tests, implementing the respective approach:

* `BasicGrainTests`: A test for a basic grain that can set and return a value.
* `TimerGrainTests`: A test for a grain that does some work upon a timer tick.
* `CallingGrainTests`: A test for a grain that calls another grain upon a request
* `CallingTimerGrainTests`: A test for a grain that calls another grain upon a timer tick.
* `ReminderGrainTests`: A test for a grain that does some work upon a reminder tick.
* `PersistentGrainTests`: A test for a grain that saves state to a storage provider.
