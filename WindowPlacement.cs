using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Windows;
using System.Windows.Interop;

namespace CameraInspector;

internal static class WindowPlacement
{
    private const int ShowMaximized = 3;
    private static readonly int PlacementSize = Marshal.SizeOf<NativePlacement>();
    private static readonly JsonSerializerOptions JsonOptions = new() { RespectRequiredConstructorParameters = true };

    private sealed record SavedPlacement(int Left, int Top, int Right, int Bottom, bool Maximized);

    internal static void Attach(Window window, string path)
    {
        if (Read(path) is { } saved)
        {
            window.SizeToContent = SizeToContent.Manual;
            window.SourceInitialized += (_, _) => Restore(window, saved);
        }

        window.Closing += (_, _) => Save(window, path);
    }

    private static SavedPlacement? Read(string path)
    {
        try
        {
            var saved = JsonSerializer.Deserialize<SavedPlacement>(File.ReadAllText(path), JsonOptions);
            return saved is not null && (long)saved.Right - saved.Left is > 0 and <= int.MaxValue &&
                (long)saved.Bottom - saved.Top is > 0 and <= int.MaxValue ? saved : null;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException)
        {
            Debug.WriteLine(error);
            return null;
        }
    }

    private static void Restore(Window window, SavedPlacement saved)
    {
        var placement = new NativePlacement
        {
            Length = PlacementSize,
            Left = saved.Left,
            Top = saved.Top,
            Right = saved.Right,
            Bottom = saved.Bottom
        };

        // Keep the HWND hidden while Windows restores the bounds and corrects offscreen positions.
        if (!SetWindowPlacement(new WindowInteropHelper(window).Handle, in placement))
            window.SizeToContent = SizeToContent.Height;
        else if (saved.Maximized)
            window.WindowState = WindowState.Maximized;
    }

    private static void Save(Window window, string path)
    {
        nint handle = new WindowInteropHelper(window).Handle;
        var placement = new NativePlacement { Length = PlacementSize };
        if (handle == 0 || !GetWindowPlacement(handle, ref placement))
            return;

        var saved = new SavedPlacement(placement.Left, placement.Top, placement.Right, placement.Bottom,
            placement.ShowCommand == ShowMaximized);
        PreferenceFile.Write(path, JsonSerializer.Serialize(saved, JsonOptions));
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativePlacement
    {
        internal int Length, Flags, ShowCommand;
        internal int MinX, MinY, MaxX, MaxY;
        internal int Left, Top, Right, Bottom;
    }

    [DllImport("user32.dll")]
    private static extern bool GetWindowPlacement(nint window, ref NativePlacement placement);

    [DllImport("user32.dll")]
    private static extern bool SetWindowPlacement(nint window, in NativePlacement placement);
}
