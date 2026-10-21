using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;

namespace TaskbarMonitor;

/// <summary>GitHub'daki en son sürüm: exe ve SHA-256 dosyasının adresleri.</summary>
public sealed record UpdateInfo(Version Version, string ExeUrl, string ShaUrl);

/// <summary>
/// Uygulama içi güncelleme: GitHub Releases'teki en son sürümü denetler, exe'yi indirir,
/// SHA-256 ile doğrular, çalışan exe'nin adını değiştirip yenisini yerine koyar ve yeniden başlatır.
/// Sürüm sayfasında TaskbarMonitor.exe ve TaskbarMonitor.exe.sha256 dosyaları bulunmalıdır.
/// </summary>
static class Updater
{
    const string Repo = "akinel-omur/taskbar-monitor";
    const string ExeAsset = "TaskbarMonitor.exe";
    const string ShaAsset = "TaskbarMonitor.exe.sha256";
    const long MaxBytes = 100L * 1024 * 1024;

    static readonly string[] AllowedHosts = ["github.com", "objects.githubusercontent.com", "release-assets.githubusercontent.com"];

    static readonly HttpClient Http = CreateClient();

    static HttpClient CreateClient()
    {
        var c = new HttpClient { Timeout = TimeSpan.FromSeconds(60) };
        c.DefaultRequestHeaders.UserAgent.ParseAdd("TaskbarMonitor-Updater");
        return c;
    }

    public static Version Current => Normalize(Assembly.GetExecutingAssembly().GetName().Version ?? new Version(1, 0, 0));

    // 1.2.0 ile 1.2.0.0 aynı sürüm sayılsın (Version.Revision -1 ise küçük çıkar).
    static Version Normalize(Version v) => new(v.Major, v.Minor, Math.Max(v.Build, 0));

    /// <summary>Daha yeni bir sürüm varsa bilgisini, yoksa null döndürür. Erişilemezse istisna fırlatır.</summary>
    public static async Task<UpdateInfo?> CheckAsync()
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, $"https://api.github.com/repos/{Repo}/releases/latest");
        req.Headers.Accept.ParseAdd("application/vnd.github+json");
        using var res = await Http.SendAsync(req);
        if (res.StatusCode == System.Net.HttpStatusCode.NotFound)
            throw new InvalidOperationException("Sürüm bilgisi alınamadı (henüz yayınlanmış sürüm yok ya da depo herkese açık değil).");
        res.EnsureSuccessStatusCode();

        using var doc = JsonDocument.Parse(await res.Content.ReadAsStringAsync());
        var root = doc.RootElement;
        var tag = root.GetProperty("tag_name").GetString() ?? "";
        if (!Version.TryParse(tag.TrimStart('v', 'V'), out var parsed)) return null;
        var version = Normalize(parsed);
        if (version <= Current) return null;

        string? exe = null, sha = null;
        foreach (var a in root.GetProperty("assets").EnumerateArray())
        {
            var name = a.GetProperty("name").GetString();
            var url = a.GetProperty("browser_download_url").GetString();
            if (name == ExeAsset) exe = url;
            else if (name == ShaAsset) sha = url;
        }
        if (exe is null || sha is null)
            throw new InvalidOperationException($"v{version} sürümünde {ExeAsset} ve {ShaAsset} dosyaları yok.");
        return new UpdateInfo(version, exe, sha);
    }

    /// <summary>
    /// Yeni exe'yi indirir, doğrular, yerine koyar ve yeni süreci başlatır.
    /// Dönünce çağıran uygulamayı kapatmalıdır; yeni süreç bunun bitmesini bekler.
    /// </summary>
    public static async Task InstallAsync(UpdateInfo info)
    {
        var exe = Environment.ProcessPath ?? throw new InvalidOperationException("Çalışan exe yolu bulunamadı.");
        var tmp = exe + ".new";
        var old = exe + ".old";
        CheckUrl(info.ExeUrl);
        CheckUrl(info.ShaUrl);

        var expected = ParseSha(await Http.GetStringAsync(info.ShaUrl));
        try
        {
            using (var res = await Http.GetAsync(info.ExeUrl, HttpCompletionOption.ResponseHeadersRead))
            {
                res.EnsureSuccessStatusCode();
                if (res.Content.Headers.ContentLength > MaxBytes) throw new InvalidOperationException("İndirilen dosya beklenenden büyük.");
                await using var src = await res.Content.ReadAsStreamAsync();
                await using var dst = File.Create(tmp);
                await src.CopyToAsync(dst);
            }

            var bytes = await File.ReadAllBytesAsync(tmp);
            if (bytes.Length < 2 || bytes[0] != 'M' || bytes[1] != 'Z') throw new InvalidOperationException("İndirilen dosya çalıştırılabilir değil.");
            if (Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant() != expected)
                throw new InvalidOperationException("Sağlama toplamı uyuşmuyor; dosya bozuk ya da değiştirilmiş.");

            // Çalışan exe silinemez/üzerine yazılamaz ama adı değiştirilebilir.
            File.Move(exe, old, overwrite: true);
            try { File.Move(tmp, exe); }
            catch { File.Move(old, exe, overwrite: true); throw; }
        }
        catch
        {
            try { File.Delete(tmp); } catch { }
            throw;
        }

        System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(exe, $"--after-update {Environment.ProcessId}") { UseShellExecute = false });
    }

    /// <summary>Önceki güncellemeden kalan .old dosyasını siler.</summary>
    public static void CleanupOld()
    {
        try { if (Environment.ProcessPath is { } exe) File.Delete(exe + ".old"); } catch { }
    }

    static void CheckUrl(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var u) || u.Scheme != Uri.UriSchemeHttps || !AllowedHosts.Contains(u.Host))
            throw new InvalidOperationException("Güvenilmeyen indirme adresi: " + url);
    }

    static string ParseSha(string text)
    {
        var token = text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).FirstOrDefault()?.ToLowerInvariant() ?? "";
        if (token.Length != 64 || !token.All(Uri.IsHexDigit)) throw new InvalidOperationException("SHA-256 dosyası okunamadı.");
        return token;
    }
}
