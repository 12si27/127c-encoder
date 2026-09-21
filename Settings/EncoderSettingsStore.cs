using System.Text.Json;

namespace Encoder127c.Settings;

internal static class EncoderSettingsStore
{
    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        WriteIndented = true
    };

    private static string SettingsPath => Path.Combine(
        AppContext.BaseDirectory,
        "settings.json");

    public static EncoderSettings? Load()
    {
        try
        {
            if (!File.Exists(SettingsPath))
            {
                return null;
            }

            return JsonSerializer.Deserialize<EncoderSettings>(File.ReadAllText(SettingsPath));
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException)
        {
            return null;
        }
    }

    public static void Save(EncoderSettings settings)
    {
        try
        {
            File.WriteAllText(SettingsPath, JsonSerializer.Serialize(settings, SerializerOptions));
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // Settings persistence should never prevent the app from closing.
        }
    }
}
