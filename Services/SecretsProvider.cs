using System.Text.Json;
using MyBookingGts.Infrastructure;

namespace MyBookingGts.Services;

public sealed class SecretsProvider
{
    private readonly AppLogger _logger;

    public SecretsProvider(AppLogger logger)
    {
        _logger = logger;
    }

    public string? TryReadPassword(string secretsFilePath)
    {
        var fullPath = Path.GetFullPath(secretsFilePath);

        if (!File.Exists(fullPath))
        {
            _logger.Warn($"Secrets file was not found: {fullPath}");
            return null;
        }

        try
        {
            var json = File.ReadAllText(fullPath);
            var options = new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true,
                ReadCommentHandling = JsonCommentHandling.Skip,
                AllowTrailingCommas = true
            };

            var secrets = JsonSerializer.Deserialize<SecretsFile>(json, options);
            if (string.IsNullOrWhiteSpace(secrets?.Password))
            {
                _logger.Warn($"Secrets file does not contain a non-empty Password value: {fullPath}");
                return null;
            }

            return secrets.Password;
        }
        catch (Exception ex)
        {
            _logger.Warn($"Failed to read secrets file '{fullPath}': {ex.Message}");
            return null;
        }
    }

    private sealed class SecretsFile
    {
        public string Password { get; set; } = string.Empty;
    }
}
