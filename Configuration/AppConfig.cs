using System.Globalization;
using System.Text.Json;

namespace MyBookingGts.Configuration;

public sealed class AppConfig
{
    public EdgeConfig Edge { get; set; } = new();
    public EwrsConfig Ewrs { get; set; } = new();
    public BookingConfig Booking { get; set; } = new();
    public List<string> ExcludedDates { get; set; } = [];
    public List<ExcludedDateRangeConfig> ExcludedDateRanges { get; set; } = [];
    public LoggingConfig Logging { get; set; } = new();

    public static AppConfig Load(string path)
    {
        if (!File.Exists(path))
        {
            throw new FileNotFoundException("Configuration file was not found.", path);
        }

        var json = File.ReadAllText(path);
        var options = new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true,
            ReadCommentHandling = JsonCommentHandling.Skip,
            AllowTrailingCommas = true
        };

        var config = JsonSerializer.Deserialize<AppConfig>(json, options)
                     ?? throw new InvalidOperationException("Configuration file is empty or invalid.");

        config.Validate();
        return config;
    }

    public HashSet<DateOnly> GetExcludedDates()
    {
        var result = new HashSet<DateOnly>();

        foreach (var value in ExcludedDates)
        {
            result.Add(ParseDate(value, "ExcludedDates"));
        }

        foreach (var range in ExcludedDateRanges)
        {
            var from = ParseDate(range.From, "ExcludedDateRanges.From");
            var to = ParseDate(range.To, "ExcludedDateRanges.To");

            if (to < from)
            {
                throw new InvalidOperationException(
                    $"Excluded date range is invalid: {range.From}..{range.To}.");
            }

            for (var date = from; date <= to; date = date.AddDays(1))
            {
                result.Add(date);
            }
        }

        return result;
    }

    public string? GetExclusionReason(DateOnly date)
    {
        if (ExcludedDates.Any(x => ParseDate(x, "ExcludedDates") == date))
        {
            return "excluded date";
        }

        foreach (var range in ExcludedDateRanges)
        {
            var from = ParseDate(range.From, "ExcludedDateRanges.From");
            var to = ParseDate(range.To, "ExcludedDateRanges.To");
            if (date >= from && date <= to)
            {
                return string.IsNullOrWhiteSpace(range.Reason)
                    ? $"excluded range {from:yyyy-MM-dd}..{to:yyyy-MM-dd}"
                    : range.Reason.Trim();
            }
        }

        return null;
    }

    private void Validate()
    {
        if (string.IsNullOrWhiteSpace(Edge.ProfileDirectory))
            throw new InvalidOperationException("Edge.ProfileDirectory is required.");

        if (!Edge.UseFirefox)
        {
            if (string.IsNullOrWhiteSpace(Edge.ExecutablePath))
                throw new InvalidOperationException("Edge.ExecutablePath is required when Edge.UseFirefox is false.");

            if (Edge.RemoteDebuggingPort is < 1 or > 65535)
                throw new InvalidOperationException("Edge.RemoteDebuggingPort must be between 1 and 65535.");
        }

        if (Edge.StartupTimeoutSeconds < 1)
            throw new InvalidOperationException("Edge.StartupTimeoutSeconds must be greater than zero.");

        if (!Uri.TryCreate(Ewrs.HomeUrl, UriKind.Absolute, out _))
            throw new InvalidOperationException("Ewrs.HomeUrl is invalid.");

        if (!Uri.TryCreate(Ewrs.MyBookingsUrl, UriKind.Absolute, out _))
            throw new InvalidOperationException("Ewrs.MyBookingsUrl is invalid.");

        if (string.IsNullOrWhiteSpace(Ewrs.ExpectedLocation))
            throw new InvalidOperationException("Ewrs.ExpectedLocation is required.");

        if (string.IsNullOrWhiteSpace(Ewrs.ExpectedFloor))
            throw new InvalidOperationException("Ewrs.ExpectedFloor is required.");

        if (string.IsNullOrWhiteSpace(Ewrs.ExpectedTimePeriod))
            throw new InvalidOperationException("Ewrs.ExpectedTimePeriod is required.");

        _ = TimeZoneInfo.FindSystemTimeZoneById(Ewrs.TimeZoneId);

        if (Booking.DeskPriorities.Count == 0 || Booking.DeskPriorities.Any(string.IsNullOrWhiteSpace))
            throw new InvalidOperationException("Booking.DeskPriorities must contain at least one value.");

        if (Booking.DatesToCheck < 1)
            throw new InvalidOperationException("Booking.DatesToCheck must be greater than zero.");

        if (Booking.MaximumSearchDaysAhead < Booking.DatesToCheck)
            throw new InvalidOperationException("Booking.MaximumSearchDaysAhead is too small.");

        if (Booking.RetryDelayMinMinutes < 1 ||
            Booking.RetryDelayMaxMinutes < Booking.RetryDelayMinMinutes)
        {
            throw new InvalidOperationException("Booking retry delay range is invalid.");
        }

        if (Booking.AuthenticationCheckSeconds < 1)
            throw new InvalidOperationException("Booking.AuthenticationCheckSeconds must be greater than zero.");

        if (Booking.TechnicalRetrySeconds < 1)
            throw new InvalidOperationException("Booking.TechnicalRetrySeconds must be greater than zero.");

        if (Booking.TechnicalRetryCount < 1)
            throw new InvalidOperationException("Booking.TechnicalRetryCount must be greater than zero.");

        if (Booking.SearchResultWaitSeconds < 1)
            throw new InvalidOperationException("Booking.SearchResultWaitSeconds must be greater than zero.");

        if (string.IsNullOrWhiteSpace(Logging.LogFilePath))
            throw new InvalidOperationException("Logging.LogFilePath is required.");

        if (string.IsNullOrWhiteSpace(Logging.DiagnosticsDirectory))
            throw new InvalidOperationException("Logging.DiagnosticsDirectory is required.");

        if (Logging.MaximumLogFileMegabytes < 1)
            throw new InvalidOperationException("Logging.MaximumLogFileMegabytes must be greater than zero.");

        if (Logging.RetainedLogFiles < 1)
            throw new InvalidOperationException("Logging.RetainedLogFiles must be greater than zero.");

        _ = GetExcludedDates();
    }

    private static DateOnly ParseDate(string value, string settingName)
    {
        if (!DateOnly.TryParseExact(
                value,
                "yyyy-MM-dd",
                CultureInfo.InvariantCulture,
                DateTimeStyles.None,
                out var date))
        {
            throw new InvalidOperationException(
                $"{settingName} contains invalid date '{value}'. Expected format: yyyy-MM-dd.");
        }

        return date;
    }
}

public sealed class EdgeConfig
{
    public bool UseFirefox { get; set; }
    public string ExecutablePath { get; set; } = string.Empty;

    // Leave empty to use the Playwright-managed patched Firefox build.
    // A regular installed Firefox is not compatible with Playwright automation.
    public string FirefoxExecutablePath { get; set; } = string.Empty;

    public int RemoteDebuggingPort { get; set; } = 9222;
    public string ProfileDirectory { get; set; } = string.Empty;
    public int StartupTimeoutSeconds { get; set; } = 30;
}

public sealed class EwrsConfig
{
    public string HomeUrl { get; set; } = string.Empty;
    public string MyBookingsUrl { get; set; } = string.Empty;
    public string ExpectedLocation { get; set; } = string.Empty;
    public string ExpectedFloor { get; set; } = string.Empty;
    public string ExpectedTimePeriod { get; set; } = "Full Day";
    public string TimeZoneId { get; set; } = "Eastern Standard Time";
}

public sealed class BookingConfig
{
    public List<string> DeskPriorities { get; set; } = [];
    public int DatesToCheck { get; set; } = 3;
    public int MaximumSearchDaysAhead { get; set; } = 90;
    public int RetryDelayMinMinutes { get; set; } = 15;
    public int RetryDelayMaxMinutes { get; set; } = 20;
    public int AuthenticationCheckSeconds { get; set; } = 5;
    public int TechnicalRetrySeconds { get; set; } = 30;
    public int TechnicalRetryCount { get; set; } = 3;
    public int SearchResultWaitSeconds { get; set; } = 15;
}

public sealed class ExcludedDateRangeConfig
{
    public string From { get; set; } = string.Empty;
    public string To { get; set; } = string.Empty;
    public string? Reason { get; set; }
}

public sealed class LoggingConfig
{
    public string LogFilePath { get; set; } = string.Empty;
    public string DiagnosticsDirectory { get; set; } = string.Empty;
    public int MaximumLogFileMegabytes { get; set; } = 20;
    public int RetainedLogFiles { get; set; } = 5;
}
