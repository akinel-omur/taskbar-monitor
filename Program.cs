using System.Globalization;

namespace TaskbarMonitor;

static class Program
{
    [STAThread]
    static void Main(string[] args)
    {
        // Sensörleri GUI olmadan denemek için: TaskbarMonitor.exe --probe [çıktı-dosyası]
        if (args.Length > 0 && args[0] == "--probe")
        {
            Probe(args.Length > 1 ? args[1] : null);
            return;
        }

        DebugLog.Enabled = args.Contains("--debug");

        using var single = new Mutex(true, @"Local\TaskbarMonitor.Single", out var isFirst);
        if (!isFirst) return;

        ApplicationConfiguration.Initialize();
        Application.Run(new Widget());
    }

    static void Probe(string? outFile)
    {
        var lines = new List<string>();
        using var sensors = new Sensors();
        lines.Add($"ASUS ACPI: {sensors.HasAsus}   NVML: {sensors.HasNvidia}   Kültür: {CultureInfo.CurrentCulture.Name}");

        for (var i = 0; i < 4; i++)
        {
            Thread.Sleep(1000);
            var s = sensors.Read();
            lines.Add($"[{i}] " + string.Join("  ", Metrics.All.Select(m =>
                $"{m.Id}=" + string.Join("/", new[] { m.Top, m.Bottom }.OfType<Cell>()
                    .Select(c => c.Get(s) is { } v ? c.Format(v) : "yok")))));
        }

        if (outFile is null) foreach (var l in lines) Console.WriteLine(l);
        else File.WriteAllLines(outFile, lines);
    }
}
