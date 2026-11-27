using System.Diagnostics;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace CameraInfo;

internal sealed record ShutterDecrease(uint From, uint To, DateTimeOffset ObservedAt);
internal readonly record struct ShutterComparison(uint? PreviousCount, ShutterDecrease? LastDecrease);

internal static class ShutterHistory
{
    internal static string DirectoryPath { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Camera Info", "Shutter History");

    private static readonly JsonSerializerOptions JsonOptions = new() { RespectRequiredConstructorParameters = true };
    private sealed record SavedReading(uint Count, ShutterDecrease? LastDecrease);

    internal static ShutterComparison? TryRecord(CameraReading reading, string directory)
    {
        if (reading.Count is not { UsbProperty: uint property } count || string.IsNullOrWhiteSpace(reading.Serial))
            return null;

        string identity = $"{reading.Model.Trim().ToUpperInvariant()}\n{reading.Serial.Trim()}\n{property:X4}";
        if (count.IsUpperBound)
            identity += "\nmechanical upper bound";
        string path = Path.Combine(directory, Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(identity))) + ".json");
        try
        {
            Directory.CreateDirectory(directory);
            // Hold the lock across comparison and replacement so another app instance cannot overwrite it.
            using var locked = new FileStream(path + ".lock", FileMode.OpenOrCreate, FileAccess.Write, FileShare.None);
            SavedReading? previous = null;
            try
            {
                previous = JsonSerializer.Deserialize<SavedReading>(File.ReadAllText(path), JsonOptions)
                    ?? throw new JsonException("Missing shutter reading.");
            }
            catch (FileNotFoundException) { }

            var decrease = previous is not null && count.Value < previous.Count
                ? new ShutterDecrease(previous.Count, count.Value, DateTimeOffset.UtcNow)
                : previous?.LastDecrease;

            if (previous?.Count != count.Value)
            {
                string temporary = path + ".tmp";
                File.WriteAllText(temporary, JsonSerializer.Serialize(new SavedReading(count.Value, decrease), JsonOptions));
                File.Move(temporary, path, overwrite: true);
            }

            return new(previous?.Count, decrease);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException)
        {
            Debug.WriteLine(error);
            return null;
        }
    }
}
