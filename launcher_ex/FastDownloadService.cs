using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
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

    public event Action<string, long, long> DownloadProgressChanged;

    public FastDownloadService(string installRoot)
    {
        if (string.IsNullOrWhiteSpace(installRoot))
            throw new ArgumentNullException(nameof(installRoot));

        Directory.CreateDirectory(Path.GetFullPath(installRoot));
    }

    public async Task DownloadFilesAsync(IEnumerable<DownloadRequest> files, CancellationToken cancellationToken)
    {
        if (_disposed) throw new ObjectDisposedException(nameof(FastDownloadService));
        if (files == null) throw new ArgumentNullException(nameof(files));

        var requests = files.ToList();
        if (requests.Count == 0)
            return;

        var aria2Path = ResolveAria2Path();
        if (string.IsNullOrEmpty(aria2Path))
            throw new FileNotFoundException("Bundled aria2c.exe was not found. Reinstall the launcher and try again.");

        var rpcPort = GetFreeTcpPort();
        var rpcSecret = Guid.NewGuid().ToString("N");
        var gidMap = new Dictionary<string, DownloadRequest>(StringComparer.OrdinalIgnoreCase);

        var startInfo = new ProcessStartInfo
        {
            FileName = aria2Path,
            Arguments = BuildAria2Arguments(rpcPort, rpcSecret),
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardError = true,
            RedirectStandardOutput = true
        };

        using var process = new Process { StartInfo = startInfo, EnableRaisingEvents = true };
        using var cancellationRegistration = cancellationToken.Register(() => KillProcess(process));

        Debug.WriteLine($"[FastDownloadService] Starting one aria2c process for {requests.Count} files.");
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
            await WaitForRpcAsync(rpcPort, rpcSecret, waitTask, cancellationToken).ConfigureAwait(false);

            foreach (var request in requests)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var gid = Guid.NewGuid().ToString("N").Substring(0, 16);
                request.Gid = gid;
                gidMap[gid] = request;
                await AddDownloadAsync(rpcPort, rpcSecret, request).ConfigureAwait(false);
            }

            var remaining = new HashSet<string>(gidMap.Keys, StringComparer.OrdinalIgnoreCase);
            while (remaining.Count > 0)
            {
                cancellationToken.ThrowIfCancellationRequested();

                foreach (var gid in remaining.ToList())
                {
                    var request = gidMap[gid];
                    var status = await TellStatusAsync(rpcPort, rpcSecret, gid).ConfigureAwait(false);
                    if (status == null)
                        continue;

                    DownloadProgressChanged?.Invoke(request.DestinationPath, status.CompletedLength, Math.Max(status.TotalLength, status.CompletedLength));

                    if (string.Equals(status.Status, "complete", StringComparison.OrdinalIgnoreCase))
                    {
                        remaining.Remove(gid);
                    }
                    else if (string.Equals(status.Status, "error", StringComparison.OrdinalIgnoreCase) ||
                             string.Equals(status.Status, "removed", StringComparison.OrdinalIgnoreCase))
                    {
                        throw new DownloadException($"aria2c failed for {Path.GetFileName(request.DestinationPath)}: {status.ErrorMessage ?? status.Status}");
                    }
                }

                if (remaining.Count > 0)
                    await Task.Delay(ProgressPollMs, cancellationToken).ConfigureAwait(false);
            }

            await ShutdownAria2Async(rpcPort, rpcSecret).ConfigureAwait(false);
            var exitCode = await waitTask.ConfigureAwait(false);
            await Task.WhenAll(stdoutTask, stderrTask).ConfigureAwait(false);

            if (exitCode != 0)
                throw new DownloadException($"aria2c exited with code {exitCode}.");
        }
        finally
        {
            if (!process.HasExited)
                KillProcess(process);

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

    private static string BuildAria2Arguments(int rpcPort, string rpcSecret)
    {
        return string.Join(" ", new[]
        {
            "--continue=true",
            "--allow-overwrite=true",
            "--auto-file-renaming=false",
            "--file-allocation=none",
            "--max-concurrent-downloads=4",
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
            "--check-certificate=true"
        });
    }

    private async Task WaitForRpcAsync(int rpcPort, string rpcSecret, Task<int> waitTask, CancellationToken cancellationToken)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(10);
        while (DateTime.UtcNow < deadline)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (waitTask.IsCompleted)
                throw new DownloadException("aria2c exited before the RPC server started.");

            try
            {
                await CallRpcAsync(rpcPort, rpcSecret, "aria2.getVersion", new JArray()).ConfigureAwait(false);
                return;
            }
            catch (WebException)
            {
                await Task.Delay(100, cancellationToken).ConfigureAwait(false);
            }
        }

        throw new TimeoutException("Timed out waiting for aria2c RPC server to start.");
    }

    private async Task AddDownloadAsync(int rpcPort, string rpcSecret, DownloadRequest request)
    {
        var destinationPath = Path.GetFullPath(request.DestinationPath);
        var directory = Path.GetDirectoryName(destinationPath);
        if (string.IsNullOrEmpty(directory))
            throw new IOException($"Failed to determine destination directory for '{destinationPath}'.");

        Directory.CreateDirectory(directory);

        var options = new JObject
        {
            ["gid"] = request.Gid,
            ["dir"] = directory,
            ["out"] = Path.GetFileName(destinationPath)
        };

        var parameters = new JArray
        {
            new JArray(request.Url),
            options
        };

        await CallRpcAsync(rpcPort, rpcSecret, "aria2.addUri", parameters).ConfigureAwait(false);
    }

    private async Task<Aria2Status> TellStatusAsync(int rpcPort, string rpcSecret, string gid)
    {
        var parameters = new JArray
        {
            gid,
            new JArray("completedLength", "totalLength", "status", "errorMessage")
        };

        var result = await CallRpcAsync(rpcPort, rpcSecret, "aria2.tellStatus", parameters).ConfigureAwait(false);
        if (result == null)
            return null;

        return new Aria2Status
        {
            CompletedLength = ParseAria2Length((string)result["completedLength"]),
            TotalLength = ParseAria2Length((string)result["totalLength"]),
            Status = (string)result["status"],
            ErrorMessage = (string)result["errorMessage"]
        };
    }

    private async Task ShutdownAria2Async(int rpcPort, string rpcSecret)
    {
        try
        {
            await CallRpcAsync(rpcPort, rpcSecret, "aria2.shutdown", new JArray()).ConfigureAwait(false);
        }
        catch (WebException)
        {
        }
    }

    private async Task<JToken> CallRpcAsync(int rpcPort, string rpcSecret, string method, JArray parameters)
    {
        var allParameters = new JArray { "token:" + rpcSecret };
        foreach (var parameter in parameters)
            allParameters.Add(parameter);

        var requestJson = new JObject
        {
            ["jsonrpc"] = "2.0",
            ["id"] = "r1delta",
            ["method"] = method,
            ["params"] = allParameters
        }.ToString(Newtonsoft.Json.Formatting.None);

        var request = (HttpWebRequest)WebRequest.Create($"http://127.0.0.1:{rpcPort}/jsonrpc");
        request.Method = "POST";
        request.ContentType = "application/json";
        request.Timeout = 2000;
        request.ReadWriteTimeout = 2000;

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
            var responseObject = JObject.Parse(responseJson);
            var error = responseObject["error"];
            if (error != null)
                throw new DownloadException((string)error["message"] ?? $"aria2 RPC call failed: {method}");

            return responseObject["result"];
        }
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

    public sealed class DownloadRequest
    {
        public string Url { get; set; }
        public string DestinationPath { get; set; }
        public string Gid { get; set; }
    }

    private sealed class Aria2Status
    {
        public long CompletedLength { get; set; }
        public long TotalLength { get; set; }
        public string Status { get; set; }
        public string ErrorMessage { get; set; }
    }
}

public class DownloadException : Exception
{
    public DownloadException(string message) : base(message) { }
    public DownloadException(string message, Exception innerException) : base(message, innerException) { }
}
