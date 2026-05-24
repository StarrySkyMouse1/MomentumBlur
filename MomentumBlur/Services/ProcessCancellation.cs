using System.Diagnostics;

namespace mmod_record.Services;

public static class ProcessCancellation
{
    public static async Task WaitForExitOrKillAsync(
        Process process,
        CancellationToken cancellationToken,
        TimeSpan? timeout = null)
    {
        try
        {
            using var timeoutCts = timeout is null
                ? null
                : new CancellationTokenSource(timeout.Value);
            using var linkedCts = timeoutCts is null
                ? CancellationTokenSource.CreateLinkedTokenSource(cancellationToken)
                : CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutCts.Token);

            await process.WaitForExitAsync(linkedCts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            KillTree(process);
            throw;
        }
        catch (OperationCanceledException) when (timeout is not null)
        {
            KillTree(process);
            throw new TimeoutException($"Process timed out after {timeout.Value}.");
        }
    }

    public static void KillTree(Process? process)
    {
        if (process is null)
            return;

        try
        {
            if (!process.HasExited)
                process.Kill(entireProcessTree: true);
        }
        catch
        {
            // Process may already have exited or access may be denied.
        }
    }
}
