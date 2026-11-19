// -----------------------------------------------------------------------------
// Project: TechConnect.Client.Tests
// File:    StaTask.cs
//
// Purpose:
// Runs WinForms UI test work on a dedicated STA thread with a message loop so
// ConfigureAwait(true) continuations can complete under xUnit's MTA threads.
//
// -----------------------------------------------------------------------------

namespace TechConnect.Client.Tests.Support;

/// <summary>
/// Runs work on a dedicated STA thread with a WinForms message loop so
/// <c>ConfigureAwait(true)</c> UI continuations can complete.
/// </summary>
/// <remarks>
/// Limitation: each call starts a new background STA thread and pumps until
/// <see cref="Application.ExitThread"/>; not a general-purpose dispatcher.
/// </remarks>
internal static class StaTask
{
    /// <summary>
    /// Runs a synchronous action on an STA thread with a WinForms message loop.
    /// </summary>
    /// <param name="action">Work to execute on the STA thread.</param>
    /// <returns>A task that completes when the action finishes or faults.</returns>
    public static Task Run(Action action) =>
        Run(() =>
        {
            action();
            return Task.CompletedTask;
        });

    /// <summary>
    /// Runs an asynchronous action on an STA thread with a WinForms message loop.
    /// Continuations using <c>ConfigureAwait(true)</c> resume on that thread.
    /// </summary>
    /// <param name="action">Async work to execute on the STA thread.</param>
    /// <returns>A task that completes when the action finishes or faults.</returns>
    public static Task Run(Func<Task> action)
    {
        ArgumentNullException.ThrowIfNull(action);

        var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            try
            {
                async void Start()
                {
                    try
                    {
                        await action().ConfigureAwait(true);
                        tcs.TrySetResult();
                    }
                    catch (Exception ex)
                    {
                        tcs.TrySetException(ex);
                    }
                    finally
                    {
                        Application.ExitThread();
                    }
                }

                WindowsFormsSynchronizationContext.AutoInstall = true;
                Start();
                Application.Run();
            }
            catch (Exception ex)
            {
                tcs.TrySetException(ex);
            }
        })
        {
            IsBackground = true,
            Name = "TechConnect.Client-STA-Test"
        };

        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        return tcs.Task;
    }
}
