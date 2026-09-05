using System.Diagnostics;

namespace Delve.Services;

/// Result-activation actions - deliberately both shell out to existing OS mechanisms rather
/// than reimplementing default-handler resolution or folder navigation.
public static class ShellOpenService
{
    /// Opens a file/folder path via its registered default handler - the same as double-
    /// clicking it in Explorer.
    public static void OpenDefault(string path)
    {
        Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
    }

    /// Opens Explorer with the item pre-selected, matching Explorer's own "Open file location".
    public static void RevealInExplorer(string path)
    {
        Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{path}\"") { UseShellExecute = true });
    }
}
