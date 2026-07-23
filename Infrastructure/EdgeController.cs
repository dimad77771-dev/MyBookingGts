using System.Diagnostics;
using System.Net.Sockets;
using Microsoft.Playwright;
using MyBookingGts.Configuration;

namespace MyBookingGts.Infrastructure;

public sealed class EdgeSession
{
    public required IPlaywright Playwright { get; init; }
    public required IBrowser Browser { get; init; }
    public required IBrowserContext Context { get; init; }
    public required IPage Page { get; set; }
}

public sealed class EdgeController
{
    private readonly AppLogger _logger;
    private readonly HttpClient _httpClient = new()
    {
        Timeout = TimeSpan.FromSeconds(2)
    };

    public EdgeController(AppLogger logger)
    {
        _logger = logger;
    }

    public async Task<EdgeSession> StartOrConnectAsync(
        EdgeConfig config,
        string startupUrl,
        CancellationToken cancellationToken)
    {
        var executablePath = Path.GetFullPath(config.ExecutablePath);
        var profileDirectory = Path.GetFullPath(config.ProfileDirectory);
        var pidFilePath = Path.Combine(profileDirectory, ".mybooking-edge.pid");
        var cdpUrl = $"http://127.0.0.1:{config.RemoteDebuggingPort}";

        if (!File.Exists(executablePath))
        {
            throw new FileNotFoundException("Microsoft Edge executable was not found.", executablePath);
        }

        Directory.CreateDirectory(profileDirectory);

        if (await IsCdpAvailableAsync(cdpUrl, cancellationToken))
        {
            ValidateExistingEdge(pidFilePath, executablePath);
            _logger.Info($"CDP endpoint is already available at {cdpUrl}. Connecting to the existing managed Edge instance.");
        }
        else
        {
            if (await IsTcpPortOpenAsync(config.RemoteDebuggingPort, cancellationToken))
            {
                throw new InvalidOperationException(
                    $"Port {config.RemoteDebuggingPort} is occupied, but it is not a valid Edge CDP endpoint. Nothing was terminated.");
            }

            var process = StartEdge(executablePath, profileDirectory, config.RemoteDebuggingPort, startupUrl);
            File.WriteAllText(pidFilePath, process.Id.ToString());
            _logger.Info($"Started Edge. PID={process.Id}; CDP port={config.RemoteDebuggingPort}; profile={profileDirectory}");

            var deadline = DateTimeOffset.UtcNow.AddSeconds(config.StartupTimeoutSeconds);
            while (DateTimeOffset.UtcNow < deadline)
            {
                cancellationToken.ThrowIfCancellationRequested();

                if (process.HasExited)
                {
                    throw new InvalidOperationException(
                        $"Edge exited before the CDP endpoint became available. Exit code: {process.ExitCode}.");
                }

                if (await IsCdpAvailableAsync(cdpUrl, cancellationToken))
                {
                    break;
                }

                await Task.Delay(500, cancellationToken);
            }

            if (!await IsCdpAvailableAsync(cdpUrl, cancellationToken))
            {
                throw new TimeoutException(
                    $"Edge CDP endpoint did not become available within {config.StartupTimeoutSeconds} seconds.");
            }
        }

        var playwright = await Playwright.CreateAsync();
        var browser = await playwright.Chromium.ConnectOverCDPAsync(cdpUrl);
        var context = browser.Contexts.FirstOrDefault()
                      ?? throw new InvalidOperationException("Connected Edge has no browser context.");

        var page = context.Pages.FirstOrDefault(x =>
                       x.Url.Contains("ewrs.gov.on.ca", StringComparison.OrdinalIgnoreCase))
                   ?? context.Pages.FirstOrDefault()
                   ?? await context.NewPageAsync();

        _logger.Info($"Connected to Edge through CDP. Current page: {page.Url}");

        return new EdgeSession
        {
            Playwright = playwright,
            Browser = browser,
            Context = context,
            Page = page
        };
    }

    private Process StartEdge(
        string executablePath,
        string profileDirectory,
        int port,
        string startupUrl)
    {
        var arguments = string.Join(' ', new[]
        {
            $"--remote-debugging-port={port}",
            "--remote-debugging-address=127.0.0.1",
            $"--user-data-dir=\"{profileDirectory}\"",
            "--no-first-run",
            "--no-default-browser-check",
            "--new-window",
            $"\"{startupUrl}\""
        });

        var startInfo = new ProcessStartInfo
        {
            FileName = executablePath,
            Arguments = arguments,
            UseShellExecute = false,
            CreateNoWindow = false,
            WorkingDirectory = Path.GetDirectoryName(executablePath)!
        };

        return Process.Start(startInfo)
               ?? throw new InvalidOperationException("Failed to start Microsoft Edge.");
    }

    private void ValidateExistingEdge(string pidFilePath, string expectedExecutablePath)
    {
        if (!File.Exists(pidFilePath) ||
            !int.TryParse(File.ReadAllText(pidFilePath).Trim(), out var pid))
        {
            throw new InvalidOperationException(
                "The CDP port belongs to an unknown browser instance: the managed Edge PID file is missing or invalid.");
        }

        try
        {
            using var process = Process.GetProcessById(pid);
            if (process.HasExited)
            {
                throw new InvalidOperationException("The managed Edge PID points to a process that has already exited.");
            }

            var actualExecutablePath = process.MainModule?.FileName;
            if (string.IsNullOrWhiteSpace(actualExecutablePath) ||
                !Path.GetFullPath(actualExecutablePath)
                    .Equals(Path.GetFullPath(expectedExecutablePath), StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException(
                    $"The CDP port is active, but PID {pid} is not the configured Edge executable. Nothing was terminated.");
            }
        }
        catch (ArgumentException)
        {
            throw new InvalidOperationException(
                $"The managed Edge PID {pid} does not exist, but the CDP port is active. Nothing was terminated.");
        }
    }

    private async Task<bool> IsCdpAvailableAsync(string cdpUrl, CancellationToken cancellationToken)
    {
        try
        {
            using var response = await _httpClient.GetAsync($"{cdpUrl}/json/version", cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                return false;
            }

            var body = await response.Content.ReadAsStringAsync(cancellationToken);
            return body.Contains("webSocketDebuggerUrl", StringComparison.OrdinalIgnoreCase);
        }
        catch(Exception ex)
        {
            _logger.Warn("IsCdpAvailableAsync: exception=\n" + ex);
            return false;
        }
    }

    private static async Task<bool> IsTcpPortOpenAsync(int port, CancellationToken cancellationToken)
    {
        try
        {
            using var client = new TcpClient();
            await client.ConnectAsync("127.0.0.1", port, cancellationToken);
            return true;
        }
        catch
        {
            return false;
        }
    }
}
