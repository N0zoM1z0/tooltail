using System.Diagnostics;

namespace Tooltail.Adapters.AgentEvents.Codex;

internal interface ICodexChildProcess : IAsyncDisposable
{
    Stream StandardInput { get; }

    Stream StandardOutput { get; }

    Stream StandardError { get; }

    int ExitCode { get; }

    Task WaitForExitAsync();

    void KillTree();
}

internal interface ICodexProcessLauncher
{
    ICodexChildProcess Start(ProcessStartInfo startInfo);
}

internal sealed class SystemCodexProcessLauncher : ICodexProcessLauncher
{
    public ICodexChildProcess Start(ProcessStartInfo startInfo)
    {
        var process = new Process { StartInfo = startInfo };
        try
        {
            if (!process.Start())
            {
                throw new InvalidOperationException("The configured Codex process did not start.");
            }

            return new SystemCodexChildProcess(process);
        }
        catch
        {
            process.Dispose();
            throw;
        }
    }
}
internal sealed class SystemCodexChildProcess(Process process) : ICodexChildProcess
{
    public Stream StandardInput => process.StandardInput.BaseStream;

    public Stream StandardOutput => process.StandardOutput.BaseStream;

    public Stream StandardError => process.StandardError.BaseStream;

    public int ExitCode => process.ExitCode;

    public Task WaitForExitAsync() => process.WaitForExitAsync();

    public void KillTree()
    {
        if (!process.HasExited)
        {
            process.Kill(entireProcessTree: true);
        }
    }

    public ValueTask DisposeAsync()
    {
        process.Dispose();
        return ValueTask.CompletedTask;
    }
}
