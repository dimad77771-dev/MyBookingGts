using System.Globalization;
using System.Text.RegularExpressions;
using Microsoft.Playwright;
using MyBookingGts.Configuration;
using MyBookingGts.Infrastructure;

namespace MyBookingGts.Services;

public sealed class PreferredDeskBooker
{
    private readonly EdgeSession _session;
    private readonly AppLogger _logger;

    public PreferredDeskBooker(EdgeSession session, AppLogger logger)
    {
        _session = session;
        _logger = logger;
    }

    public async Task<bool> BookPreferredDeskAsync(
        AppConfig config,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var selectedDate = await ReadSelectedDateAsync();
        if (selectedDate is null)
        {
            throw new InvalidOperationException(
                "The selected booking date could not be read from the search results page.");
        }

        _logger.Info($"Attempting booking for selected date {selectedDate:yyyy-MM-dd}.");

        var rows = _session.Page.Locator("table tbody tr, [role='row']");
        var rowCount = await rows.CountAsync();

        foreach (var priority in config.Booking.DeskPriorities)
        {
            var deskRegex = CreateDeskRegex(priority);

            for (var index = 0; index < rowCount; index++)
            {
                cancellationToken.ThrowIfCancellationRequested();

                var row = rows.Nth(index);
                if (!await row.IsVisibleAsync())
                {
                    continue;
                }

                var rowText = NormalizeWhitespace(await row.InnerTextAsync());
                if (!deskRegex.IsMatch(rowText))
                {
                    continue;
                }

                var fullDeskNumber = ExtractDeskNumber(rowText, priority);
                var bookButton = await FindVisibleBookButtonAsync(row);

                if (bookButton is null)
                {
                    _logger.Warn(
                        $"Configured desk '{priority}' was found in row '{rowText}', but no visible Book button exists.");
                    continue;
                }

                if (!await IsEnabledAsync(bookButton))
                {
                    _logger.Info(
                        $"Configured desk '{fullDeskNumber}' is visible, but its Book button is disabled.");
                    continue;
                }

                _logger.Info(
                    $"Booking configured desk '{fullDeskNumber}' for {selectedDate:yyyy-MM-dd}. " +
                    $"Matched priority token: {priority}.");

                await bookButton.ClickAsync();
                _logger.Info($"Book button clicked for desk '{fullDeskNumber}'.");

                await Task.Delay(1500, cancellationToken);

                if (!await VerifyBookingAsync(
                        config,
                        selectedDate.Value,
                        fullDeskNumber,
                        priority,
                        cancellationToken))
                {
                    throw new InvalidOperationException(
                        $"The Book button was clicked for desk '{fullDeskNumber}' on " +
                        $"{selectedDate:yyyy-MM-dd}, but the booking was not found in My bookings.");
                }

                _logger.Info(
                    $"BOOKING CONFIRMED: {selectedDate:yyyy-MM-dd}, desk {fullDeskNumber}.");
                return true;
            }
        }

        _logger.Info("No enabled Book button was found for any configured priority desk.");
        return false;
    }

    private async Task<bool> VerifyBookingAsync(
        AppConfig config,
        DateOnly date,
        string fullDeskNumber,
        string priority,
        CancellationToken cancellationToken)
    {
        _logger.Info($"Verifying booking in My bookings: {date:yyyy-MM-dd}, desk {fullDeskNumber}.");

        await _session.Page.GotoAsync(
            config.Ewrs.MyBookingsUrl,
            new PageGotoOptions
            {
                WaitUntil = WaitUntilState.DOMContentLoaded,
                Timeout = 60_000
            });

        await Task.Delay(TimeSpan.FromSeconds(5), cancellationToken);

        var rows = _session.Page.Locator("table tbody tr, [role='row']");
        var rowCount = await rows.CountAsync();
        var dateText = date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        var priorityRegex = CreateDeskRegex(priority);

        for (var index = 0; index < rowCount; index++)
        {
            var row = rows.Nth(index);
            if (!await row.IsVisibleAsync())
            {
                continue;
            }

            var rowText = NormalizeWhitespace(await row.InnerTextAsync());
            if (!rowText.Contains(dateText, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (rowText.Contains(fullDeskNumber, StringComparison.OrdinalIgnoreCase) ||
                priorityRegex.IsMatch(rowText))
            {
                _logger.Info($"Verified booking row: {rowText}");
                return true;
            }
        }

        return false;
    }

    private async Task<DateOnly?> ReadSelectedDateAsync()
    {
        var inputs = _session.Page.Locator("input");
        var inputCount = await inputs.CountAsync();

        for (var index = 0; index < inputCount; index++)
        {
            var input = inputs.Nth(index);
            if (!await input.IsVisibleAsync())
            {
                continue;
            }

            string value;
            try
            {
                value = await input.InputValueAsync();
            }
            catch (PlaywrightException)
            {
                continue;
            }

            if (TryParseDate(value, out var inputDate))
            {
                return inputDate;
            }
        }

        var bodyText = NormalizeWhitespace(await _session.Page.Locator("body").InnerTextAsync());
        var match = Regex.Match(
            bodyText,
            @"\b(?<month>Jan|Feb|Mar|Apr|May|Jun|Jul|Aug|Sep|Oct|Nov|Dec)\s+(?<day>\d{1,2}),\s+(?<year>\d{4})\b",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

        if (match.Success && DateOnly.TryParseExact(
                match.Value,
                "MMM d, yyyy",
                CultureInfo.InvariantCulture,
                DateTimeStyles.AllowWhiteSpaces,
                out var bodyDate))
        {
            return bodyDate;
        }

        return null;
    }

    private static bool TryParseDate(string value, out DateOnly date)
    {
        var formats = new[]
        {
            "M/d/yyyy",
            "MM/dd/yyyy",
            "yyyy-MM-dd",
            "MMM d, yyyy"
        };

        return DateOnly.TryParseExact(
            value.Trim(),
            formats,
            CultureInfo.InvariantCulture,
            DateTimeStyles.AllowWhiteSpaces,
            out date);
    }

    private static async Task<ILocator?> FindVisibleBookButtonAsync(ILocator row)
    {
        var byRole = row.GetByRole(
            AriaRole.Button,
            new LocatorGetByRoleOptions { Name = "Book", Exact = true });

        var button = await FirstVisibleAsync(byRole);
        if (button is not null)
        {
            return button;
        }

        return await FirstVisibleAsync(
            row.Locator("button").Filter(new LocatorFilterOptions { HasText = "Book" }));
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

    private static async Task<bool> IsEnabledAsync(ILocator locator)
    {
        if (!await locator.IsEnabledAsync())
        {
            return false;
        }

        var disabled = await locator.GetAttributeAsync("disabled");
        var ariaDisabled = await locator.GetAttributeAsync("aria-disabled");
        var classValue = await locator.GetAttributeAsync("class") ?? string.Empty;

        return disabled is null &&
               !string.Equals(ariaDisabled, "true", StringComparison.OrdinalIgnoreCase) &&
               !classValue.Contains("disabled", StringComparison.OrdinalIgnoreCase);
    }

    private static string ExtractDeskNumber(string rowText, string priority)
    {
        var tokens = rowText.Split(
            new[] { ' ', '\t', '\r', '\n' },
            StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var regex = CreateDeskRegex(priority);

        return tokens.FirstOrDefault(regex.IsMatch) ?? priority;
    }

    private static Regex CreateDeskRegex(string desk)
    {
        return new Regex(
            $@"\b{Regex.Escape(desk.Trim())}\b",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);
    }

    private static string NormalizeWhitespace(string value)
    {
        return Regex.Replace(value ?? string.Empty, @"\s+", " ").Trim();
    }
}
