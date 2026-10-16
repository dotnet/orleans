using Microsoft.Accordant;
using Orleans.DurableTasks;
using Xunit;

namespace Orleans.DurableTasks.Abstractions.Tests;

[TestSuite("BVT")]
[TestProvider("None")]
[TestArea("DurableTasks")]
[TestCategory("BVT")]
public class DurableTaskModelTests
{
    private const string TestName = nameof(TaskIdHierarchyLifecycleMatchesAccordantModel);
    private const int MaxGeneratedCases = 64;
    private const int MaxCommandsPerCase = 8;
    private const int MaxTransitionSequenceLength = 3;

    [Fact]
    public async Task TaskIdHierarchyLifecycleMatchesAccordantModel()
    {
        var spec = CreateTaskIdSpec();
        var createRoot = spec.GetOperation<SegmentCommand, TaskIdObservation>("CreateRoot");
        var appendChild = spec.GetOperation<SegmentCommand, TaskIdObservation>("AppendChild");
        var moveToParent = spec.GetOperation<Unit, TaskIdObservation>("MoveToParent");
        var probeRelations = spec.GetOperation<SnapshotCommand, TaskIdObservation>("ProbeRelations");
        var probeNoneRelations = spec.GetOperation<Unit, TaskIdObservation>("ProbeNoneRelations");
        var tryAppendChildToNone = spec.GetOperation<SegmentCommand, TaskIdObservation>("TryAppendChildToNone");
        var inputs = new InputSet
        {
            createRoot.With(new SegmentCommand("tenant"), "CreateRoot ordinary 'tenant'"),
            appendChild.With(new SegmentCommand("step/one"), "AppendChild slash 'step/one'"),
            appendChild.With(new SegmentCommand(@"step\one"), @"AppendChild backslash 'step\one'"),
            moveToParent.With("MoveToParent"),
            probeRelations.With(new SnapshotCommand(0), "ProbeRelations remembered snapshot 0"),
            probeNoneRelations.With("ProbeNoneRelations"),
            tryAppendChildToNone.With(new SegmentCommand("forbidden"), "TryAppendChildToNone 'forbidden'")
        };
        var initialState = new TaskIdHierarchyModelState();
        var testCases = spec.GenerateTests(
            initialState,
            inputs,
            new TestGenerationOptions
            {
                MaxDepth = 4,
                MaxOperationApplicationCount = 2,
                SequentialTestCaseAlgorithm = SequentialTestCaseAlgorithms.CreateTransitionCoverage(MaxTransitionSequenceLength),
                ShouldApply = CanApply,
                ShouldIncludeTransition = ShouldIncludeTransition
            });

        Assert.NotEmpty(testCases);
        Assert.InRange(testCases.Count, 1, MaxGeneratedCases);
        Assert.All(testCases, testCase => Assert.InRange(testCase.OperationCalls.Count, 1, MaxCommandsPerCase));
        AssertGeneratedInputsAreCovered(testCases, inputs);
        AssertRequiredTransitionsAreCovered(testCases);

        var context = spec.CreateTestingContext(TestName);
        context.RequestPrinter = request => request?.ToString() ?? "<null>";
        context.ResponsePrinter = response => response?.ToString() ?? "<null>";
        var results = await spec.RunTests(
            context,
            initialState,
            testCases,
            new TestExecutionOptions
            {
                StopOnFirstFailure = false,
                BeforeEach = info => info.Context.Register(new TaskIdSystemUnderTest())
            });

        Assert.Equal(testCases.Count, results.Count);
        for (var caseIndex = 0; caseIndex < results.Count; caseIndex++)
        {
            Assert.True(results[caseIndex].Success, BuildFailureMessage(caseIndex, testCases[caseIndex], results[caseIndex]));
        }
    }

    private static Spec<TaskIdHierarchyModelState> CreateTaskIdSpec()
    {
        var spec = Spec.For<TaskIdHierarchyModelState>();
        spec.Operation<SegmentCommand, TaskIdObservation>(
            "CreateRoot",
            (request, state) =>
            {
                var current = new List<string> { request.Segment };
                var remembered = RememberedAfter(state.RememberedSnapshots, current);
                return Expect.That<TaskIdObservation>(
                        response => ValidateObservation(response, current, remembered))
                    .ThenState<TaskIdHierarchyModelState>(next =>
                    {
                        next.CurrentSegments = current;
                        next.RememberedSnapshots = remembered;
                    });
            });
        spec.Operation<SegmentCommand, TaskIdObservation>(
            "AppendChild",
            (request, state) =>
            {
                var current = new List<string>(state.CurrentSegments) { request.Segment };
                var remembered = RememberedAfter(state.RememberedSnapshots, current);
                return Expect.That<TaskIdObservation>(
                        response => ValidateObservation(response, current, remembered))
                    .ThenState<TaskIdHierarchyModelState>(next =>
                    {
                        next.CurrentSegments = current;
                        next.RememberedSnapshots = remembered;
                    });
            });
        spec.Operation<Unit, TaskIdObservation>(
            "MoveToParent",
            (_, state) =>
            {
                var current = state.CurrentSegments.Count == 0
                    ? new List<string>()
                    : state.CurrentSegments.Take(state.CurrentSegments.Count - 1).ToList();
                return Expect.That<TaskIdObservation>(
                        response => ValidateObservation(response, current, state.RememberedSnapshots))
                    .ThenState<TaskIdHierarchyModelState>(next => next.CurrentSegments = current);
            });
        spec.Operation<SnapshotCommand, TaskIdObservation>(
            "ProbeRelations",
            (request, state) =>
                Expect.That<TaskIdObservation>(
                        response => ValidateObservation(
                            response,
                            state.CurrentSegments,
                            state.RememberedSnapshots,
                            selectedSnapshotIndex: request.SnapshotIndex))
                    .SameState());
        spec.Operation<Unit, TaskIdObservation>(
            "ProbeNoneRelations",
            (_, state) =>
                Expect.That<TaskIdObservation>(
                        response => ValidateObservation(response, state.CurrentSegments, state.RememberedSnapshots))
                    .SameState());
        spec.Operation<SegmentCommand, TaskIdObservation>(
            "TryAppendChildToNone",
            (_, state) =>
                Expect.That<TaskIdObservation>(
                        response => ValidateObservation(
                            response,
                            state.CurrentSegments,
                            state.RememberedSnapshots,
                            expectedExceptionType: typeof(InvalidOperationException).FullName,
                            expectedExceptionMessage: "A child identifier requires a non-empty parent."))
                    .SameState());

        var createRoot = spec.GetOperation<SegmentCommand, TaskIdObservation>("CreateRoot");
        var appendChild = spec.GetOperation<SegmentCommand, TaskIdObservation>("AppendChild");
        var moveToParent = spec.GetOperation<Unit, TaskIdObservation>("MoveToParent");
        var probeRelations = spec.GetOperation<SnapshotCommand, TaskIdObservation>("ProbeRelations");
        var probeNoneRelations = spec.GetOperation<Unit, TaskIdObservation>("ProbeNoneRelations");
        var tryAppendChildToNone = spec.GetOperation<SegmentCommand, TaskIdObservation>("TryAppendChildToNone");
        return spec.ExecuteWith<TaskIdSystemUnderTest>()
            .Bind(createRoot, static (sut, request) => sut.CreateRoot(request))
            .Bind(appendChild, static (sut, request) => sut.AppendChild(request))
            .Bind(moveToParent, static (sut, _) => sut.MoveToParent())
            .Bind(probeRelations, static (sut, request) => sut.ProbeRelations(request))
            .Bind(probeNoneRelations, static (sut, _) => sut.ProbeNoneRelations())
            .Bind(tryAppendChildToNone, static (sut, request) => sut.TryAppendChildToNone(request))
            .Done();
    }

    private static bool CanApply(OperationInput input, IState rawState)
    {
        var state = (TaskIdHierarchyModelState)rawState;
        return input.Operation.Name switch
        {
            "CreateRoot" => state.CurrentSegments.Count == 0 && state.RememberedSnapshots.Count == 0,
            "AppendChild" => input.Request is SegmentCommand append
                && state.CurrentSegments.Count switch
                {
                    1 => append.Segment == "step/one",
                    2 => append.Segment == @"step\one",
                    _ => false
                },
            "MoveToParent" => true,
            "ProbeRelations" => input.Request is SnapshotCommand request
                && request.SnapshotIndex >= 0
                && request.SnapshotIndex < state.RememberedSnapshots.Count,
            "ProbeNoneRelations" => true,
            "TryAppendChildToNone" => state.CurrentSegments.Count == 0,
            _ => false
        };
    }

    private static bool ShouldIncludeTransition(IState rawState, OperationCall call, IState _)
    {
        var state = (TaskIdHierarchyModelState)rawState;
        return call.OperationInput.Operation.Name switch
        {
            "CreateRoot" or "AppendChild" => true,
            "MoveToParent" => state.CurrentSegments.Count > 0
                || (state.CurrentSegments.Count == 0 && state.RememberedSnapshots.Count == 0),
            "ProbeRelations" => state.CurrentSegments.Count == 1 && state.RememberedSnapshots.Count == 1,
            "ProbeNoneRelations" or "TryAppendChildToNone" =>
                state.CurrentSegments.Count == 0 && state.RememberedSnapshots.Count == 0,
            _ => false
        };
    }

    private static List<TaskIdSnapshotModel> RememberedAfter(
        IReadOnlyList<TaskIdSnapshotModel> remembered,
        IReadOnlyList<string> current)
    {
        var result = remembered
            .Select(snapshot => new TaskIdSnapshotModel { Segments = new List<string>(snapshot.Segments) })
            .ToList();
        if (current.Count > 0 && !result.Any(snapshot => snapshot.Segments.SequenceEqual(current)))
        {
            result.Add(new TaskIdSnapshotModel { Segments = new List<string>(current) });
        }

        return result;
    }

    private static ValidationResult ValidateObservation(
        TaskIdObservation actual,
        IReadOnlyList<string> current,
        IReadOnlyList<TaskIdSnapshotModel> remembered,
        int? selectedSnapshotIndex = null,
        string? expectedExceptionType = null,
        string? expectedExceptionMessage = null)
    {
        var failures = new List<string>();
        IReadOnlyList<string> parent = current.Count <= 1 ? [] : current.Take(current.Count - 1).ToArray();
        CheckEqual(failures, "formatted path", Format(current), actual.FormattedPath);
        CheckEqual(failures, "IsDefault", current.Count == 0, actual.IsDefault);
        CheckEqual(failures, "equals default", current.Count == 0, actual.EqualsDefault);
        CheckEqual(failures, "equals None", current.Count == 0, actual.EqualsNone);
        CheckEqual(failures, "default equals current", current.Count == 0, actual.DefaultEqualsCurrent);
        CheckEqual(failures, "None equals current", current.Count == 0, actual.NoneEqualsCurrent);
        CheckEqual(failures, "formatted parent", Format(parent), actual.ParentFormattedPath);
        CheckEqual(failures, "parent IsDefault", parent.Count == 0, actual.ParentIsDefault);
        CheckEqual(failures, "parent equals None", parent.Count == 0, actual.ParentEqualsNone);
        CheckRelation(failures, "current -> current", current, current, actual.CurrentToCurrent);
        CheckRelation(failures, "parent -> current", parent, current, actual.ParentToCurrent);
        CheckRelation(failures, "current -> parent", current, parent, actual.CurrentToParent);
        CheckRelation(failures, "current -> None", current, [], actual.CurrentToNone);
        CheckRelation(failures, "None -> current", [], current, actual.NoneToCurrent);
        CheckRelation(failures, "None -> None", [], [], actual.NoneToNone);
        CheckEqual(failures, "None formatted path", string.Empty, actual.NoneFormattedPath);
        CheckEqual(failures, "None IsDefault", true, actual.NoneIsDefault);
        CheckEqual(failures, "None parent formatted path", string.Empty, actual.NoneParentFormattedPath);
        CheckEqual(failures, "None parent IsDefault", true, actual.NoneParentIsDefault);
        CheckEqual(failures, "selected snapshot", selectedSnapshotIndex, actual.SelectedSnapshotIndex);
        CheckEqual(failures, "exception type", expectedExceptionType, actual.ExceptionType);
        CheckEqual(failures, "exception message", expectedExceptionMessage, actual.ExceptionMessage);
        CheckEqual(failures, "remembered snapshot count", remembered.Count, actual.RememberedSnapshots.Count);

        var comparableCount = Math.Min(remembered.Count, actual.RememberedSnapshots.Count);
        for (var index = 0; index < comparableCount; index++)
        {
            var expected = remembered[index].Segments;
            var observed = actual.RememberedSnapshots[index];
            CheckEqual(failures, $"remembered[{index}] formatted path", Format(expected), observed.FormattedPath);
            CheckEqual(failures, $"remembered[{index}] IsDefault", false, observed.IsDefault);
            CheckEqual(failures, $"remembered[{index}] equals None", false, observed.EqualsNone);
            CheckRelation(failures, $"current -> remembered[{index}]", current, expected, observed.CurrentToSnapshot);
            CheckRelation(failures, $"remembered[{index}] -> current", expected, current, observed.SnapshotToCurrent);
        }

        return failures.Count == 0
            ? ValidationResult.Valid()
            : ValidationResult.Invalid(string.Join(Environment.NewLine, failures));
    }

    private static void CheckRelation(
        List<string> failures,
        string name,
        IReadOnlyList<string> left,
        IReadOnlyList<string> right,
        TaskIdRelationObservation actual)
    {
        var equal = left.SequenceEqual(right);
        var ancestor = IsAncestor(left, right);
        var descendant = IsAncestor(right, left);
        var parent = ancestor && right.Count == left.Count + 1;
        var child = descendant && left.Count == right.Count + 1;
        CheckEqual(failures, $"{name} Equals", equal, actual.ValueEquals);
        CheckEqual(failures, $"{name} ==", equal, actual.OperatorEquals);
        CheckEqual(failures, $"{name} !=", !equal, actual.OperatorNotEquals);
        CheckEqual(failures, $"{name} IsAncestorOf", ancestor, actual.IsAncestor);
        CheckEqual(failures, $"{name} IsDescendantOf", descendant, actual.IsDescendant);
        CheckEqual(failures, $"{name} IsParentOf", parent, actual.IsParent);
        CheckEqual(failures, $"{name} IsChildOf", child, actual.IsChild);
    }

    private static bool IsAncestor(IReadOnlyList<string> candidate, IReadOnlyList<string> other)
    {
        if (candidate.Count == 0 || other.Count == 0 || candidate.Count > other.Count)
        {
            return false;
        }

        for (var index = 0; index < candidate.Count; index++)
        {
            if (!string.Equals(candidate[index], other[index], StringComparison.Ordinal))
            {
                return false;
            }
        }

        return true;
    }

    private static string Format(IReadOnlyList<string> segments)
        => string.Join("/", segments.Select(segment => segment
            .Replace("\\", "\\\\", StringComparison.Ordinal)
            .Replace("/", "\\/", StringComparison.Ordinal)));

    private static void CheckEqual<T>(List<string> failures, string name, T expected, T actual)
    {
        if (!EqualityComparer<T>.Default.Equals(expected, actual))
        {
            failures.Add($"{name}: expected <{expected}>, actual <{actual}>.");
        }
    }

    private static void AssertGeneratedInputsAreCovered(
        IList<SequentialTestCase> testCases,
        InputSet inputs)
    {
        var generatedInputNames = testCases
            .SelectMany(testCase => testCase.OperationCalls)
            .Select(call => call.OperationInput.Name)
            .ToHashSet(StringComparer.Ordinal);
        foreach (var input in inputs.Inputs)
        {
            Assert.Contains(input.Name, generatedInputNames);
        }
    }

    private static void AssertRequiredTransitionsAreCovered(IList<SequentialTestCase> testCases)
    {
        var operationSequences = testCases
            .Select(testCase => testCase.OperationCalls
                .Select(call => call.OperationInput.Operation.Name)
                .ToArray())
            .ToList();
        Assert.Contains(operationSequences, sequence => sequence[0] == "MoveToParent");
        Assert.Contains(operationSequences, sequence => sequence.SequenceEqual(["CreateRoot", "MoveToParent"]));
        Assert.Contains(operationSequences, sequence => sequence.SequenceEqual(["CreateRoot", "AppendChild", "AppendChild"]));
        Assert.Contains(operationSequences, sequence => sequence.Contains("ProbeRelations", StringComparer.Ordinal));
        Assert.Contains(operationSequences, sequence => sequence.Contains("ProbeNoneRelations", StringComparer.Ordinal));
        Assert.Contains(operationSequences, sequence => sequence[0] == "TryAppendChildToNone");
    }

    private static string BuildFailureMessage(
        int caseIndex,
        SequentialTestCase testCase,
        TestCaseExecutionResult result)
    {
        var operations = string.Join(
            Environment.NewLine,
            testCase.OperationCalls.Select((call, operationIndex) => $"  [{operationIndex}] {call}"));
        return $"""
            {TestName} failed.
            Generated case index: {caseIndex}
            Description: {testCase.Description}
            Operation sequence:
            {operations}
            Accordant failure: {result.LastFailureMessage ?? "<none>"}
            Accordant log: {result.LogFilePath ?? "<none>"}
            """;
    }

    private sealed record SegmentCommand(string Segment)
    {
        public override string ToString() => Segment;
    }

    private sealed record SnapshotCommand(int SnapshotIndex)
    {
        public override string ToString() => SnapshotIndex.ToString();
    }

    private sealed class TaskIdSystemUnderTest
    {
        private readonly List<TaskId> rememberedSnapshots = [];
        private TaskId current = TaskId.None;

        public TaskIdObservation CreateRoot(SegmentCommand request)
        {
            current = TaskId.CreateRoot(request.Segment);
            RememberCurrent();
            return Observe();
        }

        public TaskIdObservation AppendChild(SegmentCommand request)
        {
            current = current.Child(request.Segment);
            RememberCurrent();
            return Observe();
        }

        public TaskIdObservation MoveToParent()
        {
            current = current.Parent();
            return Observe();
        }

        public TaskIdObservation ProbeRelations(SnapshotCommand request)
            => Observe(selectedSnapshotIndex: request.SnapshotIndex);

        public TaskIdObservation ProbeNoneRelations() => Observe();

        public TaskIdObservation TryAppendChildToNone(SegmentCommand request)
        {
            Exception? failure = null;
            try
            {
                _ = current.Child(request.Segment);
            }
            catch (Exception exception)
            {
                failure = exception;
            }

            return Observe(failure: failure);
        }

        private void RememberCurrent()
        {
            if (!rememberedSnapshots.Contains(current))
            {
                rememberedSnapshots.Add(current);
            }
        }

        private TaskIdObservation Observe(int? selectedSnapshotIndex = null, Exception? failure = null)
        {
            if (selectedSnapshotIndex is int index)
            {
                _ = rememberedSnapshots[index];
            }

            var none = TaskId.None;
            var parent = current.Parent();
            return new TaskIdObservation
            {
                FormattedPath = current.ToString(),
                IsDefault = current.IsDefault,
                EqualsDefault = current.Equals(default(TaskId)),
                EqualsNone = current.Equals(TaskId.None),
                DefaultEqualsCurrent = default(TaskId) == current,
                NoneEqualsCurrent = TaskId.None == current,
                ParentFormattedPath = parent.ToString(),
                ParentIsDefault = parent.IsDefault,
                ParentEqualsNone = parent == TaskId.None,
                CurrentToCurrent = ObserveRelation(current, current),
                ParentToCurrent = ObserveRelation(parent, current),
                CurrentToParent = ObserveRelation(current, parent),
                CurrentToNone = ObserveRelation(current, none),
                NoneToCurrent = ObserveRelation(none, current),
                NoneToNone = ObserveRelation(none, none),
                NoneFormattedPath = none.ToString(),
                NoneIsDefault = none.IsDefault,
                NoneParentFormattedPath = none.Parent().ToString(),
                NoneParentIsDefault = none.Parent().IsDefault,
                SelectedSnapshotIndex = selectedSnapshotIndex,
                ExceptionType = failure?.GetType().FullName,
                ExceptionMessage = failure?.Message,
                RememberedSnapshots = rememberedSnapshots.Select(snapshot => new RememberedTaskIdObservation
                {
                    FormattedPath = snapshot.ToString(),
                    IsDefault = snapshot.IsDefault,
                    EqualsNone = snapshot == TaskId.None,
                    CurrentToSnapshot = ObserveRelation(current, snapshot),
                    SnapshotToCurrent = ObserveRelation(snapshot, current)
                }).ToList()
            };
        }

        private static TaskIdRelationObservation ObserveRelation(TaskId left, TaskId right)
            => new()
            {
                ValueEquals = left.Equals(right),
                OperatorEquals = left == right,
                OperatorNotEquals = left != right,
                IsAncestor = left.IsAncestorOf(right),
                IsDescendant = left.IsDescendantOf(right),
                IsParent = left.IsParentOf(right),
                IsChild = left.IsChildOf(right)
            };
    }

    private sealed class TaskIdObservation
    {
        public string FormattedPath { get; init; } = string.Empty;

        public bool IsDefault { get; init; }

        public bool EqualsDefault { get; init; }

        public bool EqualsNone { get; init; }

        public bool DefaultEqualsCurrent { get; init; }

        public bool NoneEqualsCurrent { get; init; }

        public string ParentFormattedPath { get; init; } = string.Empty;

        public bool ParentIsDefault { get; init; }

        public bool ParentEqualsNone { get; init; }

        public TaskIdRelationObservation CurrentToCurrent { get; init; } = new();

        public TaskIdRelationObservation ParentToCurrent { get; init; } = new();

        public TaskIdRelationObservation CurrentToParent { get; init; } = new();

        public TaskIdRelationObservation CurrentToNone { get; init; } = new();

        public TaskIdRelationObservation NoneToCurrent { get; init; } = new();

        public TaskIdRelationObservation NoneToNone { get; init; } = new();

        public string NoneFormattedPath { get; init; } = string.Empty;

        public bool NoneIsDefault { get; init; }

        public string NoneParentFormattedPath { get; init; } = string.Empty;

        public bool NoneParentIsDefault { get; init; }

        public int? SelectedSnapshotIndex { get; init; }

        public string? ExceptionType { get; init; }

        public string? ExceptionMessage { get; init; }

        public List<RememberedTaskIdObservation> RememberedSnapshots { get; init; } = [];

        public override string ToString()
            => $"path='{FormattedPath}', default={IsDefault}, parent='{ParentFormattedPath}', remembered={RememberedSnapshots.Count}, exception={ExceptionType ?? "<none>"}";
    }

    private sealed class RememberedTaskIdObservation
    {
        public string FormattedPath { get; init; } = string.Empty;

        public bool IsDefault { get; init; }

        public bool EqualsNone { get; init; }

        public TaskIdRelationObservation CurrentToSnapshot { get; init; } = new();

        public TaskIdRelationObservation SnapshotToCurrent { get; init; } = new();
    }

    private sealed class TaskIdRelationObservation
    {
        public bool ValueEquals { get; init; }

        public bool OperatorEquals { get; init; }

        public bool OperatorNotEquals { get; init; }

        public bool IsAncestor { get; init; }

        public bool IsDescendant { get; init; }

        public bool IsParent { get; init; }

        public bool IsChild { get; init; }
    }

    [Fact]
    public async Task DurableCancellationLifecycleMatchesAccordantModel()
    {
        const string testName = nameof(DurableCancellationLifecycleMatchesAccordantModel);
        var spec = CreateCancellationSpec();
        var requestTaskCancellation = spec.GetOperation<TaskCancellationCommand, CancellationObservation>("RequestTaskCancellation");
        var cancelAttempt = spec.GetOperation<AttemptCancellationCommand, CancellationObservation>("CancelAttempt");
        var registerObservers = spec.GetOperation<Unit, CancellationObservation>("RegisterObservers");
        var observeCancellation = spec.GetOperation<Unit, CancellationObservation>("ObserveCancellation");
        var inputs = new InputSet
        {
            requestTaskCancellation.With(new TaskCancellationCommand("buyer"), "RequestTaskCancellation buyer"),
            requestTaskCancellation.With(new TaskCancellationCommand("operator"), "RequestTaskCancellation operator"),
            requestTaskCancellation.With(new TaskCancellationCommand(null), "RequestTaskCancellation without reason"),
            cancelAttempt.With(
                new AttemptCancellationCommand(DurableTaskAttemptCancellationReason.ActivationStopping),
                "CancelAttempt activation stopping"),
            cancelAttempt.With(
                new AttemptCancellationCommand(DurableTaskAttemptCancellationReason.TaskDeleted),
                "CancelAttempt task deleted"),
            registerObservers.With("RegisterObservers"),
            observeCancellation.With("ObserveCancellation")
        };
        var initialState = new DurableCancellationModelState();
        var testCases = spec.GenerateTests(
            initialState,
            inputs,
            new TestGenerationOptions
            {
                MaxDepth = 4,
                MaxOperationApplicationCount = 2,
                SequentialTestCaseAlgorithm = SequentialTestCaseAlgorithms.CreateTransitionCoverage(MaxTransitionSequenceLength),
                ShouldApply = CanApplyCancellationCommand
            });

        Assert.NotEmpty(testCases);
        Assert.All(testCases, testCase => Assert.InRange(testCase.OperationCalls.Count, 1, MaxCommandsPerCase));
        AssertGeneratedInputsAreCovered(testCases, inputs);
        AssertRequiredCancellationTransitionsAreCovered(testCases);

        var context = spec.CreateTestingContext(testName);
        context.RequestPrinter = request => request?.ToString() ?? "<null>";
        context.ResponsePrinter = response => response?.ToString() ?? "<null>";
        var results = await spec.RunTests(
            context,
            initialState,
            testCases,
            new TestExecutionOptions
            {
                StopOnFirstFailure = false,
                BeforeEach = info => info.Context.Register(new CancellationSystemUnderTest())
            });

        Assert.Equal(testCases.Count, results.Count);
        for (var caseIndex = 0; caseIndex < results.Count; caseIndex++)
        {
            Assert.True(
                results[caseIndex].Success,
                BuildCancellationFailureMessage(testName, caseIndex, testCases[caseIndex], results[caseIndex]));
        }
    }

    private static Spec<DurableCancellationModelState> CreateCancellationSpec()
    {
        var spec = Spec.For<DurableCancellationModelState>();
        spec.Operation<TaskCancellationCommand, CancellationObservation>(
            "RequestTaskCancellation",
            (request, state) =>
            {
                var expected = RequestTaskCancellation(state, request);
                return Expect.That<CancellationObservation>(response => ValidateCancellationObservation(response, expected))
                    .ThenState<DurableCancellationModelState>(next => CopyCancellationState(expected, next));
            });
        spec.Operation<AttemptCancellationCommand, CancellationObservation>(
            "CancelAttempt",
            (request, state) =>
            {
                var expected = CancelAttempt(state, request);
                return Expect.That<CancellationObservation>(response => ValidateCancellationObservation(response, expected))
                    .ThenState<DurableCancellationModelState>(next => CopyCancellationState(expected, next));
            });
        spec.Operation<Unit, CancellationObservation>(
            "RegisterObservers",
            (_, state) =>
            {
                var expected = RegisterObservers(state);
                return Expect.That<CancellationObservation>(response => ValidateCancellationObservation(response, expected))
                    .ThenState<DurableCancellationModelState>(next => CopyCancellationState(expected, next));
            });
        spec.Operation<Unit, CancellationObservation>(
            "ObserveCancellation",
            (_, state) =>
                Expect.That<CancellationObservation>(response => ValidateCancellationObservation(response, state))
                    .SameState());

        var requestTaskCancellation = spec.GetOperation<TaskCancellationCommand, CancellationObservation>("RequestTaskCancellation");
        var cancelAttempt = spec.GetOperation<AttemptCancellationCommand, CancellationObservation>("CancelAttempt");
        var registerObservers = spec.GetOperation<Unit, CancellationObservation>("RegisterObservers");
        var observeCancellation = spec.GetOperation<Unit, CancellationObservation>("ObserveCancellation");
        return spec.ExecuteWith<CancellationSystemUnderTest>()
            .BindAsync(requestTaskCancellation, static (sut, request) => sut.RequestTaskCancellationAsync(request))
            .BindAsync(cancelAttempt, static (sut, request) => sut.CancelAttemptAsync(request))
            .BindAsync(registerObservers, static (sut, _) => sut.RegisterObserversAsync())
            .BindAsync(observeCancellation, static (sut, _) => sut.ObserveAsync())
            .Done();
    }

    private static bool CanApplyCancellationCommand(OperationInput input, IState rawState)
    {
        var state = (DurableCancellationModelState)rawState;
        return input.Operation.Name switch
        {
            "RegisterObservers" => state.ObserverCount < 2,
            _ => true
        };
    }

    /// <summary>The first request records the reason and cancels <see cref="DurableTaskContext.CancellationToken"/>.</summary>
    private static DurableCancellationModelState RequestTaskCancellation(
        DurableCancellationModelState state,
        TaskCancellationCommand command)
    {
        var next = CloneCancellationState(state);
        if (next.TaskCancellationRequested)
        {
            return next;
        }

        next.TaskCancellationRequested = true;
        next.TaskCancellationReason = command.Reason;
        CancelToken(next);
        return next;
    }

    /// <summary>
    /// The first cancellation of the attempt records its reason and cancels
    /// <see cref="DurableTaskContext.AttemptCancellationToken"/>, then <see cref="DurableTaskContext.CancellationToken"/>.
    /// </summary>
    private static DurableCancellationModelState CancelAttempt(
        DurableCancellationModelState state,
        AttemptCancellationCommand command)
    {
        var next = CloneCancellationState(state);
        if (next.AttemptCanceled)
        {
            return next;
        }

        next.AttemptCanceled = true;
        next.AttemptCancellationReason = command.Reason;
        next.AttemptObserverInvocations += next.ObserverCount;
        CancelToken(next);
        return next;
    }

    /// <summary>Registers an observer on each token. An observer registered on a canceled token runs at once.</summary>
    private static DurableCancellationModelState RegisterObservers(DurableCancellationModelState state)
    {
        var next = CloneCancellationState(state);
        next.ObserverCount++;
        if (next.TokenCanceled)
        {
            next.TokenObserverInvocations++;
        }

        if (next.AttemptCanceled)
        {
            next.AttemptObserverInvocations++;
        }

        return next;
    }

    private static void CancelToken(DurableCancellationModelState state)
    {
        if (!state.TokenCanceled)
        {
            state.TokenCanceled = true;
            state.TokenObserverInvocations += state.ObserverCount;
        }
    }

    private static DurableCancellationModelState CloneCancellationState(DurableCancellationModelState state)
    {
        var clone = new DurableCancellationModelState();
        CopyCancellationState(state, clone);
        return clone;
    }

    private static void CopyCancellationState(
        DurableCancellationModelState source,
        DurableCancellationModelState destination)
    {
        destination.TaskCancellationRequested = source.TaskCancellationRequested;
        destination.TaskCancellationReason = source.TaskCancellationReason;
        destination.AttemptCanceled = source.AttemptCanceled;
        destination.AttemptCancellationReason = source.AttemptCancellationReason;
        destination.TokenCanceled = source.TokenCanceled;
        destination.ObserverCount = source.ObserverCount;
        destination.TokenObserverInvocations = source.TokenObserverInvocations;
        destination.AttemptObserverInvocations = source.AttemptObserverInvocations;
    }

    private static ValidationResult ValidateCancellationObservation(
        CancellationObservation actual,
        DurableCancellationModelState expected)
    {
        var failures = new List<string>();
        CheckEqual(failures, "IsTaskCancellationRequested", expected.TaskCancellationRequested, actual.IsTaskCancellationRequested);
        CheckEqual(failures, "TaskCancellationReason", expected.TaskCancellationReason, actual.TaskCancellationReason);
        CheckEqual(failures, "CancellationToken canceled", expected.TokenCanceled, actual.TokenIsCancellationRequested);
        CheckEqual(failures, "AttemptCancellationToken canceled", expected.AttemptCanceled, actual.AttemptTokenIsCancellationRequested);
        CheckEqual(
            failures,
            "durable operation outcome",
            expected.AttemptCanceled ? $"attempt-canceled:{expected.AttemptCancellationReason}" : "completed",
            actual.DurableOperationOutcome);
        CheckEqual(failures, "CancellationToken observer invocations", expected.TokenObserverInvocations, actual.TokenObserverInvocations);
        CheckEqual(failures, "AttemptCancellationToken observer invocations", expected.AttemptObserverInvocations, actual.AttemptObserverInvocations);
        CheckEqual(failures, "observer ordering violations", string.Empty, string.Join("|", actual.ObserverViolations));
        CheckEqual(failures, "unexpected adapter exception", null, actual.UnexpectedException);
        return failures.Count == 0
            ? ValidationResult.Valid()
            : ValidationResult.Invalid(string.Join(Environment.NewLine, failures));
    }

    private static void AssertRequiredCancellationTransitionsAreCovered(IList<SequentialTestCase> testCases)
    {
        var inputSequences = testCases
            .Select(testCase => testCase.OperationCalls.Select(call => call.OperationInput.Name).ToArray())
            .ToList();
        var description = Environment.NewLine + string.Join(
            Environment.NewLine,
            inputSequences.Select(sequence => string.Join(" -> ", sequence)));
        Assert.True(
            inputSequences.Any(sequence => ContainsOrderedPair(sequence, "RequestTaskCancellation ", "RequestTaskCancellation ")),
            "Missing repeated task cancellation." + description);
        Assert.True(
            inputSequences.Any(sequence => ContainsOrderedPair(sequence, "CancelAttempt ", "CancelAttempt ")),
            "Missing repeated attempt cancellation." + description);
        Assert.True(
            inputSequences.Any(sequence => ContainsOrderedPair(sequence, "CancelAttempt ", "RequestTaskCancellation ")),
            "Missing task cancellation after attempt cancellation." + description);
        Assert.True(
            inputSequences.Any(sequence => ContainsOrderedPair(sequence, "RequestTaskCancellation ", "CancelAttempt ")),
            "Missing attempt cancellation after task cancellation." + description);
        Assert.True(
            inputSequences.Any(sequence => ContainsOrderedPair(sequence, "RegisterObservers", "CancelAttempt ")),
            "Missing observers of an attempt cancellation." + description);
        Assert.True(
            inputSequences.Any(sequence => ContainsOrderedPair(sequence, "RegisterObservers", "RequestTaskCancellation ")),
            "Missing observers of a task cancellation." + description);
        Assert.True(
            inputSequences.Any(sequence => ContainsOrderedPair(sequence, "CancelAttempt ", "RegisterObservers")),
            "Missing late observers." + description);
    }

    private static bool ContainsOrderedPair(string[] sequence, string firstPrefix, string secondPrefix)
        => Enumerable.Range(0, Math.Max(0, sequence.Length - 1)).Any(index =>
            sequence[index].StartsWith(firstPrefix, StringComparison.Ordinal)
            && sequence[index + 1].StartsWith(secondPrefix, StringComparison.Ordinal));

    private static string BuildCancellationFailureMessage(
        string testName,
        int caseIndex,
        SequentialTestCase testCase,
        TestCaseExecutionResult result)
    {
        var operations = string.Join(
            Environment.NewLine,
            testCase.OperationCalls.Select((call, operationIndex) => $"  [{operationIndex}] {call}"));
        return $"""
            {testName} failed.
            Generated case index: {caseIndex}
            Description: {testCase.Description}
            Operation sequence:
            {operations}
            Accordant failure: {result.LastFailureMessage ?? "<none>"}
            Accordant log: {result.LogFilePath ?? "<none>"}
            """;
    }

    private sealed record TaskCancellationCommand(string? Reason)
    {
        public override string ToString() => $"reason={Reason ?? "<null>"}";
    }

    private sealed record AttemptCancellationCommand(DurableTaskAttemptCancellationReason Reason)
    {
        public override string ToString() => $"reason={Reason}";
    }

    private sealed class CancellationSystemUnderTest
    {
        private readonly TestContext context;
        private readonly List<CancellationTokenRegistration> registrations = [];
        private readonly List<string> observerViolations = [];
        private int tokenObserverInvocations;
        private int attemptObserverInvocations;
        private string? unexpectedException;

        public CancellationSystemUnderTest()
        {
            var host = new TestHost(DateTimeOffset.UnixEpoch);
            context = host.CreateContext(TaskId.CreateRoot("accordant-cancellation"));
        }

        public Task<CancellationObservation> RequestTaskCancellationAsync(TaskCancellationCommand command)
            => RunAndObserveAsync(() => DurableTaskRuntimeHelper.RequestTaskCancellation(context, command.Reason));

        public Task<CancellationObservation> CancelAttemptAsync(AttemptCancellationCommand command)
            => RunAndObserveAsync(() => DurableTaskRuntimeHelper.CancelAttempt(context, command.Reason));

        public Task<CancellationObservation> RegisterObserversAsync()
            => RunAndObserveAsync(() =>
            {
                registrations.Add(context.CancellationToken.Register(() =>
                {
                    tokenObserverInvocations++;

                    // The cancellation's state is published before the token's observers run.
                    if (!context.IsTaskCancellationRequested && !context.AttemptCancellationToken.IsCancellationRequested)
                    {
                        observerViolations.Add("CancellationToken observer ran before the cancellation was recorded");
                    }
                }));

                // An attempt's token is canceled before CancellationToken, so its observers find CancellationToken canceled
                // only by an earlier task cancellation.
                var registeredBeforeAttemptCancellation = !context.AttemptCancellationToken.IsCancellationRequested;
                registrations.Add(context.AttemptCancellationToken.Register(() =>
                {
                    attemptObserverInvocations++;
                    if (registeredBeforeAttemptCancellation
                        && context.CancellationToken.IsCancellationRequested != context.IsTaskCancellationRequested)
                    {
                        observerViolations.Add("AttemptCancellationToken observer ran after CancellationToken was canceled");
                    }
                }));
            });

        public Task<CancellationObservation> ObserveAsync() => RunAndObserveAsync(static () => { });

        private async Task<CancellationObservation> RunAndObserveAsync(Action command)
        {
            unexpectedException = null;
            try
            {
                command();
            }
            catch (Exception exception)
            {
                unexpectedException = DescribeException(exception);
            }

            // Every durable operation of a canceled attempt throws the attempt's cancellation.
            var response = await DurableTaskRuntimeHelper.RunAsync(DurableTask.Delay(TimeSpan.Zero), context);
            var outcome = response.Exception switch
            {
                null => "completed",
                DurableTaskAttemptCanceledException canceled when canceled.CancellationToken == context.AttemptCancellationToken
                    => $"attempt-canceled:{canceled.Reason}",
                { } exception => DescribeException(exception),
            };

            return new CancellationObservation(
                IsTaskCancellationRequested: context.IsTaskCancellationRequested,
                TaskCancellationReason: context.TaskCancellationReason,
                TokenIsCancellationRequested: context.CancellationToken.IsCancellationRequested,
                AttemptTokenIsCancellationRequested: context.AttemptCancellationToken.IsCancellationRequested,
                DurableOperationOutcome: outcome,
                TokenObserverInvocations: tokenObserverInvocations,
                AttemptObserverInvocations: attemptObserverInvocations,
                ObserverViolations: observerViolations.ToArray(),
                UnexpectedException: unexpectedException);
        }

        private static string DescribeException(Exception exception)
            => $"{exception.GetType().FullName}: {exception.Message}";
    }

    private sealed record CancellationObservation(
        bool IsTaskCancellationRequested,
        string? TaskCancellationReason,
        bool TokenIsCancellationRequested,
        bool AttemptTokenIsCancellationRequested,
        string DurableOperationOutcome,
        int TokenObserverInvocations,
        int AttemptObserverInvocations,
        IReadOnlyList<string> ObserverViolations,
        string? UnexpectedException)
    {
        public override string ToString()
            => $"task={IsTaskCancellationRequested}({TaskCancellationReason ?? "<null>"}), token={TokenIsCancellationRequested}, "
                + $"attempt={AttemptTokenIsCancellationRequested}, operation={DurableOperationOutcome}, "
                + $"observers={TokenObserverInvocations}/{AttemptObserverInvocations}, "
                + $"violations=[{string.Join("|", ObserverViolations)}], unexpected={UnexpectedException ?? "<none>"}";
    }
}

[State]
internal partial class TaskIdHierarchyModelState : State
{
    public List<string> CurrentSegments { get; set; } = [];

    public List<TaskIdSnapshotModel> RememberedSnapshots { get; set; } = [];
}

[State]
internal partial class TaskIdSnapshotModel : State
{
    public List<string> Segments { get; set; } = [];
}

[State]
internal partial class DurableCancellationModelState : State
{
    public bool TaskCancellationRequested { get; set; }

    public string? TaskCancellationReason { get; set; }

    public bool AttemptCanceled { get; set; }

    public DurableTaskAttemptCancellationReason AttemptCancellationReason { get; set; }

    public bool TokenCanceled { get; set; }

    public int ObserverCount { get; set; }

    public int TokenObserverInvocations { get; set; }

    public int AttemptObserverInvocations { get; set; }
}
