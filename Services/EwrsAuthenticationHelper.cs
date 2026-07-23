using Microsoft.Playwright;
using MyBookingGts.Configuration;
using MyBookingGts.Infrastructure;

namespace MyBookingGts.Services;

public sealed class EwrsAuthenticationHelper
{
    private readonly EdgeSession _session;
    private readonly AppLogger _logger;
    private readonly SecretsProvider _secretsProvider;

    public EwrsAuthenticationHelper(EdgeSession session, AppLogger logger)
    {
        _session = session;
        _logger = logger;
        _secretsProvider = new SecretsProvider(logger);
    }

    public async Task EnsureAuthenticatedAndReadyAsync(
        AppConfig config,
        CancellationToken cancellationToken)
    {
        var lastStatusLog = DateTimeOffset.MinValue;
        var passwordSubmitted = false;

        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            _session.Page = FindBestPage();
            var page = _session.Page;

            if (await TryDismissWhatsNewAsync(page))
            {
                _logger.Info("Dismissed the EWRS 'What's New' dialog by clicking 'Got it'.");
                await Task.Delay(300, cancellationToken);
                continue;
            }

            if (await IsEwrsWorkspaceAsync(page))
            {
                _logger.Info($"EWRS authenticated workspace detected. Current URL: {page.Url}");
                return;
            }

            if (await IsPasswordPageAsync(page))
            {
                var password = _secretsProvider.TryReadPassword(
                    config.Authentication.SecretsFilePath);

                if (string.IsNullOrEmpty(password))
                {
                    if (DateTimeOffset.Now - lastStatusLog >= TimeSpan.FromMinutes(1))
                    {
                        _logger.Warn(
                            "Microsoft sign-in password page detected, but no password could be read from the secrets file. " +
                            "Enter the password manually or correct the local secrets file.");
                        lastStatusLog = DateTimeOffset.Now;
                    }
                }
                else if (!passwordSubmitted)
                {
                    await SubmitPasswordAsync(page, password, cancellationToken);
                    passwordSubmitted = true;
                    _logger.Info("Password was submitted on the Microsoft sign-in page. Password value was not logged.");
                    await Task.Delay(1000, cancellationToken);
                    continue;
                }
            }

            if (DateTimeOffset.Now - lastStatusLog >= TimeSpan.FromMinutes(1))
            {
                _logger.Warn(
                    $"Waiting for EWRS authentication or MFA completion. Current URL: {page.Url}");
                lastStatusLog = DateTimeOffset.Now;
            }

            await Task.Delay(
                TimeSpan.FromSeconds(config.Booking.AuthenticationCheckSeconds),
                cancellationToken);
        }
    }

    private IPage FindBestPage()
    {
        return _session.Context.Pages.FirstOrDefault(x =>
                   x.Url.Contains("login.microsoftonline.com", StringComparison.OrdinalIgnoreCase))
               ?? _session.Context.Pages.FirstOrDefault(x =>
                   x.Url.Contains("ewrs.gov.on.ca", StringComparison.OrdinalIgnoreCase))
               ?? _session.Context.Pages.LastOrDefault()
               ?? _session.Page;
    }

    private static async Task<bool> IsEwrsWorkspaceAsync(IPage page)
    {
        if (!page.Url.Contains("ewrs.gov.on.ca", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        try
        {
            var bodyText = await page.Locator("body").InnerTextAsync();
            return bodyText.Contains(
                       "Employee Workspace Reservation System",
                       StringComparison.OrdinalIgnoreCase) &&
                   (bodyText.Contains("My bookings", StringComparison.OrdinalIgnoreCase) ||
                    bodyText.Contains("Book a workspace", StringComparison.OrdinalIgnoreCase));
        }
        catch
        {
            return false;
        }
    }

    private static async Task<bool> IsPasswordPageAsync(IPage page)
    {
        if (!page.Url.Contains("login.microsoftonline.com", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var password = page.Locator(
            "input[type='password'], input[name='passwd'], input[placeholder='Password']");

        return await AnyVisibleAsync(password);
    }

    private static async Task SubmitPasswordAsync(
        IPage page,
        string password,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var passwordInputs = page.Locator(
            "input[type='password'], input[name='passwd'], input[placeholder='Password']");
        var passwordInput = await FirstVisibleAsync(passwordInputs)
                            ?? throw new InvalidOperationException(
                                "Microsoft sign-in password field could not be found.");

        await passwordInput.FillAsync(password);

        var signInButtons = page.GetByRole(
            AriaRole.Button,
            new PageGetByRoleOptions { Name = "Sign in", Exact = true });
        var signInButton = await FirstVisibleAsync(signInButtons)
                           ?? await FirstVisibleAsync(page.Locator("input[type='submit']"))
                           ?? throw new InvalidOperationException(
                               "Microsoft sign-in submit control could not be found.");

        await signInButton.ClickAsync();
    }

    private static async Task<bool> TryDismissWhatsNewAsync(IPage page)
    {
        if (!page.Url.Contains("ewrs.gov.on.ca", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var buttons = page.GetByRole(
            AriaRole.Button,
            new PageGetByRoleOptions { Name = "Got it", Exact = true });
        var button = await FirstVisibleAsync(buttons);

        if (button is null)
        {
            return false;
        }

        await button.ClickAsync();
        return true;
    }

    private static async Task<ILocator?> FirstVisibleAsync(ILocator locator)
    {
        var count = await locator.CountAsync();
        for (var index = 0; index < count; index++)
        {
            var item = locator.Nth(index);
            if (await item.IsVisibleAsync())
            {
                return item;
            }
        }

        return null;
    }

    private static async Task<bool> AnyVisibleAsync(ILocator locator)
    {
        return await FirstVisibleAsync(locator) is not null;
    }
}
