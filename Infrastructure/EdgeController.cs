using System.Diagnostics;
using System.Net.Sockets;
using Microsoft.Playwright;
using MyBookingGts.Configuration;

namespace MyBookingGts.Infrastructure;

public sealed class EdgeSession
{
    public required IPlaywright Playwright { get; init; }
    public IBrowser? Browser { get; init; }
    public required IBrowserContext Context { get; init; }
    public required IPage Page { get; set; }
    public required string BrowserName { get; init; }
}

public sealed class EdgeController
{
    private readonly AppLogger _logger;

    // Corporate proxy settings can interfere even with requests to 127.0.0.1.
    // CDP is strictly local, therefore proxy usage is disabled explicitly.
    private readonly HttpClient _httpClient = new(
        new SocketsHttpHandler
        {
            UseProxy = false,
            ConnectTimeout = TimeSpan.FromSeconds(3)
        })
    {
        Timeout = TimeSpan.FromSeconds(5)
    };

    public EdgeController(AppLogger logger)
    {
        _logger = logger;
    }

    public Task<EdgeSession> StartOrConnectAsync(
        EdgeConfig config,
        string startupUrl,
        CancellationToken cancellationToken)
    {
        return config.UseFirefox
            ? StartFirefoxAsync(config, startupUrl, cancellationToken)
            : StartOrConnectChromiumAsync(config, startupUrl, cancellationToken);
    }

    private async Task<EdgeSession> StartFirefoxAsync(
        EdgeConfig config,
        string startupUrl,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var profileDirectory = Path.GetFullPath(config.ProfileDirectory);
        Directory.CreateDirectory(profileDirectory);

        var configuredExecutable = string.IsNullOrWhiteSpace(config.FirefoxExecutablePath)
            ? null
            : Path.GetFullPath(config.FirefoxExecutablePath);

        if (configuredExecutable is not null && !File.Exists(configuredExecutable))
        {
            throw new FileNotFoundException("Configured Firefox executable was not found.", configuredExecutable);
        }

        _logger.Info(
            configuredExecutable is null
                ? $"Starting Playwright-managed Firefox. profile={profileDirectory}"
                : $"Starting configured Firefox executable through Playwright. executable={configuredExecutable}; profile={profileDirectory}");

        if (configuredExecutable is not null)
        {
            _logger.Warn(
                "A regular installed Firefox is normally incompatible with Playwright because Playwright requires a patched Firefox build. " +
                "Leave Edge.FirefoxExecutablePath empty to use the supported Playwright-managed Firefox.");
        }

        var playwright = await Playwright.CreateAsync();

        try
        {
            var launchOptions = new BrowserTypeLaunchPersistentContextOptions
            {
                Headless = false,
                Timeout = config.StartupTimeoutSeconds * 1000
            };

            if (configuredExecutable is not null)
            {
                launchOptions.ExecutablePath = configuredExecutable;
            }

            _logger.Info("Calling Firefox.LaunchPersistentContextAsync...");

            var context = await playwright.Firefox.LaunchPersistentContextAsync(
                profileDirectory,
                launchOptions);

            _logger.Info($"Firefox persistent context started. Existing pages: {context.Pages.Count}.");

            var page = context.Pages.FirstOrDefault(x =>
                           x.Url.Contains("ewrs.gov.on.ca", StringComparison.OrdinalIgnoreCase))
                       ?? context.Pages.FirstOrDefault();

            if (page is null)
            {
                _logger.Info("Firefox context has no page. Creating a new page.");
                page = await context.NewPageAsync();
            }

            _logger.Info($"Firefox page selected. Current URL: {page.Url}");

            if (string.IsNullOrWhiteSpace(page.Url) ||
                page.Url.Equals("about:blank", StringComparison.OrdinalIgnoreCase) ||
                page.Url.StartsWith("about:", StringComparison.OrdinalIgnoreCase))
            {
                _logger.Info($"Opening startup URL in Firefox: {startupUrl}");

                await page.GotoAsync(
                    startupUrl,
                    new PageGotoOptions
                    {
                        WaitUntil = WaitUntilState.DOMContentLoaded,
                        Timeout = config.StartupTimeoutSeconds * 1000
                    });
            }

            _logger.Info($"Firefox started and connected through Playwright. Current page: {page.Url}");

            return new EdgeSession
            {
                Playwright = playwright,
                Browser = context.Browser,
                Context = context,
                Page = page,
                BrowserName = "Firefox"
            };
        }
        catch (PlaywrightException ex)
        {
            playwright.Dispose();

            var executableDescription = configuredExecutable is null
                ? "Playwright-managed Firefox"
                : configuredExecutable;

            throw new InvalidOperationException(
                $"Firefox could not be started or controlled by Playwright. Executable: {executableDescription}. " +
                "If Playwright-managed Firefox is not installed, run: pwsh bin/Debug/net8.0/playwright.ps1 install firefox",
                ex);
        }
        catch
        {
            playwright.Dispose();
            throw;
        }
    }

    private async Task<EdgeSession> StartOrConnectChromiumAsync(
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
            throw new FileNotFoundException("Chromium browser executable was not found.", executablePath);
        }

        Directory.CreateDirectory(profileDirectory);

        if (await IsCdpAvailableAsync(cdpUrl, cancellationToken))
        {
            ValidateExistingBrowser(pidFilePath, executablePath);
            _logger.Info($"CDP endpoint is already available at {cdpUrl}. Connecting to the existing managed Chromium instance.");
        }
        else
        {
            if (await IsTcpPortOpenAsync(config.RemoteDebuggingPort, cancellationToken))
            {
                throw new InvalidOperationException(
                    $"Port {config.RemoteDebuggingPort} is occupied, but it is not a valid Chromium CDP endpoint. Nothing was terminated.");
            }

            var process = StartChromiumBrowser(
                executablePath,
                profileDirectory,
                config.RemoteDebuggingPort,
                startupUrl);

            File.WriteAllText(pidFilePath, process.Id.ToString());
            _logger.Info(
                $"Started Chromium browser. PID={process.Id}; CDP port={config.RemoteDebuggingPort}; profile={profileDirectory}");

            var deadline = DateTimeOffset.UtcNow.AddSeconds(config.StartupTimeoutSeconds);
            while (DateTimeOffset.UtcNow < deadline)
            {
                cancellationToken.ThrowIfCancellationRequested();

                if (process.HasExited)
                {
                    throw new InvalidOperationException(
                        $"Chromium browser exited before the CDP endpoint became available. Exit code: {process.ExitCode}.");
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
                    $"Chromium CDP endpoint did not become available within {config.StartupTimeoutSeconds} seconds.");
            }
        }

        var playwright = await Playwright.CreateAsync();
        var browser = await playwright.Chromium.ConnectOverCDPAsync(cdpUrl);
        var context = browser.Contexts.FirstOrDefault()
                      ?? throw new InvalidOperationException("Connected Chromium browser has no browser context.");

        var page = context.Pages.FirstOrDefault(x =>
                       x.Url.Contains("ewrs.gov.on.ca", StringComparison.OrdinalIgnoreCase))
                   ?? context.Pages.FirstOrDefault()
                   ?? await context.NewPageAsync();

        _logger.Info($"Connected to Chromium browser through CDP. Current page: {page.Url}");

        return new EdgeSession
        {
            Playwright = playwright,
            Browser = browser,
            Context = context,
            Page = page,
            BrowserName = "Chromium"
        };
    }

    private Process StartChromiumBrowser(
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
               ?? throw new InvalidOperationException("Failed to start Chromium browser.");
    }

    private void ValidateExistingBrowser(string pidFilePath, string expectedExecutablePath)
    {
        if (!File.Exists(pidFilePath) ||
            !int.TryParse(File.ReadAllText(pidFilePath).Trim(), out var pid))
        {
            throw new InvalidOperationException(
                "The CDP port belongs to an unknown browser instance: the managed browser PID file is missing or invalid.");
        }

        try
        {
            using var process = Process.GetProcessById(pid);
            if (process.HasExited)
            {
                throw new InvalidOperationException("The managed browser PID points to a process that has already exited.");
            }

            var actualExecutablePath = process.MainModule?.FileName;
            if (string.IsNullOrWhiteSpace(actualExecutablePath) ||
                !Path.GetFullPath(actualExecutablePath)
                    .Equals(Path.GetFullPath(expectedExecutablePath), StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException(
                    $"The CDP port is active, but PID {pid} is not the configured browser executable. Nothing was terminated.");
            }
        }
        catch (ArgumentException)
        {
            throw new InvalidOperationException(
                $"The managed browser PID {pid} does not exist, but the CDP port is active. Nothing was terminated.");
        }
    }

    private async Task<bool> IsCdpAvailableAsync(
        string cdpUrl,
        CancellationToken cancellationToken)
    {
        var endpoint = $"{cdpUrl}/json/version";

        try
        {
            using var response = await _httpClient.GetAsync(endpoint, cancellationToken);

            if (!response.IsSuccessStatusCode)
            {
                _logger.Warn(
                    $"CDP endpoint returned HTTP {(int)response.StatusCode} {response.StatusCode}: {endpoint}");
                return false;
            }

            var body = await response.Content.ReadAsStringAsync(cancellationToken);
            var isAvailable = body.Contains(
                "webSocketDebuggerUrl",
                StringComparison.OrdinalIgnoreCase);

            if (!isAvailable)
            {
                _logger.Warn("CDP response does not contain webSocketDebuggerUrl.");
            }

            return isAvailable;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (TaskCanceledException ex)
        {
            _logger.Warn($"CDP request timed out: {endpoint}. {ex.Message}");
            return false;
        }
        catch (HttpRequestException ex)
        {
            _logger.Warn($"CDP request failed: {endpoint}. {ex.Message}");
            return false;
        }
        catch (Exception ex)
        {
            _logger.Warn("IsCdpAvailableAsync: unexpected exception:\n" + ex);
            return false;
        }
    }

    private static async Task<bool> IsTcpPortOpenAsync(
        int port,
        CancellationToken cancellationToken)
    {
        try
        {
            using var client = new TcpClient();
            await client.ConnectAsync("127.0.0.1", port, cancellationToken);
            return true;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            return false;
        }
    }
}
