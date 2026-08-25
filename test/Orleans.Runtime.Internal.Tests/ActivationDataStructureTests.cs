using System.Reflection;
using System.Runtime.CompilerServices;
using Orleans.Runtime;
using Xunit;

namespace UnitTests;

[TestSuite("BVT")]
[TestArea("Runtime")]
public class ActivationDataStructureTests
{
    private const BindingFlags InstanceFields = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
    private const BindingFlags DeclaredMethods =
        BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly;
    private static readonly Type ActivationType = typeof(ActivationData);

    [Fact]
    public void EmbeddedSubsystems_AreMutableValueTypesWithoutOwnerReferences()
    {
        foreach (var fieldName in new[] { "_requests", "_messagePump", "_operations" })
        {
            var field = Assert.IsAssignableFrom<FieldInfo>(ActivationType.GetField(fieldName, InstanceFields));
            var subsystemType = field.FieldType;

            Assert.True(subsystemType.IsValueType, $"{subsystemType} must be embedded in ActivationData.");
            Assert.False(field.IsInitOnly, $"{subsystemType} must remain mutable.");
            Assert.DoesNotContain(
                subsystemType.GetFields(InstanceFields),
                candidate => candidate.FieldType == ActivationType);
            Assert.DoesNotContain(
                subsystemType.GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic),
                method => method.GetCustomAttribute<AsyncStateMachineAttribute>() is not null);
        }
    }

    [Fact]
    public void RecursiveReferenceSlotCount_DoesNotExceedPreRefactorLayout()
    {
        const int PreRefactorReferenceSlots = 15;

        var referenceSlots = CountReferenceSlots(ActivationType);

        Assert.True(
            referenceSlots <= PreRefactorReferenceSlots,
            $"ActivationData has {referenceSlots} recursive reference slots; the pre-refactor layout had {PreRefactorReferenceSlots}.");
    }

    [Fact]
    public void Extras_ComponentDictionaryIsAllocatedOnlyWhenAComponentIsAdded()
    {
        var extrasType = Assert.Single(
            ActivationType.GetNestedTypes(BindingFlags.NonPublic),
            type => type.Name == "ActivationDataExtras");
        var componentsField = Assert.IsAssignableFrom<FieldInfo>(extrasType.GetField("_components", InstanceFields));
        var extras = Activator.CreateInstance(extrasType, nonPublic: true);
        Assert.NotNull(extras);

        Assert.Null(componentsField.GetValue(extras));

        var tryGetComponent = Assert.IsAssignableFrom<MethodInfo>(
            extrasType.GetMethod("TryGetComponent", BindingFlags.Instance | BindingFlags.Public));
        var arguments = new object?[] { typeof(IDisposable), null };
        Assert.False((bool)tryGetComponent.Invoke(extras, arguments)!);
        Assert.Null(componentsField.GetValue(extras));

        var setComponent = Assert.IsAssignableFrom<MethodInfo>(
            extrasType.GetMethod("SetComponent", BindingFlags.Instance | BindingFlags.Public));
        setComponent.Invoke(extras, new object[] { typeof(IDisposable), new MemoryStream() });
        Assert.NotNull(componentsField.GetValue(extras));
    }

    [Fact]
    public void RequestScheduler_WorkloadCollectionsExposeConcreteTypes()
    {
        var schedulerType = Assert.Single(
            ActivationType.GetNestedTypes(BindingFlags.NonPublic),
            type => type.Name == "RequestScheduler");

        Assert.Equal(
            typeof(List<(Message Message, CoarseStopwatch QueuedTime)>),
            schedulerType.GetProperty("Waiting")?.PropertyType);
        Assert.Equal(
            typeof(Dictionary<Message, CoarseStopwatch>),
            schedulerType.GetProperty("Running")?.PropertyType);
    }

    [Theory]
    [InlineData("ActivateAsync", typeof(Task), 4)]
    [InlineData("FinishDeactivating", typeof(Task), 3)]
    [InlineData("StartMigrationAsync", typeof(ValueTask<bool>), 4)]
    [InlineData("MigrateOnIdleAsync", typeof(ValueTask), 1)]
    [InlineData("PlaceMigratingGrainAsync", typeof(ValueTask<SiloAddress>), 3)]
    [InlineData("RunMessageLoop", typeof(Task), 1)]
    [InlineData("RetryCancellationAfterDelay", typeof(ValueTask), 3)]
    [InlineData("ProcessOperationsAsync", typeof(Task), 1)]
    [InlineData("DisposeAsync", typeof(ValueTask), 1)]
    public void AsyncCoordinator_IsStaticAndTakesActivation(
        string methodName,
        Type returnType,
        int parameterCount)
    {
        var method = Assert.Single(
            ActivationType.GetMethods(DeclaredMethods),
            candidate => candidate.Name == methodName
                && candidate.ReturnType == returnType
                && candidate.GetParameters() is [{ ParameterType: var firstParameter }, ..]
                && firstParameter == ActivationType
                && candidate.GetParameters().Length == parameterCount);

        Assert.True(method.IsStatic);
        Assert.NotNull(method.GetCustomAttribute<AsyncStateMachineAttribute>());
    }

    private static int CountReferenceSlots(Type type)
    {
        var result = 0;
        foreach (var field in type.GetFields(InstanceFields | BindingFlags.DeclaredOnly))
        {
            if (!field.FieldType.IsValueType)
            {
                result++;
            }
            else if (!field.FieldType.IsPrimitive && !field.FieldType.IsEnum)
            {
                result += CountReferenceSlots(field.FieldType);
            }
        }

        return result;
    }
}
