using System.Diagnostics;
using System.IO;

namespace CameraInspector;

internal static class PreferenceFile
{
    internal static void Write(string path, string value)
    {
        string directory = Path.GetDirectoryName(path)!;
        string temporary = Path.Combine(directory, Path.GetRandomFileName());
        try
        {
            Directory.CreateDirectory(directory);
            try
            {
                File.WriteAllText(temporary, value);
                File.Move(temporary, path, overwrite: true);
            }
            finally
            {
                File.Delete(temporary);
            }
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            Debug.WriteLine(error);
        }
    }
}
