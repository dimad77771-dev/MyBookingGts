using MyBookingGts.Configuration;
using MyBookingGts.Domain;
using MyBookingGts.Infrastructure;
using MyBookingGts.Services;
using Microsoft.Playwright;

namespace MyBookingGts;

internal static class Program
{
    private const string MutexName = "MyBookingGts.SingleInstance";

    public static async Task<int> Main()
    {
        var configPath = Path.Combine(AppContext.BaseDirectory, "appsettings.json");
        AppLogger? logger = null;
        EdgeSession? edgeSession = null;

        using var cancellation = new CancellationTokenSource();
        Console.CancelKeyPress += (_, args) =>
        {
            args.Cancel = true;
            cancellation.Cancel();
        };

        try
        {
            using var instance = SingleInstanceGuard.Acquire(MutexName);

            var startupConfig = AppConfig.Load(configPath);
            logger = new AppLogger(startupConfig.Logging);
            logger.Info("My Booking GTS started.");
            logger.Info($"Configuration: {configPath}");
            logger.Info("Version 1 safety mode: the robot stops before clicking any booking or confirmation control.");

            var edgeController = new EdgeController(logger);
            edgeSession = await edgeController.StartOrConnectAsync(
                startupConfig.Edge,
                startupConfig.Ewrs.HomeUrl,
                cancellation.Token);

            await InstallDateOverlayAutoCloseAsync(edgeSession.Page, logger);

            var diagnostics = new DiagnosticCapture(logger);
            var robot = new EwrsRobot(edgeSession, logger, diagnostics);
            var authentication = new EwrsAuthenticationHelper(edgeSession, logger);

            await authentication.EnsureAuthenticatedAndReadyAsync(
                startupConfig,
                cancellation.Token);

            var cycleNumber = 0;

            while (!cancellation.IsCancellationRequested)
            {
                AppConfig config;
                try
                {
                    config = AppConfig.Load(configPath);
                }
                catch (Exception ex)
                {
                    logger.Error(ex, "Configuration reload failed. Retrying in 60 seconds.");
                    await Task.Delay(TimeSpan.FromSeconds(60), cancellation.Token);
                    continue;
                }

                cycleNumber++;
                var completed = false;

                for (var attempt = 1;
                     attempt <= config.Booking.TechnicalRetryCount && !completed;
                     attempt++)
                {
                    try
                    {
                        var outcome = await robot.RunCycleAsync(
                            config,
                            cycleNumber,
                            cancellation.Token);

                        completed = true;

                        if (outcome == CycleOutcome.PreferredDeskFound)
                        {
                            logger.Warn("Robot is now waiting indefinitely for manual analysis. Press Ctrl+C to exit. Browser remains open.");
                            await Task.Delay(Timeout.InfiniteTimeSpan, cancellation.Token);
                            return 0;
                        }
                    }
                    catch (AuthenticationRequiredException ex)
                    {
                        logger.Warn(ex.Message);
                        await authentication.EnsureAuthenticatedAndReadyAsync(
                            config,
                            cancellation.Token);
                        attempt = 0;
                    }
                    catch (SafetyMismatchException ex)
                    {
                        logger.Error(ex.Message);
                        try
                        {
                            await diagnostics.CaptureAsync(
                                edgeSession.Page,
                                config.Logging,
                                "safety-mismatch",
                                new[] { ex.ToString() });
                        }
                        catch (Exception captureException)
                        {
                            logger.Warn($"Diagnostic capture also failed: {captureException.Message}");
                        }

                        completed = true;
                    }
                    catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
                    {
                        throw;
                    }
                    catch (Exception ex)
                    {
                        logger.Error(ex, $"Technical failure in cycle {cycleNumber}, attempt {attempt}/{config.Booking.TechnicalRetryCount}.");

                        try
                        {
                            await diagnostics.CaptureAsync(
                                edgeSession.Page,
                                config.Logging,
                                "technical-error",
                                new[] { ex.ToString() });
                        }
                        catch (Exception captureException)
                        {
                            logger.Warn($"Diagnostic capture also failed: {captureException.Message}");
                        }

                        if (attempt < config.Booking.TechnicalRetryCount)
                        {
                            logger.Info($"Technical retry in {config.Booking.TechnicalRetrySeconds} seconds.");
                            await Task.Delay(
                                TimeSpan.FromSeconds(config.Booking.TechnicalRetrySeconds),
                                cancellation.Token);
                        }
                    }
                }

                var delaySeconds = Random.Shared.Next(
                    config.Booking.RetryDelayMinMinutes * 60,
                    config.Booking.RetryDelayMaxMinutes * 60 + 1);
                var nextCycle = DateTimeOffset.Now.AddSeconds(delaySeconds);

                logger.Info(
                    $"Next cycle at {nextCycle:yyyy-MM-dd HH:mm:ss zzz} " +
                    $"(pause {TimeSpan.FromSeconds(delaySeconds):hh\\:mm\\:ss}).");

                await Task.Delay(TimeSpan.FromSeconds(delaySeconds), cancellation.Token);
            }

            return 0;
        }
        catch (OperationCanceledException)
        {
            logger?.Info("My Booking GTS stopped by user. Browser was not closed intentionally.");
            return 0;
        }
        catch (Exception ex)
        {
            if (logger is not null)
            {
                logger.Error(ex, "Fatal error. My Booking GTS is stopping. Browser was not intentionally closed.");
            }
            else
            {
                Console.Error.WriteLine(ex);
            }

            return 1;
        }
        finally
        {
            // Do not call Browser.CloseAsync(): the browser must remain visible for manual MFA and analysis.
            edgeSession?.Playwright.Dispose();
        }
    }

    private static async Task InstallDateOverlayAutoCloseAsync(IPage page, AppLogger logger)
    {
        const string script = """
            (() => {
                if (window.__myBookingDateOverlayCloserInstalled) {
                    return;
                }

                window.__myBookingDateOverlayCloserInstalled = true;

                document.addEventListener('click', event => {
                    const target = event.target instanceof Element ? event.target : null;
                    if (!target) {
                        return;
                    }

                    const dateCell = target.closest(
                        '.mat-calendar-body-cell, .mat-mdc-calendar-body-cell, [role="gridcell"]');

                    if (!dateCell) {
                        return;
                    }

                    const disabled =
                        dateCell.getAttribute('aria-disabled') === 'true' ||
                        dateCell.hasAttribute('disabled') ||
                        dateCell.classList.contains('mat-calendar-body-disabled') ||
                        dateCell.classList.contains('mat-mdc-calendar-body-disabled');

                    if (disabled) {
                        return;
                    }

                    setTimeout(() => {
                        const backdrop = document.querySelector(
                            '.cdk-overlay-backdrop.cdk-overlay-backdrop-showing');

                        if (backdrop instanceof HTMLElement) {
                            backdrop.click();
                        }
                    }, 250);
                }, true);
            })();
            """;

        await page.AddInitScriptAsync(script);

        try
        {
            await page.EvaluateAsync(script);
        }
        catch (PlaywrightException ex)
        {
            logger.Warn($"Could not install the date overlay closer on the current document: {ex.Message}");
        }

        logger.Info("Installed automatic date overlay closer for EWRS calendar selections.");
    }
}
