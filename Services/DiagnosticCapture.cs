using System.Text;
using Microsoft.Playwright;
using MyBookingGts.Configuration;
using MyBookingGts.Infrastructure;

namespace MyBookingGts.Services;

public sealed class DiagnosticCapture
{
    private readonly AppLogger _logger;

    public DiagnosticCapture(AppLogger logger)
    {
        _logger = logger;
    }

    public async Task<string> CaptureAsync(
        IPage page,
        LoggingConfig config,
        string reason,
        IEnumerable<string>? extraLines = null)
    {
        var root = Path.GetFullPath(config.DiagnosticsDirectory);
        Directory.CreateDirectory(root);

        var safeReason = string.Concat(reason
            .Where(ch => char.IsLetterOrDigit(ch) || ch is '-' or '_'));
        if (string.IsNullOrWhiteSpace(safeReason))
        {
            safeReason = "capture";
        }

        var directory = Path.Combine(
            root,
            $"{DateTimeOffset.Now:yyyy-MM-dd_HH-mm-ss-fff}_{safeReason}");
        Directory.CreateDirectory(directory);

        var screenshotPath = Path.Combine(directory, "screenshot.png");
        var htmlPath = Path.Combine(directory, "page.html");
        var infoPath = Path.Combine(directory, "info.txt");

        try
        {
            await page.ScreenshotAsync(new PageScreenshotOptions
            {
                Path = screenshotPath,
                FullPage = true
            });
        }
        catch (Exception ex)
        {
            _logger.Warn($"Failed to save diagnostic screenshot: {ex.Message}");
        }

        try
        {
            await File.WriteAllTextAsync(htmlPath, await page.ContentAsync(), new UTF8Encoding(false));
        }
        catch (Exception ex)
        {
            _logger.Warn($"Failed to save diagnostic HTML: {ex.Message}");
        }

        var info = new List<string>
        {
            $"Timestamp: {DateTimeOffset.Now:O}",
            $"Reason: {reason}",
            $"URL: {page.Url}",
            $"Title: {await SafeTitleAsync(page)}"
        };

        if (extraLines is not null)
        {
            info.AddRange(extraLines);
        }

        try
        {
            await File.WriteAllLinesAsync(infoPath, info, new UTF8Encoding(false));
        }
        catch (Exception ex)
        {
            _logger.Warn($"Failed to save diagnostic info: {ex.Message}");
        }

        _logger.Info($"Diagnostic material saved to: {directory}");
        return directory;
    }

    private static async Task<string> SafeTitleAsync(IPage page)
    {
        try
        {
            return await page.TitleAsync();
        }
        catch
        {
            return "<unavailable>";
        }
    }
}
