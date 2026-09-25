using System.IO.Pipes;
using System.Security.Cryptography;
using System.Text;

namespace Keel.Desktop.Services;

/// <summary>
/// Single-instance behaviour (PRD 8): the first process holds an exclusive lock file in the data
/// directory and listens on a local pipe (a named pipe on Windows, a Unix domain socket elsewhere).
/// A second launch sends its <c>.keel</c> path (or nothing) and exits; the first brings its window to
/// the front and opens the file.
/// </summary>
public sealed class SingleInstance : IDisposable
{
    private const string ActivateMessage = "activate";
    private const string OpenPrefix = "open\t";

    private readonly FileStream _lock;
    private readonly string _pipeName;
    private readonly CancellationTokenSource _stop = new();
    private Task _listener = Task.CompletedTask;

    private SingleInstance(FileStream lockFile, string pipeName)
    {
        _lock = lockFile;
        _pipeName = pipeName;
    }

    /// <summary>Takes the instance lock for <paramref name="dataRoot"/>; null when another process holds it.</summary>
    public static SingleInstance? TryAcquire(string dataRoot)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(dataRoot);
        Directory.CreateDirectory(dataRoot);
        try
        {
            var stream = new FileStream(Path.Combine(dataRoot, "instance.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
            return new SingleInstance(stream, PipeNameFor(dataRoot));
        }
        catch (IOException)
        {
            return null;
        }
        catch (UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>
    /// Sends the launch request to the running instance; false when it does not answer in time (the
    /// caller then starts normally).
    /// </summary>
    public static bool TrySend(string dataRoot, string? filePath, int timeoutMs = 3000)
    {
        try
        {
            using var client = new NamedPipeClientStream(".", PipeNameFor(dataRoot), PipeDirection.Out);
            client.Connect(timeoutMs);
            var message = string.IsNullOrWhiteSpace(filePath) ? ActivateMessage : OpenPrefix + Path.GetFullPath(filePath);
            var bytes = Encoding.UTF8.GetBytes(message);
            client.Write(bytes, 0, bytes.Length);
            client.Flush();
            return true;
        }
        catch (Exception ex) when (ex is IOException or TimeoutException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    /// <summary>The pipe name for a data directory (one instance per data directory and user).</summary>
    public static string PipeNameFor(string dataRoot)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(Path.GetFullPath(dataRoot) + "|" + Environment.UserName));
        return "keel-" + Convert.ToHexString(hash, 0, 8).ToLowerInvariant();
    }

    /// <summary>
    /// Starts listening; <paramref name="received"/> gets null for "activate" or the file path to open.
    /// It runs on a background thread: marshal to the UI thread.
    /// </summary>
    public void Listen(Action<string?> received)
    {
        ArgumentNullException.ThrowIfNull(received);
        _listener = Task.Run(() => ListenAsync(received, _stop.Token));
    }

    /// <inheritdoc />
    public void Dispose()
    {
        _stop.Cancel();
        try
        {
            _listener.Wait(TimeSpan.FromSeconds(2));
        }
        catch (AggregateException)
        {
        }

        _stop.Dispose();
        _lock.Dispose();
    }

    private async Task ListenAsync(Action<string?> received, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                using var server = new NamedPipeServerStream(_pipeName, PipeDirection.In, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
                await server.WaitForConnectionAsync(ct).ConfigureAwait(false);
                using var reader = new StreamReader(server, Encoding.UTF8);
                var message = await reader.ReadToEndAsync(ct).ConfigureAwait(false);
                if (message.StartsWith(OpenPrefix, StringComparison.Ordinal))
                {
                    received(message[OpenPrefix.Length..]);
                }
                else if (message == ActivateMessage)
                {
                    received(null);
                }
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (IOException)
            {
                // A client that disconnected early; keep listening.
            }
        }
    }
}

/// <summary>Command-line arguments Keel understands: a budget file to open (PRD 8, double-click open).</summary>
public static class LaunchArguments
{
    /// <summary>The first argument that names a <c>.keel</c> file, as a full path; null when there is none.</summary>
    public static string? BudgetFile(IReadOnlyList<string> args)
    {
        ArgumentNullException.ThrowIfNull(args);
        foreach (var arg in args)
        {
            if (string.IsNullOrWhiteSpace(arg) || arg.StartsWith('-'))
            {
                continue;
            }

            var candidate = arg.StartsWith("file://", StringComparison.OrdinalIgnoreCase) && Uri.TryCreate(arg, UriKind.Absolute, out var uri) ? uri.LocalPath : arg;
            if (candidate.EndsWith(Keel.Application.Files.IBudgetFileService.Extension, StringComparison.OrdinalIgnoreCase))
            {
                return Path.GetFullPath(candidate);
            }
        }

        return null;
    }
}
