namespace MyBookingGts.Domain;

public enum CycleOutcome
{
    NoPreferredDeskFound,
    PreferredDeskFound,
    SafetyMismatch
}

public enum DateSelectionOutcome
{
    Selected,
    Disabled
}

public sealed record PreferredDeskMatch(
    DateOnly Date,
    string Priority,
    IReadOnlyList<string> MatchingLines);

public sealed class AuthenticationRequiredException : Exception
{
    public AuthenticationRequiredException(string message) : base(message)
    {
    }
}

public sealed class SafetyMismatchException : Exception
{
    public SafetyMismatchException(string message) : base(message)
    {
    }
}
