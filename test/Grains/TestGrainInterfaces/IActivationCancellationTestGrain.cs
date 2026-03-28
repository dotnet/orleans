#nullable enable

namespace UnitTests.GrainInterfaces;

/// <summary>
/// Interface for testing activation cancellation scenarios.
/// These grains are used to verify the proper handling of cancellation during grain activation.
/// </summary>
public interface IActivationCancellationTestGrain : IGrainWithGuidKey
{
    /// <summary>
    /// A simple method to test that the grain is activated and working.
    /// </summary>
    Task<string> GetActivationId();

    /// <summary>
    /// Checks if the activation was successful.
    /// </summary>
    Task<bool> IsActivated();
}

/// <summary>
/// Grain that throws OperationCanceledException during OnActivateAsync when the cancellation token is triggered.
/// This simulates code that properly observes the cancellation token.
/// </summary>
public interface IActivationCancellation_ThrowsOperationCancelledGrain : IActivationCancellationTestGrain;

/// <summary>
/// Grain that throws ObjectDisposedException during OnActivateAsync when trying to access disposed services.
/// This simulates code that doesn't observe the cancellation token but tries to access services that have been disposed.
/// </summary>
public interface IActivationCancellation_ThrowsObjectDisposedGrain : IActivationCancellationTestGrain;

/// <summary>
/// Grain that throws a generic exception during OnActivateAsync (not related to cancellation).
/// This is used to verify that non-cancellation exceptions are still handled properly.
/// </summary>
public interface IActivationCancellation_ThrowsGenericExceptionGrain : IActivationCancellationTestGrain;

/// <summary>
/// Grain that activates successfully without any issues.
/// This is a baseline to verify normal activation continues to work.
/// </summary>
public interface IActivationCancellation_SuccessfulActivationGrain : IActivationCancellationTestGrain;

/// <summary>
/// Grain that throws TaskCanceledException during OnActivateAsync.
/// TaskCanceledException inherits from OperationCanceledException and should be handled the same way.
/// </summary>
public interface IActivationCancellation_ThrowsTaskCancelledGrain : IActivationCancellationTestGrain;

/// <summary>
/// Grain that throws ObjectDisposedException unconditionally (not due to cancellation).
/// This tests that ObjectDisposedException thrown for other reasons is NOT treated as cancellation.
/// </summary>
public interface IActivationCancellation_ThrowsObjectDisposedUnconditionallyGrain : IActivationCancellationTestGrain;

/// <summary>
/// Grain that throws OperationCanceledException unconditionally (not due to cancellation).
/// This tests that OperationCanceledException thrown for other reasons is NOT treated as cancellation.
/// </summary>
public interface IActivationCancellation_ThrowsOperationCancelledUnconditionallyGrain : IActivationCancellationTestGrain;
