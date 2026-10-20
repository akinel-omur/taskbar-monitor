using System.Text.Json;

namespace TaskbarMonitor;

/// <summary>Bir sütunun tek bir satırındaki değer. Label null ise satır etiketi (CPU / GPU) kullanılır.</summary>
public sealed record Cell(
    string? Label,
    Func<Snapshot, double?> Get,
    Func<double, string> Format,
    double Warn = double.MaxValue,
    double Hot = double.MaxValue);

/// <summary>Görev çubuğunda gösterilebilen iki satırlı bir sütun (üst satır CPU, alt satır GPU gibi).</summary>
public sealed record Metric(
    string Id,
    string MenuText,
    string Sample,            // sütun genişliğini sabit tutmak için en geniş olası değer
    Cell? Top,
    Cell? Bottom);

public static class Metrics
{
    static Cell Deg(Func<Snapshot, double?> get, double warn, double hot) =>
        new(null, get, v => $"{v:0}°C", warn, hot);

    public static readonly Metric[] All =
    [
        new("temp", "Sıcaklık (CPU / GPU)", "100°C",
            Deg(s => s.CpuTemp, 90, 100), Deg(s => s.GpuTemp, 80, 87)),
        new("power", "Güç, watt (CPU / GPU)", "888 W",
            new(null, s => s.CpuPower, v => $"{v:0} W"), new(null, s => s.GpuPower, v => $"{v:0} W")),
        new("fan", "Fan devri (CPU / GPU)", "8888 rpm",
            new(null, s => s.FanCpu, v => $"{v:0} rpm"), new(null, s => s.FanGpu, v => $"{v:0} rpm")),
        new("load", "Kullanım % (CPU / GPU)", "100%",
            new(null, s => s.CpuLoad, v => $"{v:0}%"), new(null, s => s.GpuLoad, v => $"{v:0}%")),
        new("freq", "Frekans (CPU / GPU)", "8,88 GHz",
            new(null, s => s.CpuGhz, v => $"{v:0.00} GHz"), new(null, s => s.GpuMhz, v => $"{v:0} MHz")),
        new("fan_mid", "Orta fan devri", "8888 rpm",
            new("Orta fan", s => s.FanMid, v => $"{v:0} rpm"), null),
        new("ram", "RAM ve pil yüzdesi", "100%",
            new("RAM", s => s.Ram, v => $"{v:0}%"), new("Pil", s => s.BatPct, v => $"{v:0}%")),
        new("bat_power", "Pil gücü (W)", "-888 W",
            new("Pil", s => s.BatPower, v => $"{v:+0;-0;0} W"), null),
    ];

    public static readonly string[] DefaultIds = ["temp", "power", "fan", "load"];
}

/// <summary>%APPDATA%\TaskbarMonitor\settings.json</summary>
public sealed class Settings
{
    static readonly string FilePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "TaskbarMonitor", "settings.json");

    public List<string> Items { get; set; } = [.. Metrics.DefaultIds];

    /// <summary>Pencerenin sol kenarının görev çubuğunun sol kenarına uzaklığı (piksel). null: varsayılan (sol uç).</summary>
    public int? LeftGap { get; set; }

    public static Settings Load()
    {
        try
        {
            if (File.Exists(FilePath))
                return JsonSerializer.Deserialize<Settings>(File.ReadAllText(FilePath)) ?? new Settings();
        }
        catch { }
        return new Settings();
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch { }
    }
}
