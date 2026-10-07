using System.Diagnostics;
using System.IO.Pipes;
using System.Security.AccessControl;
using System.Text;

namespace Com0ComSharp;

internal static class RuntimeCompatibility
{
    internal static string CreateTemporaryDirectory(string prefix) =>
        Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), prefix + Guid.NewGuid().ToString("N"))).FullName;

    internal static string ToHexString(byte[] bytes) => BitConverter.ToString(bytes).Replace("-", "");

    internal static Encoding AnsiEncoding(int codePage)
    {
#if !NETFRAMEWORK
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
#endif
        return Encoding.GetEncoding(codePage);
    }

    internal static void SetArguments(ProcessStartInfo start, IEnumerable<string> arguments)
    {
#if NETFRAMEWORK
        start.Arguments = string.Join(" ", arguments.Select(QuoteArgument));
#else
        foreach (var argument in arguments) start.ArgumentList.Add(argument);
#endif
    }

    // CommandLineToArgvW/CRT quoting: backslashes before quotes and the closing
    // quote need doubling. ShellExecute receives this string without cmd.exe.
    internal static string QuoteArgument(string argument)
    {
        if (argument.Length != 0 && !argument.Any(c => char.IsWhiteSpace(c) || c == '"')) return argument;
        var result = new StringBuilder("\"");
        var backslashes = 0;
        foreach (var character in argument)
        {
            if (character == '\\') { backslashes++; continue; }
            if (character == '"') result.Append('\\', backslashes * 2 + 1);
            else result.Append('\\', backslashes);
            result.Append(character);
            backslashes = 0;
        }
        return result.Append('\\', backslashes * 2).Append('"').ToString();
    }

    internal static NamedPipeServerStream CreateResultPipe(string name, PipeSecurity security)
    {
#if NETFRAMEWORK
        return new NamedPipeServerStream(name, PipeDirection.InOut, 1, PipeTransmissionMode.Byte,
            PipeOptions.Asynchronous, 4096, 4096, security);
#else
        return NamedPipeServerStreamAcl.Create(name, PipeDirection.InOut, 1, PipeTransmissionMode.Byte,
            PipeOptions.Asynchronous, 4096, 4096, security);
#endif
    }

    internal static void KillProcess(Process process)
    {
#if NETFRAMEWORK
        process.Kill(); // Framework has no process-tree Kill overload.
#else
        process.Kill(true);
#endif
    }

    internal static async Task WaitForExitAsync(Process process, CancellationToken cancellationToken = default)
    {
#if NETFRAMEWORK
        cancellationToken.ThrowIfCancellationRequested();
        var completion = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        EventHandler exited = (_, _) => completion.TrySetResult(true);
        process.Exited += exited;
        try
        {
            process.EnableRaisingEvents = true;
            if (process.HasExited) completion.TrySetResult(true);
            await WaitAsync(completion.Task, cancellationToken).ConfigureAwait(false);
        }
        finally { process.Exited -= exited; }
#else
        await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
#endif
    }

    internal static async Task<T> WaitAsync<T>(Task<T> task, CancellationToken cancellationToken)
    {
        if (!cancellationToken.CanBeCanceled) return await task.ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        // Framework StreamReader often completes from its own buffer. Avoid a
        // cancellation registration and Task.WhenAny for every buffered read.
        if (task.IsCompleted) return await task.ConfigureAwait(false);
        var cancelled = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        using (cancellationToken.Register(() => cancelled.TrySetResult(true)))
        {
            if (await Task.WhenAny(task, cancelled.Task).ConfigureAwait(false) != task)
            {
                // Cancellation stops waiting; the underlying native/Framework IO
                // may finish later. Observe any eventual failure without blocking.
                _ = task.ContinueWith(t => { _ = t.Exception; }, CancellationToken.None,
                    TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
                throw new OperationCanceledException(cancellationToken);
            }
            return await task.ConfigureAwait(false);
        }
    }

    internal static Task<int> ReadTextAsync(StreamReader reader, char[] buffer, CancellationToken cancellationToken)
    {
#if NETFRAMEWORK
        return WaitAsync(reader.ReadAsync(buffer, 0, buffer.Length), cancellationToken);
#else
        return reader.ReadAsync(buffer.AsMemory(), cancellationToken).AsTask();
#endif
    }

    internal static async Task<string?> ReadBoundedLineAsync(StreamReader reader, int limit, CancellationToken cancellationToken)
    {
        var line = new StringBuilder();
        var one = new char[1];
        while (await ReadTextAsync(reader, one, cancellationToken).ConfigureAwait(false) != 0)
        {
            if (one[0] == '\n') return line.ToString().TrimEnd('\r');
            if (line.Length >= limit) throw new InvalidDataException("The management message exceeds the size limit.");
            line.Append(one[0]);
        }
        return line.Length == 0 ? null : line.ToString();
    }

#if NETFRAMEWORK
    internal static bool Contains(this string text, string value, StringComparison comparison) =>
        text.IndexOf(value, comparison) >= 0;
#endif
}
