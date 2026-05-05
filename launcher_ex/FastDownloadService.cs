using System;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;

public sealed class FastDownloadService : IDisposable
{
    private const int ProgressPollMs = 500;

    private bool _disposed;
    private Process _activeProcess;

    public event Action<long, long> DownloadProgressChanged;

    public FastDownloadService(string installRoot)
    {
        if (string.IsNullOrWhiteSpace(installRoot))
            throw new ArgumentNullException(nameof(installRoot));

        Directory.CreateDirectory(Path.GetFullPath(installRoot));
    }

    public async Task DownloadFileAsync(string url, string destinationPath, CancellationToken cancellationToken)
    {
        if (_disposed) throw new ObjectDisposedException(nameof(FastDownloadService));
        if (string.IsNullOrWhiteSpace(url)) throw new ArgumentNullException(nameof(url));
        if (string.IsNullOrWhiteSpace(destinationPath)) throw new ArgumentNullException(nameof(destinationPath));

        destinationPath = Path.GetFullPath(destinationPath);
        var directory = Path.GetDirectoryName(destinationPath);
        if (string.IsNullOrEmpty(directory))
            throw new IOException($"Failed to determine destination directory for '{destinationPath}'.");

        Directory.CreateDirectory(directory);

        var aria2Path = ResolveAria2Path();
        if (string.IsNullOrEmpty(aria2Path))
            throw new FileNotFoundException("Bundled aria2c.exe was not found. Reinstall the launcher and try again.");

        var rpcPort = GetFreeTcpPort();
        var rpcSecret = Guid.NewGuid().ToString("N");
        var gid = Guid.NewGuid().ToString("N").Substring(0, 16);

        var startInfo = new ProcessStartInfo
        {
            FileName = aria2Path,
            Arguments = BuildAria2Arguments(url, directory, Path.GetFileName(destinationPath), rpcPort, rpcSecret, gid),
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardError = true,
            RedirectStandardOutput = true,
            WorkingDirectory = directory
        };

        using var process = new Process { StartInfo = startInfo, EnableRaisingEvents = true };
        using var cancellationRegistration = cancellationToken.Register(() => KillProcess(process));

        Debug.WriteLine($"[FastDownloadService] Starting aria2c for {Path.GetFileName(destinationPath)}");
        if (!process.Start())
            throw new DownloadException("Failed to start aria2c.");

        _activeProcess = process;

        var stdoutTask = DrainOutputAsync(process.StandardOutput, "stdout");
        var stderrTask = DrainOutputAsync(process.StandardError, "stderr");
        var waitTask = Task.Run(() =>
        {
            process.WaitForExit();
            return process.ExitCode;
        }, CancellationToken.None);

        try
        {
            bool completedByRpc = false;
            while (!waitTask.IsCompleted)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var status = await ReportAria2ProgressAsync(rpcPort, rpcSecret, gid, destinationPath).ConfigureAwait(false);
                if (status != null)
                {
                    if (string.Equals(status.Status, "complete", StringComparison.OrdinalIgnoreCase))
                    {
                        completedByRpc = true;
                        KillProcess(process);
                        break;
                    }

                    if (string.Equals(status.Status, "error", StringComparison.OrdinalIgnoreCase) ||
                        string.Equals(status.Status, "removed", StringComparison.OrdinalIgnoreCase))
                    {
                        KillProcess(process);
                        throw new DownloadException($"aria2c failed for {Path.GetFileName(destinationPath)}: {status.ErrorMessage ?? status.Status}");
                    }
                }
                await Task.Delay(ProgressPollMs, cancellationToken).ConfigureAwait(false);
            }

            var exitCode = await waitTask.ConfigureAwait(false);
            await Task.WhenAll(stdoutTask, stderrTask).ConfigureAwait(false);

            await ReportAria2ProgressAsync(rpcPort, rpcSecret, gid, destinationPath).ConfigureAwait(false);

            if (completedByRpc)
                return;

            if (exitCode != 0)
            {
                if (cancellationToken.IsCancellationRequested)
                    throw new OperationCanceledException(cancellationToken);

                throw new DownloadException($"aria2c failed for {Path.GetFileName(destinationPath)} with exit code {exitCode}.");
            }
        }
        finally
        {
            if (ReferenceEquals(_activeProcess, process))
                _activeProcess = null;
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        var process = _activeProcess;
        if (process != null)
            KillProcess(process);
    }

    private static string BuildAria2Arguments(string url, string directory, string fileName, int rpcPort, string rpcSecret, string gid)
    {
        return string.Join(" ", new[]
        {
            "--gid=" + gid,
            "--continue=true",
            "--allow-overwrite=true",
            "--auto-file-renaming=false",
            "--file-allocation=none",
            "--max-connection-per-server=8",
            "--split=8",
            "--min-split-size=4M",
            "--retry-wait=2",
            "--max-tries=8",
            "--timeout=30",
            "--connect-timeout=15",
            "--summary-interval=1",
            "--download-result=hide",
            "--console-log-level=warn",
            "--enable-rpc=true",
            "--rpc-listen-all=false",
            "--rpc-listen-port=" + rpcPort,
            "--rpc-secret=" + rpcSecret,
            "--check-certificate=true",
            "--dir=" + Quote(directory),
            "--out=" + Quote(fileName),
            Quote(url)
        });
    }

    private static string ResolveAria2Path()
    {
        var baseDirectory = AppDomain.CurrentDomain.BaseDirectory;
        var candidates = new[]
        {
            Path.Combine(baseDirectory, "tools", "aria2", "aria2c.exe"),
            Path.Combine(baseDirectory, "aria2c.exe"),
            Path.Combine(Environment.CurrentDirectory, "launcher_ex", "tools", "aria2", "aria2c.exe"),
            Path.Combine(Environment.CurrentDirectory, "tools", "aria2", "aria2c.exe")
        };

        foreach (var candidate in candidates)
        {
            if (File.Exists(candidate))
                return candidate;
        }

        return null;
    }

    private async Task<Aria2Status> ReportAria2ProgressAsync(int rpcPort, string rpcSecret, string gid, string destinationPath)
    {
        try
        {
            var requestJson =
                "{\"jsonrpc\":\"2.0\",\"id\":\"r1delta\",\"method\":\"aria2.tellStatus\",\"params\":[\"token:" +
                rpcSecret +
                "\",\"" +
                gid +
                "\",[\"completedLength\",\"totalLength\",\"status\",\"errorMessage\"]]}";

            var request = (HttpWebRequest)WebRequest.Create($"http://127.0.0.1:{rpcPort}/jsonrpc");
            request.Method = "POST";
            request.ContentType = "application/json";
            request.Timeout = 1000;
            request.ReadWriteTimeout = 1000;

            using (var requestStream = await request.GetRequestStreamAsync().ConfigureAwait(false))
            using (var writer = new StreamWriter(requestStream))
            {
                await writer.WriteAsync(requestJson).ConfigureAwait(false);
            }

            using (var response = (HttpWebResponse)await request.GetResponseAsync().ConfigureAwait(false))
            using (var responseStream = response.GetResponseStream())
            using (var reader = new StreamReader(responseStream))
            {
                var responseJson = await reader.ReadToEndAsync().ConfigureAwait(false);
                var result = JObject.Parse(responseJson)["result"];
                if (result != null)
                {
                    var completed = ParseAria2Length((string)result["completedLength"]);
                    var total = ParseAria2Length((string)result["totalLength"]);
                    DownloadProgressChanged?.Invoke(completed, Math.Max(total, completed));
                    return new Aria2Status
                    {
                        CompletedLength = completed,
                        TotalLength = total,
                        Status = (string)result["status"],
                        ErrorMessage = (string)result["errorMessage"]
                    };
                }
            }
        }
        catch (WebException)
        {
            // aria2 may not have opened the RPC listener yet, or may have just exited.
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[FastDownloadService] Failed to read aria2 progress: {ex.Message}");
        }

        ReportFileProgressFallback(destinationPath);
        return null;
    }

    private void ReportFileProgressFallback(string destinationPath)
    {
        long bytes = 0;
        try
        {
            if (File.Exists(destinationPath))
                bytes = new FileInfo(destinationPath).Length;
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }

        DownloadProgressChanged?.Invoke(bytes, Math.Max(bytes, 1));
    }

    private static long ParseAria2Length(string value)
    {
        if (long.TryParse(value, out var result) && result > 0)
            return result;
        return 0;
    }

    private static int GetFreeTcpPort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        try
        {
            return ((IPEndPoint)listener.LocalEndpoint).Port;
        }
        finally
        {
            listener.Stop();
        }
    }

    private sealed class Aria2Status
    {
        public long CompletedLength { get; set; }
        public long TotalLength { get; set; }
        public string Status { get; set; }
        public string ErrorMessage { get; set; }
    }

    private static async Task DrainOutputAsync(StreamReader reader, string streamName)
    {
        string line;
        while ((line = await reader.ReadLineAsync().ConfigureAwait(false)) != null)
        {
            if (line.Length > 0)
                Debug.WriteLine($"[aria2c:{streamName}] {line}");
        }
    }

    private static void KillProcess(Process process)
    {
        try
        {
            if (process != null && !process.HasExited)
                process.Kill();
        }
        catch (InvalidOperationException)
        {
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[FastDownloadService] Failed to kill aria2c: {ex.Message}");
        }
    }

    private static string Quote(string value)
    {
        if (value == null) return "\"\"";
        return "\"" + value.Replace("\"", "\\\"") + "\"";
    }
}

public class DownloadException : Exception
{
    public DownloadException(string message) : base(message) { }
    public DownloadException(string message, Exception innerException) : base(message, innerException) { }
}
