namespace TaskbarMonitor;

/// <summary>`--debug` ile açılınca %APPDATA%\TaskbarMonitor\debug.log dosyasına gizle/göster geçişlerini yazar.</summary>
static class DebugLog
{
    static readonly string FilePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "TaskbarMonitor", "debug.log");

    public static bool Enabled { get; set; }

    public static void Write(string message)
    {
        if (!Enabled) return;
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            File.AppendAllText(FilePath, $"{DateTime.Now:HH:mm:ss.fff} {message}{Environment.NewLine}");
        }
        catch { }
    }
}
