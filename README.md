# TaskbarMonitor

Windows 11 görev çubuğunda laptopun **sıcaklık, güç (watt), fan devri ve kullanım** değerlerini gösteren küçük, saydam bir gösterge. ASUS ROG dizüstüler için yazıldı (ROG Strix G16 / Core Ultra 9 275HX / RTX 5080 üzerinde denendi).

```
CPU   86°C   71 W   60%   RAM  69%
GPU   67°C  103 W   43%   Pil  79%
```

Yönetici yetkisi, ek sürücü veya üçüncü parti kütüphane gerektirmez.

## Çalıştırma

Gereksinim: .NET 10 SDK, Windows 11.

```powershell
dotnet build -c Release
bin\Release\net10.0-windows\win-x64\TaskbarMonitor.exe
```

Tek dosya yayınlamak için: `dotnet publish -c Release -o publish`

## Kullanım

- **Sürükle (sol tık):** konumu değiştirir, kaydedilir.
- **Sağ tık:** hangi değerlerin gösterileceği, yazı boyutu (küçük/orta/büyük), "Windows ile birlikte başlat", "Konumu sıfırla", "Çıkış".
- Tam ekran uygulama/oyun öndeyken gösterge gizlenir.
- Ayarlar: `%APPDATA%\TaskbarMonitor\settings.json`
- `TaskbarMonitor.exe --debug`: gizle/göster geçişlerini `%APPDATA%\TaskbarMonitor\debug.log` dosyasına yazar.

## Güncelleme

Sağ tık → **Güncellemeleri denetle** (ya da açılışta otomatik; menüden kapatılabilir). Yeni sürüm varsa GitHub Releases'ten `TaskbarMonitor.exe` indirilir, `TaskbarMonitor.exe.sha256` ile doğrulanır, yerine konur ve uygulama yeniden başlar. Yönetici yetkisi gerekmez; exe'nin bulunduğu klasör yazılabilir olmalı.

Yeni sürüm yayınlamak için `main`'de bir etiket at: `git tag v1.2.0 && git push origin v1.2.0`. CI uygulamayı o sürüm numarasıyla derler ve Release'e exe + sha256 dosyasını yükler.

## Değerler nereden okunuyor

| Değer | Kaynak |
|---|---|
| CPU watt | Windows "Energy Meter" (RAPL) performans sayacı |
| CPU sıcaklığı, fan devirleri | ASUS ATKACPI (`\\.\ATKACPI`) |
| GPU sıcaklık/watt/kullanım/frekans | NVIDIA NVML (`nvml.dll`) |
| CPU kullanımı/frekansı | PDH (Processor Information) |
| RAM, pil | `GlobalMemoryStatusEx`, `CallNtPowerInformation` |

Devam eden geliştirme için bkz. [HANDOFF.md](HANDOFF.md).
