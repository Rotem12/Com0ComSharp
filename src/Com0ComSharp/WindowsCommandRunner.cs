using System.ComponentModel;
using System.Diagnostics;
using System.Text;
using System.Runtime.InteropServices;

namespace Com0ComSharp;

public interface ICom0ComCommandRunner
{
    Task<CommandResult> RunAsync(DriverPackage package, Com0ComCommand command, ClientOptions options, CancellationToken cancellationToken = default);
}

public sealed class WindowsCommandRunner : ICom0ComCommandRunner
{
    public async Task<CommandResult> RunAsync(DriverPackage package, Com0ComCommand command, ClientOptions options, CancellationToken cancellationToken = default)
    {
        WindowsDiagnostics.EnsureWindows();
        var arguments = command.ToArguments();
        cancellationToken.ThrowIfCancellationRequested();
        var elevated = WindowsDiagnostics.IsAdministrator();
        if (!elevated && options.Elevation == ElevationMode.RequireAdministrator)
            return new(command.Operation, 740, "Administrator access is required. Run from your elevated installer or select ElevationMode.Prompt.", FailureKind.ElevationRequired);
        if (command.Operation == Com0ComOperation.CreateNamedPair)
        {
            if (!elevated)
                return new(command.Operation, 740, "Named pair creation requires Com0ComSharp.Tool.exe for one elevation request. Set ElevationHelperPath or run from an elevated installer.", FailureKind.ElevationRequired);
            using var setupTimeout = new CancellationTokenSource(options.Timeout);
            using var setupCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, setupTimeout.Token);
            var result = await NamedPairSetup.RunAsync(command,
                (step, token) => RunAsync(package, step, options, token),
                WindowsDiagnostics.GetPairs, WindowsDiagnostics.GetUnavailableComPortNames, WindowsDiagnostics.GetDevices, setupCancellation.Token).ConfigureAwait(false);
            return result.Failure == FailureKind.Cancelled && setupTimeout.IsCancellationRequested && !cancellationToken.IsCancellationRequested
                ? result with { Failure = FailureKind.TimedOut } : result;
        }
        var directory = RuntimeCompatibility.CreateTemporaryDirectory("Com0ComSharp-");
        var log = Path.Combine(directory, "setup.log");
        // Reserve a private random path before an elevated process is given it.
        using (File.Create(log)) { }
        Process? process = null;
        var canDelete = true;
        try
        {
            var start = BuildStartInfo(package, arguments, log, !elevated);
            try { process = await Task.Run(() => Process.Start(start), CancellationToken.None).ConfigureAwait(false); }
            catch (Win32Exception e)
            {
                return new(command.Operation, e.NativeErrorCode, e.Message, e.NativeErrorCode switch
                {
                    1223 => FailureKind.ElevationDenied, 740 => FailureKind.ElevationRequired,
                    5 => FailureKind.AccessDenied, _ => FailureKind.ProcessFailed
                });
            }
            if (process is null) return new(command.Operation, -1, "Windows did not start setupc.", FailureKind.ProcessFailed);
            // Drain redirected pipes concurrently even though normal setupc diagnostics go to the log.
            var stdout = elevated ? process.StandardOutput.ReadToEndAsync() : Task.FromResult("");
            var stderr = elevated ? process.StandardError.ReadToEndAsync() : Task.FromResult("");
            using var timeout = new CancellationTokenSource(options.Timeout);
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeout.Token);
            try { await RuntimeCompatibility.WaitForExitAsync(process, linked.Token).ConfigureAwait(false); }
            catch (OperationCanceledException)
            {
                var note = "Operation interrupted. Driver changes already made are not rolled back. Inspect devices before retrying.";
                try { if (!process.HasExited) RuntimeCompatibility.KillProcess(process); await RuntimeCompatibility.WaitForExitAsync(process).ConfigureAwait(false); }
                catch (Win32Exception) { canDelete = false; note += " Windows prevented stopping the elevated process; it may still be running."; }
                catch (InvalidOperationException) { }
                return new(command.Operation, -1, note, cancellationToken.IsCancellationRequested ? FailureKind.Cancelled : FailureKind.TimedOut);
            }
            var output = (elevated ? "" : ReadLog(log)) + await stdout.ConfigureAwait(false) + await stderr.ConfigureAwait(false);
            if (process.ExitCode == 0 && command.WaitSeconds > 0 && command.Operation is not (Com0ComOperation.Help or Com0ComOperation.List or Com0ComOperation.ListFriendlyNames or Com0ComOperation.BusyNames))
            {
                // setupc's --wait would overflow its eight-slot parser for indexed
                // installs with --output. Use the same Windows PnP completion API.
                var wait = Task.Run(() => CMP_WaitNoPendingInstallEvents(checked((uint)command.WaitSeconds * 1000)));
                try
                {
                    var pending = await RuntimeCompatibility.WaitAsync(wait, linked.Token).ConfigureAwait(false);
                    if (pending != 0) return new(command.Operation, checked((int)pending), output + "\nWindows PnP completion was not confirmed. Inspect devices before retrying.", FailureKind.TimedOut);
                }
                catch (OperationCanceledException)
                {
                    return new(command.Operation, -1, output + "\nStopped waiting for PnP completion; native changes remain.", cancellationToken.IsCancellationRequested ? FailureKind.Cancelled : FailureKind.TimedOut);
                }
            }
            return CommandResult.FromExit(command.Operation, process.ExitCode, output);
        }
        finally
        {
            process?.Dispose();
            if (canDelete)
            {
                try { File.Delete(log); Directory.Delete(directory); }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
            }
        }
    }

    internal static ProcessStartInfo BuildStartInfo(DriverPackage package, IReadOnlyList<string> arguments, string log, bool elevate)
    {
        var start = new ProcessStartInfo(package.SetupExecutable)
        {
            WorkingDirectory = package.DirectoryPath, UseShellExecute = elevate, CreateNoWindow = !elevate,
            WindowStyle = ProcessWindowStyle.Hidden, RedirectStandardOutput = !elevate, RedirectStandardError = !elevate
        };
        var nativeArguments = new List<string>();
        if (elevate) start.Verb = "runas";
        if (elevate)
        {
            // setupc's second parser splits on whitespace even inside quotes.
            var buffer = new StringBuilder(32768);
            var length = GetShortPathNameW(log, buffer, buffer.Capacity);
            var nativeLog = length > 0 && length < buffer.Capacity ? buffer.ToString() : log;
            if (nativeLog.Any(char.IsWhiteSpace)) throw new ArgumentException("setupc cannot accept a log path containing spaces and no usable short filename exists. Use ElevationHelperPath, which captures stdout inside the elevated helper.");
            nativeArguments.Add("--output"); nativeArguments.Add(nativeLog);
        }
        nativeArguments.AddRange(arguments);
        if (nativeArguments.Count > 8) throw new ArgumentException("The setupc command line exceeds its upstream eight-argument parser.");
        var encoding = RuntimeCompatibility.AnsiEncoding((int)GetACP());
        if (nativeArguments.Sum(a => encoding.GetByteCount(a) + 1) >= 1024) throw new ArgumentException("The setupc command line exceeds its upstream 1024-byte buffer. Use a shorter path.");
        RuntimeCompatibility.SetArguments(start, nativeArguments);
        return start;
    }

    private static string ReadLog(string path)
    {
        // Native setup.dll writes ANSI, not UTF-8.
        return File.ReadAllText(path, RuntimeCompatibility.AnsiEncoding((int)GetACP()));
    }
    [DllImport("kernel32.dll")] private static extern uint GetACP();
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern uint GetShortPathNameW(string path, StringBuilder buffer, int size);
    [DllImport("cfgmgr32.dll")] private static extern uint CMP_WaitNoPendingInstallEvents(uint timeoutMilliseconds);
}
