using System.Diagnostics;
using System.Text;

namespace VideoEdit.Services;

public sealed class ProcessRunner : IProcessRunner, IDisposable
{
    private readonly object _gate = new();
    private readonly HashSet<Process> _live = [];
    private bool _disposed;

    public async Task<ProcessResult> RunAsync(
        string fileName,
        IReadOnlyList<string> arguments,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        cancellationToken.ThrowIfCancellationRequested();

        // Validate the timeout before starting a process. Otherwise CancelAfter
        // can throw while leaving a child running outside the cleanup block.
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(timeout);

        var startInfo = new ProcessStartInfo
        {
            FileName = fileName,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        using var process = new Process { StartInfo = startInfo, EnableRaisingEvents = true };
        var stdout = new StringBuilder();
        var stderr = new StringBuilder();
        process.OutputDataReceived += (_, args) =>
        {
            if (args.Data is not null)
            {
                stdout.AppendLine(args.Data);
            }
        };
        process.ErrorDataReceived += (_, args) =>
        {
            if (args.Data is not null)
            {
                stderr.AppendLine(args.Data);
            }
        };

        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            timeoutCts.Token.ThrowIfCancellationRequested();
            if (!process.Start())
            {
                throw VideoEditException.Conflict($"Failed to start {fileName}.");
            }

            _live.Add(process);
        }

        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        try
        {
            await process.WaitForExitAsync(timeoutCts.Token);
        }
        catch (OperationCanceledException)
        {
            Kill(process);
            await process.WaitForExitAsync(CancellationToken.None);
            throw;
        }
        finally
        {
            lock (_gate)
            {
                _live.Remove(process);
            }
        }

        return new ProcessResult(process.ExitCode, stdout.ToString(), stderr.ToString());
    }

    public void Dispose()
    {
        List<Process> live;
        lock (_gate)
        {
            _disposed = true;
            live = _live.ToList();
            _live.Clear();
        }

        foreach (var process in live)
        {
            Kill(process);
        }
    }

    private static void Kill(Process? process)
    {
        try
        {
            if (process is { HasExited: false })
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch (InvalidOperationException)
        {
        }
    }
}
