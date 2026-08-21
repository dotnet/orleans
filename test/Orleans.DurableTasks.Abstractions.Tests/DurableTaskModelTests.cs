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
        var registerBefore = spec.GetOperation<CancellationRegistrationCommand, CancellationObservation>(
            "RegisterBeforeCancellation");
        var disposeRegistration = spec.GetOperation<CancellationSlotCommand, CancellationObservation>(
            "DisposeRegistration");
        var requestCancellation = spec.GetOperation<Unit, CancellationObservation>("RequestCancellation");
        var registerAfter = spec.GetOperation<CancellationRegistrationCommand, CancellationObservation>(
            "RegisterAfterCancellation");
        var observeCancellation = spec.GetOperation<Unit, CancellationObservation>("ObserveCancellation");
        var inputs = new InputSet
        {
            registerBefore.With(
                new CancellationRegistrationCommand(0, CallbackOutcome.Success),
                "RegisterBeforeCancellation slot 0 success"),
            registerBefore.With(
                new CancellationRegistrationCommand(1, CallbackOutcome.ThrowInvalidOperation),
                "RegisterBeforeCancellation slot 1 invalid-operation failure"),
            registerBefore.With(
                new CancellationRegistrationCommand(2, CallbackOutcome.ThrowArgument),
                "RegisterBeforeCancellation slot 2 argument failure"),
            disposeRegistration.With(new CancellationSlotCommand(0), "DisposeRegistration slot 0"),
            disposeRegistration.With(new CancellationSlotCommand(1), "DisposeRegistration slot 1"),
            disposeRegistration.With(new CancellationSlotCommand(2), "DisposeRegistration slot 2"),
            requestCancellation.With("RequestCancellation"),
            registerAfter.With(
                new CancellationRegistrationCommand(0, CallbackOutcome.Success),
                "RegisterAfterCancellation slot 0 success"),
            registerAfter.With(
                new CancellationRegistrationCommand(1, CallbackOutcome.ThrowInvalidOperation),
                "RegisterAfterCancellation slot 1 invalid-operation failure"),
            registerAfter.With(
                new CancellationRegistrationCommand(2, CallbackOutcome.ThrowArgument),
                "RegisterAfterCancellation slot 2 argument failure"),
            observeCancellation.With("ObserveCancellation")
        };
        var initialState = CreateInitialCancellationState();
        var testCases = spec.GenerateTests(
            initialState,
            inputs,
            new TestGenerationOptions
            {
                MaxDepth = 4,
                MaxOperationApplicationCount = 2,
                SequentialTestCaseAlgorithm = SequentialTestCaseAlgorithms.CreateTransitionCoverage(4),
                ShouldApply = CanApplyCancellationCommand,
                ShouldIncludeTransition = ShouldIncludeCancellationTransition
            });

        Assert.NotEmpty(testCases);
        Assert.InRange(testCases.Count, 1, MaxGeneratedCases);
        Assert.All(testCases, testCase => Assert.InRange(testCase.OperationCalls.Count, 1, MaxCommandsPerCase));
        Assert.Equal(4, testCases.Max(testCase => testCase.OperationCalls.Count));
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
        spec.Operation<CancellationRegistrationCommand, CancellationObservation>(
            "RegisterBeforeCancellation",
            (request, state) =>
            {
                var expected = RegisterBeforeCancellation(state, request);
                return Expect.That<CancellationObservation>(
                        response => ValidateCancellationObservation(
                            response,
                            expected,
                            commandReturnedCancellationTask: false,
                            expectedLateDisposalFailure: null))
                    .ThenState<DurableCancellationModelState>(next => CopyCancellationState(expected, next));
            });
        spec.Operation<CancellationSlotCommand, CancellationObservation>(
            "DisposeRegistration",
            (request, state) =>
            {
                var expected = DisposeCancellationRegistration(state, request);
                return Expect.That<CancellationObservation>(
                        response => ValidateCancellationObservation(
                            response,
                            expected,
                            commandReturnedCancellationTask: false,
                            expectedLateDisposalFailure: null))
                    .ThenState<DurableCancellationModelState>(next => CopyCancellationState(expected, next));
            });
        spec.Operation<Unit, CancellationObservation>(
            "RequestCancellation",
            (_, state) =>
            {
                var expected = RequestCancellation(state);
                return Expect.That<CancellationObservation>(
                        response => ValidateCancellationObservation(
                            response,
                            expected,
                            commandReturnedCancellationTask: true,
                            expectedLateDisposalFailure: null))
                    .ThenState<DurableCancellationModelState>(next => CopyCancellationState(expected, next));
            });
        spec.Operation<CancellationRegistrationCommand, CancellationObservation>(
            "RegisterAfterCancellation",
            (request, state) =>
            {
                var expected = RegisterAfterCancellation(state, request);
                var lateFailure = request.Outcome == CallbackOutcome.Success
                    ? null
                    : FailureIdentity(request);
                return Expect.That<CancellationObservation>(
                        response => ValidateCancellationObservation(
                            response,
                            expected,
                            commandReturnedCancellationTask: false,
                            expectedLateDisposalFailure: lateFailure))
                    .ThenState<DurableCancellationModelState>(next => CopyCancellationState(expected, next));
            });
        spec.Operation<Unit, CancellationObservation>(
            "ObserveCancellation",
            (_, state) =>
                Expect.That<CancellationObservation>(
                        response => ValidateCancellationObservation(
                            response,
                            state,
                            commandReturnedCancellationTask: false,
                            expectedLateDisposalFailure: null))
                    .SameState());

        var registerBefore = spec.GetOperation<CancellationRegistrationCommand, CancellationObservation>(
            "RegisterBeforeCancellation");
        var disposeRegistration = spec.GetOperation<CancellationSlotCommand, CancellationObservation>(
            "DisposeRegistration");
        var requestCancellation = spec.GetOperation<Unit, CancellationObservation>("RequestCancellation");
        var registerAfter = spec.GetOperation<CancellationRegistrationCommand, CancellationObservation>(
            "RegisterAfterCancellation");
        var observeCancellation = spec.GetOperation<Unit, CancellationObservation>("ObserveCancellation");
        return spec.ExecuteWith<CancellationSystemUnderTest>()
            .BindAsync(registerBefore, static (sut, request) => sut.RegisterBeforeCancellationAsync(request))
            .BindAsync(disposeRegistration, static (sut, request) => sut.DisposeRegistrationAsync(request))
            .BindAsync(requestCancellation, static (sut, _) => sut.RequestCancellationAsync())
            .BindAsync(registerAfter, static (sut, request) => sut.RegisterAfterCancellationAsync(request))
            .BindAsync(observeCancellation, static (sut, _) => sut.ObserveCancellationAsync())
            .Done();
    }

    private static DurableCancellationModelState CreateInitialCancellationState()
        => new()
        {
            Registrations =
            [
                new CancellationRegistrationModel { Slot = 0 },
                new CancellationRegistrationModel { Slot = 1 },
                new CancellationRegistrationModel { Slot = 2 }
            ]
        };

    private static bool CanApplyCancellationCommand(OperationInput input, IState rawState)
    {
        var state = (DurableCancellationModelState)rawState;
        return input.Operation.Name switch
        {
            "RegisterBeforeCancellation" => input.Request is CancellationRegistrationCommand before
                && !state.CancellationRequested
                && IsUnusedCancellationSlot(state, before),
            "DisposeRegistration" => input.Request is CancellationSlotCommand dispose
                && TryGetCancellationSlot(state, dispose.Slot) is { Status: not CancellationRegistrationStatus.Unused, IsDisposed: false },
            "RequestCancellation" => true,
            "RegisterAfterCancellation" => input.Request is CancellationRegistrationCommand after
                && state.CancellationRequested
                && IsUnusedCancellationSlot(state, after),
            "ObserveCancellation" => true,
            _ => false
        };
    }

    private static bool ShouldIncludeCancellationTransition(IState rawState, OperationCall call, IState _)
    {
        var state = (DurableCancellationModelState)rawState;
        var used = state.Registrations.Count(
            registration => registration.Status != CancellationRegistrationStatus.Unused);
        return call.OperationInput.Operation.Name switch
        {
            "RegisterBeforeCancellation" => call.OperationInput.Request is CancellationRegistrationCommand register
                && (used == 0
                    || (used == 1
                        && register.Slot == 2
                        && GetCancellationSlot(state, 1).Status == CancellationRegistrationStatus.Active)),
            "DisposeRegistration" => !state.CancellationRequested && used == 1,
            "RequestCancellation" => !state.CancellationRequested || used == 1,
            "RegisterAfterCancellation" => state.CancellationRequested
                && (used == 0
                    || (call.OperationInput.Request is CancellationRegistrationCommand { Slot: 0 }
                        && GetCancellationSlot(state, 1).CallbackCount == 1
                        && GetCancellationSlot(state, 2).CallbackCount == 1)),
            "ObserveCancellation" => used == 0,
            _ => true
        };
    }

    private static bool IsUnusedCancellationSlot(
        DurableCancellationModelState state,
        CancellationRegistrationCommand command)
        => TryGetCancellationSlot(state, command.Slot) is
        {
            Status: CancellationRegistrationStatus.Unused
        } slot
        && ExpectedOutcomeForSlot(command.Slot) == command.Outcome
        && slot.Slot == command.Slot;

    private static CancellationRegistrationModel? TryGetCancellationSlot(
        DurableCancellationModelState state,
        int slot)
        => state.Registrations.SingleOrDefault(registration => registration.Slot == slot);

    private static CallbackOutcome ExpectedOutcomeForSlot(int slot)
        => slot switch
        {
            0 => CallbackOutcome.Success,
            1 => CallbackOutcome.ThrowInvalidOperation,
            2 => CallbackOutcome.ThrowArgument,
            _ => throw new ArgumentOutOfRangeException(nameof(slot))
        };

    private static DurableCancellationModelState RegisterBeforeCancellation(
        DurableCancellationModelState state,
        CancellationRegistrationCommand command)
    {
        var next = CloneCancellationState(state);
        var registration = GetCancellationSlot(next, command.Slot);
        registration.Outcome = command.Outcome;
        registration.Status = CancellationRegistrationStatus.Active;
        registration.RegistrationOrder = next.NextRegistrationOrder++;
        return next;
    }

    private static DurableCancellationModelState DisposeCancellationRegistration(
        DurableCancellationModelState state,
        CancellationSlotCommand command)
    {
        var next = CloneCancellationState(state);
        var registration = GetCancellationSlot(next, command.Slot);
        registration.IsDisposed = true;
        if (registration.Status == CancellationRegistrationStatus.Active)
        {
            registration.Status = CancellationRegistrationStatus.Disposed;
        }

        return next;
    }

    private static DurableCancellationModelState RequestCancellation(DurableCancellationModelState state)
    {
        var next = CloneCancellationState(state);
        next.CancellationRequestCount++;
        if (next.CancellationRequested)
        {
            return next;
        }

        next.CancellationRequested = true;
        next.TokenCanceled = true;
        next.HasFirstCancellationCompletion = true;
        var failures = new List<string>();
        foreach (var registration in next.Registrations
                     .Where(registration => registration.Status == CancellationRegistrationStatus.Active)
                     .OrderBy(registration => registration.RegistrationOrder))
        {
            registration.CallbackCount++;
            if (registration.Outcome == CallbackOutcome.Success)
            {
                registration.Status = CancellationRegistrationStatus.Invoked;
            }
            else
            {
                registration.Status = CancellationRegistrationStatus.Failed;
                failures.Add(FailureIdentity(registration.Slot, registration.Outcome));
            }
        }

        next.SharedFailureIdentities = failures;
        next.SharedCompletionStatus = failures.Count == 0
            ? CancellationCompletionStatus.Succeeded
            : CancellationCompletionStatus.Failed;
        return next;
    }

    private static DurableCancellationModelState RegisterAfterCancellation(
        DurableCancellationModelState state,
        CancellationRegistrationCommand command)
    {
        var next = CloneCancellationState(state);
        var registration = GetCancellationSlot(next, command.Slot);
        registration.Outcome = command.Outcome;
        registration.IsLate = true;
        registration.IsDisposed = true;
        registration.RegistrationOrder = next.NextRegistrationOrder++;
        registration.CallbackCount = 1;
        if (command.Outcome == CallbackOutcome.Success)
        {
            registration.Status = CancellationRegistrationStatus.Invoked;
        }
        else
        {
            registration.Status = CancellationRegistrationStatus.Failed;
            next.LateObservedFailureIdentities.Add(FailureIdentity(command));
        }

        return next;
    }

    private static CancellationRegistrationModel GetCancellationSlot(
        DurableCancellationModelState state,
        int slot)
        => state.Registrations.Single(registration => registration.Slot == slot);

    private static DurableCancellationModelState CloneCancellationState(DurableCancellationModelState state)
        => new()
        {
            CancellationRequested = state.CancellationRequested,
            TokenCanceled = state.TokenCanceled,
            HasFirstCancellationCompletion = state.HasFirstCancellationCompletion,
            SharedCompletionStatus = state.SharedCompletionStatus,
            SharedFailureIdentities = [.. state.SharedFailureIdentities],
            LateObservedFailureIdentities = [.. state.LateObservedFailureIdentities],
            NextRegistrationOrder = state.NextRegistrationOrder,
            CancellationRequestCount = state.CancellationRequestCount,
            Registrations = state.Registrations.Select(CloneCancellationRegistration).ToList()
        };

    private static CancellationRegistrationModel CloneCancellationRegistration(
        CancellationRegistrationModel registration)
        => new()
        {
            Slot = registration.Slot,
            Outcome = registration.Outcome,
            Status = registration.Status,
            IsDisposed = registration.IsDisposed,
            IsLate = registration.IsLate,
            RegistrationOrder = registration.RegistrationOrder,
            CallbackCount = registration.CallbackCount
        };

    private static void CopyCancellationState(
        DurableCancellationModelState source,
        DurableCancellationModelState destination)
    {
        destination.CancellationRequested = source.CancellationRequested;
        destination.TokenCanceled = source.TokenCanceled;
        destination.HasFirstCancellationCompletion = source.HasFirstCancellationCompletion;
        destination.SharedCompletionStatus = source.SharedCompletionStatus;
        destination.SharedFailureIdentities = [.. source.SharedFailureIdentities];
        destination.LateObservedFailureIdentities = [.. source.LateObservedFailureIdentities];
        destination.NextRegistrationOrder = source.NextRegistrationOrder;
        destination.CancellationRequestCount = source.CancellationRequestCount;
        destination.Registrations = source.Registrations.Select(CloneCancellationRegistration).ToList();
    }

    private static ValidationResult ValidateCancellationObservation(
        CancellationObservation actual,
        DurableCancellationModelState expected,
        bool commandReturnedCancellationTask,
        string? expectedLateDisposalFailure)
    {
        var failures = new List<string>();
        CheckEqual(
            failures,
            "context IsCancellationRequested",
            expected.CancellationRequested,
            actual.IsCancellationRequested);
        CheckEqual(
            failures,
            "token IsCancellationRequested",
            expected.TokenCanceled,
            actual.TokenIsCancellationRequested);
        CheckEqual(
            failures,
            "first cancellation completion captured",
            expected.HasFirstCancellationCompletion,
            actual.HasFirstCancellationTask);
        CheckEqual(
            failures,
            "command returned cancellation task",
            commandReturnedCancellationTask,
            actual.CommandReturnedCancellationTask);
        CheckEqual(
            failures,
            "returned task is first cancellation completion",
            commandReturnedCancellationTask,
            actual.ReturnedCancellationTaskIsFirst);
        CheckEqual(failures, "all request tasks share identity", true, actual.AllCancellationTasksAreFirst);
        CheckEqual(
            failures,
            "shared completion status",
            expected.SharedCompletionStatus,
            actual.SharedCompletionStatus);
        CheckEqual(
            failures,
            "shared AggregateException identity is stable",
            true,
            actual.SharedAggregateIdentityIsStable);
        CheckEqual(
            failures,
            "shared failure count",
            expected.SharedFailureIdentities.Count,
            actual.SharedFailureCount);
        CheckEqual(
            failures,
            "shared failure identities",
            string.Join("|", expected.SharedFailureIdentities),
            string.Join("|", actual.SharedFailureIdentities));
        CheckEqual(
            failures,
            "shared AggregateException type",
            expected.SharedCompletionStatus == CancellationCompletionStatus.Failed
                ? typeof(AggregateException).FullName
                : null,
            actual.SharedExceptionType);
        CheckEqual(
            failures,
            "shared AggregateException message",
            expected.SharedCompletionStatus == CancellationCompletionStatus.Failed
                ? "One or more cancellation observers failed. "
                    + string.Join(" ", expected.SharedFailureIdentities.Select(identity => $"({identity})"))
                : null,
            actual.SharedExceptionMessage);
        CheckEqual(
            failures,
            "late observed failure identities",
            string.Join("|", expected.LateObservedFailureIdentities),
            string.Join("|", actual.LateObservedFailureIdentities));
        CheckEqual(
            failures,
            "failure observed through late registration disposal",
            expectedLateDisposalFailure,
            actual.LastLateDisposalFailureIdentity);
        CheckEqual(failures, "unexpected adapter exception", null, actual.UnexpectedException);
        CheckEqual(
            failures,
            "registration slot count",
            expected.Registrations.Count,
            actual.Registrations.Count);

        var comparableCount = Math.Min(expected.Registrations.Count, actual.Registrations.Count);
        for (var index = 0; index < comparableCount; index++)
        {
            var expectedRegistration = expected.Registrations[index];
            var actualRegistration = actual.Registrations[index];
            var prefix = $"registration[{expectedRegistration.Slot}]";
            CheckEqual(failures, $"{prefix} slot", expectedRegistration.Slot, actualRegistration.Slot);
            CheckEqual(failures, $"{prefix} outcome", expectedRegistration.Outcome, actualRegistration.Outcome);
            CheckEqual(failures, $"{prefix} status", expectedRegistration.Status, actualRegistration.Status);
            CheckEqual(failures, $"{prefix} disposed", expectedRegistration.IsDisposed, actualRegistration.IsDisposed);
            CheckEqual(failures, $"{prefix} late", expectedRegistration.IsLate, actualRegistration.IsLate);
            CheckEqual(
                failures,
                $"{prefix} registration order",
                expectedRegistration.RegistrationOrder,
                actualRegistration.RegistrationOrder);
            CheckEqual(
                failures,
                $"{prefix} callback count",
                expectedRegistration.CallbackCount,
                actualRegistration.CallbackCount);
            CheckEqual(
                failures,
                $"{prefix} callback observed canceled token",
                expectedRegistration.CallbackCount > 0,
                actualRegistration.CallbackObservedCanceledToken);
            CheckEqual(
                failures,
                $"{prefix} callback observed ambient durable context",
                expectedRegistration.CallbackCount > 0,
                actualRegistration.CallbackObservedAmbientContext);
        }

        return failures.Count == 0
            ? ValidationResult.Valid()
            : ValidationResult.Invalid(string.Join(Environment.NewLine, failures));
    }

    private static void AssertRequiredCancellationTransitionsAreCovered(IList<SequentialTestCase> testCases)
    {
        var inputSequences = testCases
            .Select(testCase => testCase.OperationCalls.Select(call => call.OperationInput.Name).ToArray())
            .ToList();
        Assert.Contains(inputSequences, sequence => sequence[0] == "ObserveCancellation");
        Assert.Contains(
            inputSequences,
            sequence => ContainsContiguousSequence(sequence, "RequestCancellation", "ObserveCancellation"));
        Assert.Contains(
            inputSequences,
            sequence => ContainsContiguousSequence(
                sequence,
                "RegisterBeforeCancellation slot 0 success",
                "DisposeRegistration slot 0",
                "RequestCancellation"));
        Assert.True(
            inputSequences.Any(
                sequence =>
                    Enumerable.Range(0, Math.Max(0, sequence.Length - 2)).Any(index =>
                        sequence[index].StartsWith("RegisterBeforeCancellation slot ", StringComparison.Ordinal)
                        && sequence[index + 1].StartsWith("RegisterBeforeCancellation slot ", StringComparison.Ordinal)
                        && sequence.Skip(index).Take(2).All(
                            name => name.Contains("failure", StringComparison.Ordinal))
                        && sequence[index + 2] == "RequestCancellation")),
            "Missing multiple-failure request sequence."
            + Environment.NewLine
            + string.Join(
                Environment.NewLine,
                inputSequences.Select(sequence => string.Join(" -> ", sequence))));
        Assert.Contains(
            inputSequences,
            sequence =>
                Enumerable.Range(0, Math.Max(0, sequence.Length - 2)).Any(index =>
                    sequence[index].StartsWith("RegisterBeforeCancellation slot ", StringComparison.Ordinal)
                    && sequence[index + 1] == "RequestCancellation"
                    && sequence[index + 2] == "RequestCancellation"));
        Assert.Contains(
            inputSequences,
            sequence => ContainsContiguousSequence(
                sequence,
                "RequestCancellation",
                "RegisterAfterCancellation slot 0 success"));
        Assert.Contains(
            inputSequences,
            sequence => ContainsContiguousSequence(
                sequence,
                "RequestCancellation",
                "RegisterAfterCancellation slot 1 invalid-operation failure"));
    }

    private static bool ContainsContiguousSequence(string[] sequence, params string[] expected)
        => Enumerable.Range(0, Math.Max(0, sequence.Length - expected.Length + 1))
            .Any(index => sequence.Skip(index).Take(expected.Length).SequenceEqual(expected));

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

    private static string FailureIdentity(CancellationRegistrationCommand command)
        => FailureIdentity(command.Slot, command.Outcome);

    private static string FailureIdentity(int slot, CallbackOutcome outcome)
        => $"slot-{slot}:{outcome}";

    private sealed record CancellationRegistrationCommand(int Slot, CallbackOutcome Outcome)
    {
        public override string ToString() => $"slot={Slot}, outcome={Outcome}";
    }

    private sealed record CancellationSlotCommand(int Slot)
    {
        public override string ToString() => $"slot={Slot}";
    }

    private sealed class CancellationSystemUnderTest
    {
        private readonly TestContext context;
        private readonly RuntimeCancellationRegistration[] registrations =
        [
            new(0),
            new(1),
            new(2)
        ];
        private readonly List<string> lateObservedFailureIdentities = [];
        private Task? firstCancellationTask;
        private AggregateException? sharedAggregate;
        private bool allCancellationTasksAreFirst = true;
        private bool sharedAggregateIdentityIsStable = true;
        private int nextRegistrationOrder;
        private string? lastLateDisposalFailureIdentity;
        private string? unexpectedException;

        public CancellationSystemUnderTest()
        {
            var host = new TestHost(DateTimeOffset.UnixEpoch);
            context = host.CreateContext(TaskId.CreateRoot("accordant-cancellation"));
        }

        public async Task<CancellationObservation> RegisterBeforeCancellationAsync(
            CancellationRegistrationCommand command)
        {
            ResetCommandObservation();
            try
            {
                var registration = registrations[command.Slot];
                PrepareRegistration(registration, command, isLate: false);
                registration.Registration = await context.RegisterCancellationCallbackAsync(
                    token => InvokeCallback(registration, token));
            }
            catch (Exception exception)
            {
                unexpectedException = DescribeException(exception);
            }

            return Observe(commandReturnedCancellationTask: false, returnedTaskIsFirst: false);
        }

        public async Task<CancellationObservation> DisposeRegistrationAsync(CancellationSlotCommand command)
        {
            ResetCommandObservation();
            try
            {
                var registration = registrations[command.Slot];
                await registration.Registration!.DisposeAsync();
                registration.IsDisposed = true;
                if (registration.Status == CancellationRegistrationStatus.Active)
                {
                    registration.Status = CancellationRegistrationStatus.Disposed;
                }
            }
            catch (Exception exception)
            {
                unexpectedException = DescribeException(exception);
            }

            return Observe(commandReturnedCancellationTask: false, returnedTaskIsFirst: false);
        }

        public async Task<CancellationObservation> RequestCancellationAsync()
        {
            ResetCommandObservation();
            var returnedTaskIsFirst = false;
            try
            {
                var cancellation = DurableTaskRuntimeHelper.RequestCancellationAsync(context);
                if (firstCancellationTask is null)
                {
                    firstCancellationTask = cancellation;
                }

                returnedTaskIsFirst = ReferenceEquals(firstCancellationTask, cancellation);
                allCancellationTasksAreFirst &= returnedTaskIsFirst;
                await cancellation;
            }
            catch (AggregateException exception)
            {
                if (sharedAggregate is null)
                {
                    sharedAggregate = exception;
                }
                else
                {
                    sharedAggregateIdentityIsStable &= ReferenceEquals(sharedAggregate, exception);
                }
            }
            catch (Exception exception)
            {
                unexpectedException = DescribeException(exception);
            }

            return Observe(commandReturnedCancellationTask: true, returnedTaskIsFirst);
        }

        public async Task<CancellationObservation> RegisterAfterCancellationAsync(
            CancellationRegistrationCommand command)
        {
            ResetCommandObservation();
            try
            {
                var registration = registrations[command.Slot];
                PrepareRegistration(registration, command, isLate: true);
                registration.Registration = await context.RegisterCancellationCallbackAsync(
                    token => InvokeCallback(registration, token));
                try
                {
                    await registration.Registration.DisposeAsync();
                }
                catch (Exception exception) when (ReferenceEquals(exception, registration.Failure))
                {
                    lastLateDisposalFailureIdentity = FailureIdentity(command);
                    lateObservedFailureIdentities.Add(lastLateDisposalFailureIdentity);
                }

                registration.IsDisposed = true;
            }
            catch (Exception exception)
            {
                unexpectedException = DescribeException(exception);
            }

            return Observe(commandReturnedCancellationTask: false, returnedTaskIsFirst: false);
        }

        public Task<CancellationObservation> ObserveCancellationAsync()
        {
            ResetCommandObservation();
            return Task.FromResult(Observe(commandReturnedCancellationTask: false, returnedTaskIsFirst: false));
        }

        private void PrepareRegistration(
            RuntimeCancellationRegistration registration,
            CancellationRegistrationCommand command,
            bool isLate)
        {
            registration.Outcome = command.Outcome;
            registration.Status = CancellationRegistrationStatus.Active;
            registration.IsLate = isLate;
            registration.RegistrationOrder = nextRegistrationOrder++;
            registration.Failure = command.Outcome switch
            {
                CallbackOutcome.Success => null,
                CallbackOutcome.ThrowInvalidOperation => new InvalidOperationException(FailureIdentity(command)),
                CallbackOutcome.ThrowArgument => new ArgumentException(FailureIdentity(command)),
                _ => throw new ArgumentOutOfRangeException(nameof(command))
            };
        }

        private ValueTask InvokeCallback(RuntimeCancellationRegistration registration, CancellationToken token)
        {
            registration.CallbackCount++;
            registration.CallbackObservedCanceledToken |= token.IsCancellationRequested;
            registration.CallbackObservedAmbientContext |= ReferenceEquals(
                context,
                DurableExecutionContext.Current);
            if (registration.Failure is { } failure)
            {
                registration.Status = CancellationRegistrationStatus.Failed;
                throw failure;
            }

            registration.Status = CancellationRegistrationStatus.Invoked;
            return ValueTask.CompletedTask;
        }

        private CancellationObservation Observe(
            bool commandReturnedCancellationTask,
            bool returnedTaskIsFirst)
        {
            var sharedFailureIdentities = sharedAggregate is null
                ? []
                : sharedAggregate.InnerExceptions.Select(GetFailureIdentity).ToArray();
            return new CancellationObservation(
                IsCancellationRequested: context.IsCancellationRequested,
                TokenIsCancellationRequested: context.CancellationToken.IsCancellationRequested,
                HasFirstCancellationTask: firstCancellationTask is not null,
                CommandReturnedCancellationTask: commandReturnedCancellationTask,
                ReturnedCancellationTaskIsFirst: returnedTaskIsFirst,
                AllCancellationTasksAreFirst: allCancellationTasksAreFirst,
                SharedCompletionStatus: GetSharedCompletionStatus(),
                SharedAggregateIdentityIsStable: sharedAggregateIdentityIsStable,
                SharedFailureCount: sharedFailureIdentities.Length,
                SharedFailureIdentities: Array.AsReadOnly(sharedFailureIdentities),
                SharedExceptionType: sharedAggregate?.GetType().FullName,
                SharedExceptionMessage: sharedAggregate?.Message,
                LateObservedFailureIdentities: Array.AsReadOnly(lateObservedFailureIdentities.ToArray()),
                LastLateDisposalFailureIdentity: lastLateDisposalFailureIdentity,
                UnexpectedException: unexpectedException,
                Registrations: Array.AsReadOnly(registrations.Select(ObserveRegistration).ToArray()));
        }

        private CancellationCompletionStatus GetSharedCompletionStatus()
        {
            if (firstCancellationTask is null)
            {
                return CancellationCompletionStatus.NotRequested;
            }

            if (firstCancellationTask.IsCompletedSuccessfully)
            {
                return CancellationCompletionStatus.Succeeded;
            }

            return firstCancellationTask.IsFaulted
                ? CancellationCompletionStatus.Failed
                : CancellationCompletionStatus.Pending;
        }

        private string GetFailureIdentity(Exception exception)
        {
            var registration = registrations.SingleOrDefault(
                candidate => ReferenceEquals(candidate.Failure, exception));
            return registration is null
                ? $"unrecognized:{DescribeException(exception)}"
                : FailureIdentity(registration.Slot, registration.Outcome);
        }

        private static CancellationRegistrationObservation ObserveRegistration(
            RuntimeCancellationRegistration registration)
            => new(
                Slot: registration.Slot,
                Outcome: registration.Outcome,
                Status: registration.Status,
                IsDisposed: registration.IsDisposed,
                IsLate: registration.IsLate,
                RegistrationOrder: registration.RegistrationOrder,
                CallbackCount: registration.CallbackCount,
                CallbackObservedCanceledToken: registration.CallbackObservedCanceledToken,
                CallbackObservedAmbientContext: registration.CallbackObservedAmbientContext);

        private void ResetCommandObservation()
        {
            lastLateDisposalFailureIdentity = null;
            unexpectedException = null;
        }

        private static string DescribeException(Exception exception)
            => $"{exception.GetType().FullName}: {exception.Message}";

        private sealed class RuntimeCancellationRegistration(int slot)
        {
            public int Slot { get; } = slot;
            public CallbackOutcome Outcome { get; set; }
            public CancellationRegistrationStatus Status { get; set; }
            public bool IsDisposed { get; set; }
            public bool IsLate { get; set; }
            public int RegistrationOrder { get; set; } = -1;
            public int CallbackCount { get; set; }
            public bool CallbackObservedCanceledToken { get; set; }
            public bool CallbackObservedAmbientContext { get; set; }
            public Exception? Failure { get; set; }
            public IAsyncDisposable? Registration { get; set; }
        }
    }

    private sealed record CancellationObservation(
        bool IsCancellationRequested,
        bool TokenIsCancellationRequested,
        bool HasFirstCancellationTask,
        bool CommandReturnedCancellationTask,
        bool ReturnedCancellationTaskIsFirst,
        bool AllCancellationTasksAreFirst,
        CancellationCompletionStatus SharedCompletionStatus,
        bool SharedAggregateIdentityIsStable,
        int SharedFailureCount,
        IReadOnlyList<string> SharedFailureIdentities,
        string? SharedExceptionType,
        string? SharedExceptionMessage,
        IReadOnlyList<string> LateObservedFailureIdentities,
        string? LastLateDisposalFailureIdentity,
        string? UnexpectedException,
        IReadOnlyList<CancellationRegistrationObservation> Registrations);

    private sealed record CancellationRegistrationObservation(
        int Slot,
        CallbackOutcome Outcome,
        CancellationRegistrationStatus Status,
        bool IsDisposed,
        bool IsLate,
        int RegistrationOrder,
        int CallbackCount,
        bool CallbackObservedCanceledToken,
        bool CallbackObservedAmbientContext);
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

internal enum CallbackOutcome
{
    Success,
    ThrowInvalidOperation,
    ThrowArgument
}

internal enum CancellationRegistrationStatus
{
    Unused,
    Active,
    Disposed,
    Invoked,
    Failed
}

internal enum CancellationCompletionStatus
{
    NotRequested,
    Pending,
    Succeeded,
    Failed
}

[State]
internal partial class DurableCancellationModelState : State
{
    public bool CancellationRequested { get; set; }
    public bool TokenCanceled { get; set; }
    public bool HasFirstCancellationCompletion { get; set; }
    public CancellationCompletionStatus SharedCompletionStatus { get; set; }
    public List<string> SharedFailureIdentities { get; set; } = [];
    public List<string> LateObservedFailureIdentities { get; set; } = [];
    public int NextRegistrationOrder { get; set; }
    public int CancellationRequestCount { get; set; }
    public List<CancellationRegistrationModel> Registrations { get; set; } = [];
}

[State]
internal partial class CancellationRegistrationModel : State
{
    public int Slot { get; set; }
    public CallbackOutcome Outcome { get; set; }
    public CancellationRegistrationStatus Status { get; set; }
    public bool IsDisposed { get; set; }
    public bool IsLate { get; set; }
    public int RegistrationOrder { get; set; } = -1;
    public int CallbackCount { get; set; }
}
