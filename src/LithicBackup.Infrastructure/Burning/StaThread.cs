namespace LithicBackup.Infrastructure.Burning;

/// <summary>
/// Runs delegates on a dedicated STA thread. IMAPI2 COM objects require
/// STA apartment state, and long operations (burn, erase) must not block
/// the WPF UI thread.
/// </summary>
internal static class StaThread
{
    /// <summary>Run <paramref name="action"/> on a new STA thread and return the result.</summary>
    public static Task<T> RunAsync<T>(Func<T> action)
    {
        var tcs = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);

        var thread = new Thread(() =>
        {
            try
            {
                tcs.SetResult(action());
            }
            catch (OperationCanceledException)
            {
                tcs.SetCanceled();
            }
            catch (Exception ex)
            {
                tcs.SetException(ex);
            }
        });

        thread.SetApartmentState(ApartmentState.STA);
        thread.IsBackground = true;
        thread.Start();

        return tcs.Task;
    }

    /// <summary>Run <paramref name="action"/> on a new STA thread.</summary>
    public static Task RunAsync(Action action)
    {
        var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var thread = new Thread(() =>
        {
            try
            {
                action();
                tcs.SetResult();
            }
            catch (OperationCanceledException)
            {
                tcs.SetCanceled();
            }
            catch (Exception ex)
            {
                tcs.SetException(ex);
            }
        });

        thread.SetApartmentState(ApartmentState.STA);
        thread.IsBackground = true;
        thread.Start();

        return tcs.Task;
    }
}
