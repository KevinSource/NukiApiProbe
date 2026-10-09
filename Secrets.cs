using System.Text.Json;

namespace NukiApiProbe;

/// <summary>
/// Finds the Nuki API token without it ever living in the project folder.
/// Order: NUKI_API_TOKEN environment variable, then .NET user-secrets ("Nuki:ApiToken").
/// </summary>
public static class Secrets
{
    // Must match <UserSecretsId> in NukiApiProbe.csproj
    private const string UserSecretsId = "37283e67-de66-45d7-8338-c1dda63a28ed";
    private const string TokenKey = "Nuki:ApiToken";

    public static string? GetToken()
    {
        string? fromEnv = Environment.GetEnvironmentVariable("NUKI_API_TOKEN");
        if (!string.IsNullOrWhiteSpace(fromEnv))
            return fromEnv.Trim();

        string path = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "Microsoft", "UserSecrets", UserSecretsId, "secrets.json");
        if (!File.Exists(path))
            return null;

        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(path));
            return doc.RootElement.TryGetProperty(TokenKey, out var value) ? value.GetString()?.Trim() : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
