using System.Globalization;
using System.Text.RegularExpressions;
using Microsoft.Playwright;
using MyBookingGts.Configuration;
using MyBookingGts.Domain;
using MyBookingGts.Infrastructure;

namespace MyBookingGts.Services;

public sealed class EwrsRobot
{
    private static readonly Regex IsoDateRegex = new(
        @"\b(?<year>\d{4})-(?<month>\d{2})-(?<day>\d{2})\b",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private readonly EdgeSession _edge;
    private readonly AppLogger _logger;
    private readonly DiagnosticCapture _diagnostics;

    public EwrsRobot(EdgeSession edge, AppLogger logger, DiagnosticCapture diagnostics)
    {
        _edge = edge;
        _logger = logger;
        _diagnostics = diagnostics;
    }

    public async Task WaitForManualAuthenticationAsync(
        AppConfig config,
        CancellationToken cancellationToken)
    {
        _logger.Warn("EWRS workspace page is not visible. Manual login and MFA are required. The robot will not enter credentials.");

        var lastReminder = DateTimeOffset.MinValue;

        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            _edge.Page = FindBestPage();

            if (await IsWorkspacePageAsync(_edge.Page))
            {
                _logger.Info("EWRS authentication detected. Restarting from My bookings.");
                return;
            }

            if (DateTimeOffset.Now - lastReminder >= TimeSpan.FromMinutes(1))
            {
                _logger.Warn($"Still waiting for manual EWRS authentication. Current URL: {_edge.Page.Url}");
                lastReminder = DateTimeOffset.Now;
            }

            await Task.Delay(
                TimeSpan.FromSeconds(config.Booking.AuthenticationCheckSeconds),
                cancellationToken);
        }
    }

    public async Task<CycleOutcome> RunCycleAsync(
        AppConfig config,
        int cycleNumber,
        CancellationToken cancellationToken)
    {
        _logger.Info($"========== CYCLE {cycleNumber} START ==========");

        var bookedDates = await ReadAllBookedDatesAsync(config, cancellationToken);
        _logger.Info(bookedDates.Count == 0
            ? "Existing bookings: none"
            : $"Existing bookings: {string.Join(", ", bookedDates.OrderBy(x => x).Select(x => x.ToString("yyyy-MM-dd")))}");

        var timeZone = TimeZoneInfo.FindSystemTimeZoneById(config.Ewrs.TimeZoneId);
        var torontoNow = TimeZoneInfo.ConvertTime(DateTimeOffset.UtcNow, timeZone);
        var firstDate = DateOnly.FromDateTime(torontoNow.Date).AddDays(1);
        var excludedDates = config.GetExcludedDates();

        _logger.Info($"Toronto date/time: {torontoNow:yyyy-MM-dd HH:mm:ss zzz}");
        _logger.Info($"Search starts from: {firstDate:yyyy-MM-dd}");

        var checkedEnabledDates = 0;

        for (var offset = 0;
             offset < config.Booking.MaximumSearchDaysAhead &&
             checkedEnabledDates < config.Booking.DatesToCheck;
             offset++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var candidate = firstDate.AddDays(offset);

            if (candidate.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday)
            {
                _logger.Info($"Skip {candidate:yyyy-MM-dd}: {candidate.DayOfWeek}.");
                continue;
            }

            if (bookedDates.Contains(candidate))
            {
                _logger.Info($"Skip {candidate:yyyy-MM-dd}: already present in My bookings.");
                continue;
            }

            if (excludedDates.Contains(candidate))
            {
                _logger.Info($"Skip {candidate:yyyy-MM-dd}: {config.GetExclusionReason(candidate)}.");
                continue;
            }

            await OpenHomeAndVerifyFixedValuesAsync(config, cancellationToken);

            var dateSelection = await SelectDateAsync(config, candidate, cancellationToken);
            if (dateSelection == DateSelectionOutcome.Disabled)
            {
                _logger.Info($"Skip {candidate:yyyy-MM-dd}: date is disabled in the EWRS calendar. It does not count toward the {config.Booking.DatesToCheck} checked dates.");
                continue;
            }

            checkedEnabledDates++;
            _logger.Info($"Checking enabled date {candidate:yyyy-MM-dd} ({checkedEnabledDates}/{config.Booking.DatesToCheck}).");

            await EnsureFullDayAndRepeatOffAsync(config, cancellationToken);
            await ClickSearchAndWaitAsync(config, cancellationToken);

            var match = await FindPreferredDeskAsync(config, candidate);
            if (match is not null)
            {
                await StopBeforeBookingAsync(config, match);
                _logger.Info($"========== CYCLE {cycleNumber} STOPPED: PREFERRED DESK FOUND ==========");
                return CycleOutcome.PreferredDeskFound;
            }

            var bodyText = await _edge.Page.Locator("body").InnerTextAsync();
            if (ContainsText(bodyText, "No desks available"))
            {
                _logger.Info($"No desks are available for {candidate:yyyy-MM-dd}.");
            }
            else
            {
                _logger.Info($"No configured priority desk is visible for {candidate:yyyy-MM-dd}.");
            }
        }

        if (checkedEnabledDates < config.Booking.DatesToCheck)
        {
            _logger.Warn(
                $"Only {checkedEnabledDates} enabled candidate date(s) were found within {config.Booking.MaximumSearchDaysAhead} days.");
        }
        else
        {
            _logger.Info($"Checked {checkedEnabledDates} enabled dates. No configured priority desk was found.");
        }

        _logger.Info($"========== CYCLE {cycleNumber} END ==========");
        return CycleOutcome.NoPreferredDeskFound;
    }

    private async Task<HashSet<DateOnly>> ReadAllBookedDatesAsync(
        AppConfig config,
        CancellationToken cancellationToken)
    {
        await NavigateAsync(config.Ewrs.MyBookingsUrl, cancellationToken);
        await EnsureWorkspaceOrThrowAsync();

        if (!await IsTextVisibleAsync("My bookings"))
        {
            throw new InvalidOperationException("My bookings page did not display the expected heading.");
        }

        var dates = new HashSet<DateOnly>();
        var seenPageSignatures = new HashSet<string>(StringComparer.Ordinal);
        var rowsRead = 0;
        int? declaredTotalRows = null;

        for (var pageNumber = 1; pageNumber <= 100; pageNumber++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var rowTexts = await GetBookingRowTextsAsync();
            var signature = string.Join("\u001f", rowTexts);
            if (!seenPageSignatures.Add(signature))
            {
                throw new InvalidOperationException(
                    "My bookings pagination repeated the same page. The complete booking list cannot be trusted.");
            }

            _logger.Info($"My bookings page {pageNumber}: {rowTexts.Count} booking row(s).");

            foreach (var rowText in rowTexts)
            {
                var match = IsoDateRegex.Match(rowText);
                if (!match.Success)
                {
                    throw new InvalidOperationException(
                        $"A My bookings row does not contain an ISO date: {NormalizeWhitespace(rowText)}");
                }

                var date = new DateOnly(
                    int.Parse(match.Groups["year"].Value, CultureInfo.InvariantCulture),
                    int.Parse(match.Groups["month"].Value, CultureInfo.InvariantCulture),
                    int.Parse(match.Groups["day"].Value, CultureInfo.InvariantCulture));

                dates.Add(date);
                rowsRead++;
                _logger.Info($"Booking row: {NormalizeWhitespace(rowText)}");
            }

            declaredTotalRows ??= await ReadDeclaredResultCountAsync();

            var nextButton = await FindVisibleNextPageButtonAsync();
            if (nextButton is null || !await IsLocatorEnabledAsync(nextButton))
            {
                break;
            }

            var oldSignature = signature;
            await nextButton.ClickAsync();
            await WaitForBookingPageToChangeAsync(oldSignature, cancellationToken);
        }

        if (declaredTotalRows.HasValue && rowsRead < declaredTotalRows.Value)
        {
            throw new InvalidOperationException(
                $"My bookings reports {declaredTotalRows.Value} results, but only {rowsRead} rows were read. Nothing will be booked.");
        }

        return dates;
    }

    private async Task<List<string>> GetBookingRowTextsAsync()
    {
        var rows = _edge.Page.Locator("table tbody tr");
        var count = await rows.CountAsync();
        var result = new List<string>();

        for (var index = 0; index < count; index++)
        {
            var row = rows.Nth(index);
            if (!await row.IsVisibleAsync())
            {
                continue;
            }

            var text = await row.InnerTextAsync();
            if (IsoDateRegex.IsMatch(text))
            {
                result.Add(text);
            }
        }

        if (result.Count > 0)
        {
            return result;
        }

        // Fallback for a non-table responsive layout.
        var candidates = _edge.Page.Locator("[role='row']");
        count = await candidates.CountAsync();
        for (var index = 0; index < count; index++)
        {
            var row = candidates.Nth(index);
            if (!await row.IsVisibleAsync())
            {
                continue;
            }

            var text = await row.InnerTextAsync();
            if (IsoDateRegex.IsMatch(text))
            {
                result.Add(text);
            }
        }

        return result;
    }

    private async Task<int?> ReadDeclaredResultCountAsync()
    {
        var bodyText = await _edge.Page.Locator("body").InnerTextAsync();
        var match = Regex.Match(
            bodyText,
            @"Displaying\s+\d+\s*-\s*\d+\s+of\s+(?<count>\d+)\s+results",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

        return match.Success
            ? int.Parse(match.Groups["count"].Value, CultureInfo.InvariantCulture)
            : null;
    }

    private async Task<ILocator?> FindVisibleNextPageButtonAsync()
    {
        var selectors = new[]
        {
            "button[aria-label*='next page' i]",
            "[role='button'][aria-label*='next page' i]",
            "button[title*='next page' i]",
            "button[aria-label='Next']",
            "a[aria-label*='next page' i]"
        };

        foreach (var selector in selectors)
        {
            var locator = _edge.Page.Locator(selector);
            var count = await locator.CountAsync();
            for (var index = 0; index < count; index++)
            {
                var item = locator.Nth(index);
                if (await item.IsVisibleAsync())
                {
                    return item;
                }
            }
        }

        return null;
    }

    private async Task WaitForBookingPageToChangeAsync(
        string oldSignature,
        CancellationToken cancellationToken)
    {
        var deadline = DateTimeOffset.UtcNow.AddSeconds(10);
        while (DateTimeOffset.UtcNow < deadline)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var newSignature = string.Join("\u001f", await GetBookingRowTextsAsync());
            if (!string.Equals(newSignature, oldSignature, StringComparison.Ordinal))
            {
                return;
            }

            await Task.Delay(250, cancellationToken);
        }

        throw new TimeoutException("My bookings page did not change after clicking Next page.");
    }

    private async Task OpenHomeAndVerifyFixedValuesAsync(
        AppConfig config,
        CancellationToken cancellationToken)
    {
        await NavigateAsync(config.Ewrs.HomeUrl, cancellationToken);
        await EnsureWorkspaceOrThrowAsync();

        if (!await IsTextVisibleAsync("Book a workspace"))
        {
            throw new InvalidOperationException("Home page did not display 'Book a workspace'.");
        }

        var locationBlock = await ReadTextBlockNearLabelAsync("Location");
        var floorBlock = await ReadTextBlockNearLabelAsync("Floor(s)");

        var locationMatches = ContainsText(locationBlock, config.Ewrs.ExpectedLocation);
        var floorMatches = ContainsText(floorBlock, config.Ewrs.ExpectedFloor);

        _logger.Info($"Location block: {NormalizeWhitespace(locationBlock)}");
        _logger.Info($"Floor block: {NormalizeWhitespace(floorBlock)}");

        if (locationMatches && floorMatches)
        {
            return;
        }

        await _diagnostics.CaptureAsync(
            _edge.Page,
            config.Logging,
            "location-floor-mismatch",
            new[]
            {
                $"Expected location: {config.Ewrs.ExpectedLocation}",
                $"Actual location block: {NormalizeWhitespace(locationBlock)}",
                $"Expected floor: {config.Ewrs.ExpectedFloor}",
                $"Actual floor block: {NormalizeWhitespace(floorBlock)}"
            });

        throw new SafetyMismatchException(
            "Location or floor does not match the configured value. No booking action was performed.");
    }

    private async Task<DateSelectionOutcome> SelectDateAsync(
        AppConfig config,
        DateOnly date,
        CancellationToken cancellationToken)
    {
        await OpenDatePanelAsync(cancellationToken);
        await OpenCalendarAsync(cancellationToken);

        var dateCell = await FindCalendarDateCellAsync(date, cancellationToken);
        if (dateCell is null)
        {
            throw new InvalidOperationException(
                $"Calendar cell for {date:yyyy-MM-dd} could not be found within the configured search horizon.");
        }

        if (!await IsCalendarCellEnabledAsync(dateCell))
        {
            return DateSelectionOutcome.Disabled;
        }

        await dateCell.ClickAsync();
        _logger.Info($"Selected date in EWRS calendar: {date:yyyy-MM-dd}.");
        return DateSelectionOutcome.Selected;
    }

    private async Task OpenDatePanelAsync(CancellationToken cancellationToken)
    {
        if (await IsTextVisibleAsync("Start Date"))
        {
            return;
        }

        var selectDate = _edge.Page.GetByText("Select a date", new PageGetByTextOptions { Exact = true });
        if (await ClickFirstVisibleAsync(selectDate))
        {
            await WaitForTextVisibleAsync("Start Date", TimeSpan.FromSeconds(5), cancellationToken);
            return;
        }

        var dateLabel = _edge.Page.GetByText("Date", new PageGetByTextOptions { Exact = true });
        var count = await dateLabel.CountAsync();
        for (var index = 0; index < count; index++)
        {
            var item = dateLabel.Nth(index);
            if (!await item.IsVisibleAsync())
            {
                continue;
            }

            var parent = item.Locator("..");
            await parent.ClickAsync();
            await WaitForTextVisibleAsync("Start Date", TimeSpan.FromSeconds(5), cancellationToken);
            return;
        }

        throw new InvalidOperationException("Date selector could not be opened.");
    }

    private async Task OpenCalendarAsync(CancellationToken cancellationToken)
    {
        if (await IsCalendarVisibleAsync())
        {
            return;
        }

        var selectors = new[]
        {
            "button[aria-label*='open calendar' i]",
            "button[aria-label*='calendar' i]",
            "[role='button'][aria-label*='calendar' i]"
        };

        foreach (var selector in selectors)
        {
            var locator = _edge.Page.Locator(selector);
            var count = await locator.CountAsync();
            for (var index = count - 1; index >= 0; index--)
            {
                var button = locator.Nth(index);
                if (!await button.IsVisibleAsync())
                {
                    continue;
                }

                await button.ClickAsync();
                await WaitForCalendarVisibleAsync(cancellationToken);
                return;
            }
        }

        var startDateLabel = _edge.Page.GetByText("Start Date", new PageGetByTextOptions { Exact = true });
        var labelCount = await startDateLabel.CountAsync();
        for (var index = 0; index < labelCount; index++)
        {
            var label = startDateLabel.Nth(index);
            if (!await label.IsVisibleAsync())
            {
                continue;
            }

            var block = label.Locator("..");
            for (var level = 0; level < 3; level++)
            {
                var buttons = block.Locator("button");
                var buttonCount = await buttons.CountAsync();
                if (buttonCount > 0)
                {
                    await buttons.Last.ClickAsync();
                    await WaitForCalendarVisibleAsync(cancellationToken);
                    return;
                }

                block = block.Locator("..");
            }
        }

        throw new InvalidOperationException("Calendar popup could not be opened.");
    }

    private async Task<ILocator?> FindCalendarDateCellAsync(
        DateOnly date,
        CancellationToken cancellationToken)
    {
        for (var monthAttempt = 0; monthAttempt < 6; monthAttempt++)
        {
            var cell = await FindDateCellInCurrentCalendarAsync(date);
            if (cell is not null)
            {
                return cell;
            }

            var nextButton = await FindCalendarNextButtonAsync();
            if (nextButton is null || !await IsLocatorEnabledAsync(nextButton))
            {
                return null;
            }

            await nextButton.ClickAsync();
            await Task.Delay(250, cancellationToken);
        }

        return null;
    }

    private async Task<ILocator?> FindDateCellInCurrentCalendarAsync(DateOnly date)
    {
        var dateValue = date.ToDateTime(TimeOnly.MinValue);
        var labels = new[]
        {
            dateValue.ToString("MMMM d, yyyy", CultureInfo.InvariantCulture),
            dateValue.ToString("MMM d, yyyy", CultureInfo.InvariantCulture),
            dateValue.ToString("dddd, MMMM d, yyyy", CultureInfo.InvariantCulture)
        };

        foreach (var label in labels)
        {
            var escaped = label.Replace("'", "\\'");
            var locator = _edge.Page.Locator($"[aria-label='{escaped}']");
            var count = await locator.CountAsync();
            for (var index = 0; index < count; index++)
            {
                var item = locator.Nth(index);
                if (await item.IsVisibleAsync())
                {
                    return item;
                }
            }
        }

        // Fallback by day number is safe only when the visible calendar header
        // is confirmed to be the target month and year.
        if (await CalendarShowsTargetMonthAsync(date))
        {
            var cells = _edge.Page.Locator(
                ".mat-calendar-body-cell, .mat-mdc-calendar-body-cell, [role='gridcell']");
            var cellCount = await cells.CountAsync();
            for (var index = 0; index < cellCount; index++)
            {
                var cell = cells.Nth(index);
                if (!await cell.IsVisibleAsync())
                {
                    continue;
                }

                var text = (await cell.InnerTextAsync()).Trim();
                if (text == date.Day.ToString(CultureInfo.InvariantCulture))
                {
                    return cell;
                }
            }
        }

        return null;
    }

    private async Task<bool> CalendarShowsTargetMonthAsync(DateOnly date)
    {
        var selectors = new[]
        {
            ".mat-calendar-period-button",
            ".mat-mdc-calendar-period-button",
            "button[aria-label*='month and year' i]"
        };

        var expected = new[]
        {
            date.ToDateTime(TimeOnly.MinValue).ToString("MMM yyyy", CultureInfo.InvariantCulture),
            date.ToDateTime(TimeOnly.MinValue).ToString("MMMM yyyy", CultureInfo.InvariantCulture)
        };

        foreach (var selector in selectors)
        {
            var headers = _edge.Page.Locator(selector);
            var count = await headers.CountAsync();
            for (var index = 0; index < count; index++)
            {
                var header = headers.Nth(index);
                if (!await header.IsVisibleAsync())
                {
                    continue;
                }

                var text = NormalizeWhitespace(await header.InnerTextAsync());
                if (expected.Any(value =>
                        string.Equals(text, value, StringComparison.OrdinalIgnoreCase)))
                {
                    return true;
                }
            }
        }

        return false;
    }

    private async Task<ILocator?> FindCalendarNextButtonAsync()
    {
        var selectors = new[]
        {
            ".mat-calendar-next-button",
            ".mat-mdc-calendar-next-button",
            "button[aria-label*='next month' i]",
            "button[aria-label*='next calendar' i]"
        };

        foreach (var selector in selectors)
        {
            var locator = _edge.Page.Locator(selector);
            var count = await locator.CountAsync();
            for (var index = 0; index < count; index++)
            {
                var item = locator.Nth(index);
                if (await item.IsVisibleAsync())
                {
                    return item;
                }
            }
        }

        return null;
    }

    private static async Task<bool> IsCalendarCellEnabledAsync(ILocator cell)
    {
        var disabled = await cell.GetAttributeAsync("disabled");
        var ariaDisabled = await cell.GetAttributeAsync("aria-disabled");
        var classValue = await cell.GetAttributeAsync("class") ?? string.Empty;

        if (disabled is not null ||
            string.Equals(ariaDisabled, "true", StringComparison.OrdinalIgnoreCase) ||
            classValue.Contains("disabled", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var ancestor = cell.Locator("xpath=ancestor-or-self::*[contains(@class, 'disabled')][1]");
        if (await ancestor.CountAsync() > 0)
        {
            return false;
        }

        return await cell.IsEnabledAsync();
    }

    private async Task EnsureFullDayAndRepeatOffAsync(
        AppConfig config,
        CancellationToken cancellationToken)
    {
        var timePeriodBlock = await ReadTextBlockNearLabelAsync("Time Period");
        if (!ContainsText(timePeriodBlock, config.Ewrs.ExpectedTimePeriod))
        {
            throw new SafetyMismatchException(
                $"Time Period is not '{config.Ewrs.ExpectedTimePeriod}'. Actual block: {NormalizeWhitespace(timePeriodBlock)}");
        }

        var repeatText = _edge.Page.GetByText("Repeat", new PageGetByTextOptions { Exact = true });
        var repeatCount = await repeatText.CountAsync();
        if (repeatCount == 0)
        {
            throw new SafetyMismatchException("Repeat control was not found.");
        }

        for (var index = 0; index < repeatCount; index++)
        {
            var label = repeatText.Nth(index);
            if (!await label.IsVisibleAsync())
            {
                continue;
            }

            var block = label.Locator("..");
            for (var level = 0; level < 4; level++)
            {
                var switches = block.Locator("[role='switch'], input[type='checkbox']");
                var switchCount = await switches.CountAsync();
                for (var switchIndex = 0; switchIndex < switchCount; switchIndex++)
                {
                    var control = switches.Nth(switchIndex);
                    if (!await control.IsVisibleAsync())
                    {
                        continue;
                    }

                    cancellationToken.ThrowIfCancellationRequested();
                    var ariaChecked = await control.GetAttributeAsync("aria-checked");
                    var isChecked = string.Equals(ariaChecked, "true", StringComparison.OrdinalIgnoreCase);

                    if (ariaChecked is null)
                    {
                        try
                        {
                            isChecked = await control.IsCheckedAsync();
                        }
                        catch (PlaywrightException)
                        {
                            isChecked = await control.GetAttributeAsync("checked") is not null;
                        }
                    }

                    if (isChecked)
                    {
                        await control.ClickAsync();
                        _logger.Info("Repeat was enabled and has been switched off.");
                    }
                    else
                    {
                        _logger.Info("Repeat is off.");
                    }

                    return;
                }

                block = block.Locator("..");
            }
        }

        throw new SafetyMismatchException("Repeat switch could not be inspected.");
    }

    private async Task ClickSearchAndWaitAsync(
        AppConfig config,
        CancellationToken cancellationToken)
    {
        var search = _edge.Page.GetByText("Search", new PageGetByTextOptions { Exact = true });
        if (!await ClickFirstVisibleAsync(search))
        {
            throw new InvalidOperationException("Search button was not found.");
        }

        _logger.Info("Search button clicked.");

        try
        {
            await _edge.Page.WaitForLoadStateAsync(
                LoadState.NetworkIdle,
                new PageWaitForLoadStateOptions
                {
                    Timeout = config.Booking.SearchResultWaitSeconds * 1000
                });
        }
        catch (PlaywrightException)
        {
            // EWRS may keep background requests open. Continue with a bounded visual wait.
        }

        var deadline = DateTimeOffset.UtcNow.AddSeconds(config.Booking.SearchResultWaitSeconds);
        while (DateTimeOffset.UtcNow < deadline)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await EnsureWorkspaceOrThrowAsync();

            var bodyText = await _edge.Page.Locator("body").InnerTextAsync();
            if (ContainsText(bodyText, "No desks available") ||
                Regex.IsMatch(bodyText, @"\bW\d+\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant))
            {
                return;
            }

            await Task.Delay(500, cancellationToken);
        }

        _logger.Warn("Search result wait elapsed. The current page will still be inspected.");
    }

    private async Task<PreferredDeskMatch?> FindPreferredDeskAsync(
        AppConfig config,
        DateOnly date)
    {
        var bodyText = await _edge.Page.Locator("body").InnerTextAsync();
        var lines = bodyText
            .Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(NormalizeWhitespace)
            .Where(x => x.Length > 0)
            .ToList();

        foreach (var priority in config.Booking.DeskPriorities)
        {
            var regex = CreateDeskRegex(priority);
            var matchingLines = lines
                .Where(line => regex.IsMatch(line))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

            if (matchingLines.Count > 0)
            {
                _logger.Info($"Preferred desk token '{priority}' found for {date:yyyy-MM-dd}.");
                foreach (var line in matchingLines)
                {
                    _logger.Info($"Matching line: {line}");
                }

                return new PreferredDeskMatch(date, priority, matchingLines);
            }
        }

        return null;
    }

    private async Task StopBeforeBookingAsync(
        AppConfig config,
        PreferredDeskMatch match)
    {
        var buttons = await _edge.Page.Locator("button").AllInnerTextsAsync();
        var links = await _edge.Page.Locator("a").AllInnerTextsAsync();

        var extra = new List<string>
        {
            $"Date: {match.Date:yyyy-MM-dd}",
            $"Matched priority: {match.Priority}",
            "No booking or confirmation button was clicked.",
            "Matching lines:"
        };
        extra.AddRange(match.MatchingLines.Select(x => $"  {x}"));
        extra.Add("Visible button texts:");
        extra.AddRange(buttons.Where(x => !string.IsNullOrWhiteSpace(x)).Select(x => $"  {NormalizeWhitespace(x)}"));
        extra.Add("Visible link texts:");
        extra.AddRange(links.Where(x => !string.IsNullOrWhiteSpace(x)).Select(x => $"  {NormalizeWhitespace(x)}"));

        await _diagnostics.CaptureAsync(
            _edge.Page,
            config.Logging,
            "preferred-desk-found",
            extra);

        _logger.Warn("MANUAL ANALYSIS REQUIRED. A configured priority desk is visible. The robot stopped before clicking any booking control.");
    }

    private async Task NavigateAsync(string url, CancellationToken cancellationToken)
    {
        _edge.Page = FindBestPage();
        _logger.Info($"Navigate: {url}");

        await _edge.Page.GotoAsync(
            url,
            new PageGotoOptions
            {
                WaitUntil = WaitUntilState.DOMContentLoaded,
                Timeout = 60_000
            });

        cancellationToken.ThrowIfCancellationRequested();
    }

    private IPage FindBestPage()
    {
        return _edge.Context.Pages.FirstOrDefault(x =>
                   x.Url.Contains("ewrs.gov.on.ca", StringComparison.OrdinalIgnoreCase))
               ?? _edge.Context.Pages.FirstOrDefault()
               ?? _edge.Page;
    }

    private async Task EnsureWorkspaceOrThrowAsync()
    {
        if (!await IsWorkspacePageAsync(_edge.Page))
        {
            throw new AuthenticationRequiredException(
                $"EWRS workspace page is not visible. Current URL: {_edge.Page.Url}");
        }
    }

    private static async Task<bool> IsWorkspacePageAsync(IPage page)
    {
        try
        {
            var header = page.GetByText(
                "Employee Workspace Reservation System",
                new PageGetByTextOptions { Exact = true });

            if (!await AnyVisibleAsync(header))
            {
                return false;
            }

            var bodyText = await page.Locator("body").InnerTextAsync();
            return ContainsText(bodyText, "Book a workspace") ||
                   ContainsText(bodyText, "My bookings");
        }
        catch
        {
            return false;
        }
    }

    private async Task<string> ReadTextBlockNearLabelAsync(string labelText)
    {
        var labels = _edge.Page.GetByText(labelText, new PageGetByTextOptions { Exact = true });
        var count = await labels.CountAsync();
        string? best = null;

        for (var index = 0; index < count; index++)
        {
            var label = labels.Nth(index);
            if (!await label.IsVisibleAsync())
            {
                continue;
            }

            var block = label;
            for (var level = 0; level < 5; level++)
            {
                block = block.Locator("..");
                var text = NormalizeWhitespace(await block.InnerTextAsync());

                if (!ContainsText(text, labelText) || text.Length <= labelText.Length)
                {
                    continue;
                }

                if (best is null || text.Length < best.Length)
                {
                    best = text;
                }
            }
        }

        return best ?? string.Empty;
    }

    private async Task<bool> IsTextVisibleAsync(string text)
    {
        return await AnyVisibleAsync(
            _edge.Page.GetByText(text, new PageGetByTextOptions { Exact = true }));
    }

    private async Task WaitForTextVisibleAsync(
        string text,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        var deadline = DateTimeOffset.UtcNow.Add(timeout);
        while (DateTimeOffset.UtcNow < deadline)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (await IsTextVisibleAsync(text))
            {
                return;
            }

            await Task.Delay(100, cancellationToken);
        }

        throw new TimeoutException($"Text '{text}' did not become visible.");
    }

    private async Task WaitForCalendarVisibleAsync(CancellationToken cancellationToken)
    {
        var deadline = DateTimeOffset.UtcNow.AddSeconds(5);
        while (DateTimeOffset.UtcNow < deadline)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (await IsCalendarVisibleAsync())
            {
                return;
            }

            await Task.Delay(100, cancellationToken);
        }

        throw new TimeoutException("Calendar did not become visible.");
    }

    private async Task<bool> IsCalendarVisibleAsync()
    {
        var calendar = _edge.Page.Locator(
            ".mat-calendar, .mat-mdc-calendar, [role='grid']");
        return await AnyVisibleAsync(calendar);
    }

    private static async Task<bool> ClickFirstVisibleAsync(ILocator locator)
    {
        var count = await locator.CountAsync();
        for (var index = 0; index < count; index++)
        {
            var item = locator.Nth(index);
            if (!await item.IsVisibleAsync())
            {
                continue;
            }

            await item.ClickAsync();
            return true;
        }

        return false;
    }

    private static async Task<bool> AnyVisibleAsync(ILocator locator)
    {
        var count = await locator.CountAsync();
        for (var index = 0; index < count; index++)
        {
            if (await locator.Nth(index).IsVisibleAsync())
            {
                return true;
            }
        }

        return false;
    }

    private static async Task<bool> IsLocatorEnabledAsync(ILocator locator)
    {
        var ariaDisabled = await locator.GetAttributeAsync("aria-disabled");
        var disabled = await locator.GetAttributeAsync("disabled");
        var classValue = await locator.GetAttributeAsync("class") ?? string.Empty;

        if (disabled is not null ||
            string.Equals(ariaDisabled, "true", StringComparison.OrdinalIgnoreCase) ||
            classValue.Contains("disabled", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        return await locator.IsEnabledAsync();
    }

    private static Regex CreateDeskRegex(string desk)
    {
        return new Regex(
            $@"\b{Regex.Escape(desk.Trim())}\b",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);
    }

    private static bool ContainsText(string source, string expected)
    {
        return NormalizeWhitespace(source)
            .Contains(NormalizeWhitespace(expected), StringComparison.OrdinalIgnoreCase);
    }

    private static string NormalizeWhitespace(string value)
    {
        return Regex.Replace(value ?? string.Empty, @"\s+", " ").Trim();
    }
}
