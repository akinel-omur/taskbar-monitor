# HANDOFF — TaskbarMonitor

Bu dosya, çalışmayı başka bir oturumda (Claude Code vb.) kaldığı yerden sürdürmek için yazıldı. Önce bunu, sonra `CLAUDE.md`'yi oku. Tarih: 2026-10-20.

## 1. Amaç

Kullanıcı (Türkçe konuşuyor) görev çubuğunda laptopun **ısısını, gücünü (watt), fan devrini** vb. görebileceği küçük bir gösterge istedi. Cihaz: ASUS ROG Strix G16 G615LW, Core Ultra 9 275HX, RTX 5080 Laptop, Windows 11 Pro (build 26300), Türkçe yerel ayar, ekran 2560x1600 @ %150 DPI (DPI=144).

## 2. Şu anki durum (çalışıyor)

.NET 10 WinForms uygulaması; saydam, her zaman üstte, per-pixel alpha bir pencere görev çubuğunun **sol ucuna** oturuyor. İki satır: üstte CPU, altta GPU. Sütunlar: sıcaklık, watt, fan devri, kullanım % (varsayılan). Menüden frekans, RAM/pil %, pil watt, orta fan da eklenebiliyor.

| Dosya | Görev |
|---|---|
| `Sensors.cs` | Tüm sensör okumaları: `AtkAcpi`, `Nvml`, `PdhQuery`, pil/RAM. `Snapshot` sınıfı. |
| `Metrics.cs` | Gösterilebilir sütun tanımları (`Metric`/`Cell`) ve `Settings` (JSON). |
| `Widget.cs` | Pencere, çizim (`UpdateLayeredWindow`), konum, sürükleme, sağ tık menüsü, otomatik başlatma, tam ekranda gizleme. |
| `Program.cs` | Giriş noktası; tek örnek (mutex); `--probe [dosya]` ile GUI'siz sensör yoklaması. |

### Doğrulananlar
- Tüm sensörler gerçek değer veriyor (yönetici yetkisi gerekmeden). Yapay yükte CPU 126 W / 98 °C okundu, yani watt ölçeği doğru.
- Görev çubuğunda simgelerle çakışmadan görünüyor; sağ tık menüsü açılıyor, işaretlemeler anında uygulanıyor.
- 12 sn boyunca 24/24 örnekte görünür kaldı. Bellek ~80-95 MB, CPU kullanımı ihmal edilebilir.

### Doğrulanmayanlar
- Sürüklemeyi kasıtlı test etmedim (kullanıcı kullanmış, ayarda `LeftGap` kaydı var).
- Tam ekran uygulamada gizlenme test edilmedi.
- Bir testte menü etkileşiminden hemen sonra widget tek bir ekran görüntüsünde yoktu; tekrar edilemedi (z-sırası ya da geçici gizleme şüphesi).
- Thermal Zone yedeği (ASUS olmayan makineler için) yazıldı ama denenmedi.

## 3. Önemli teknik bulgular (tekrar keşfetme)

1. **Türkçe Windows'ta sayaç adları yerelleşmiş** (`Enerji Ölçer`, `İşlemci Bilgileri`...). `Get-Counter` İngilizce adla çalışmaz. Kodda `PdhAddEnglishCounterW` kullanılıyor, bu yüzden her dilde çalışır.
2. **CPU paket gücü sürücüsüz okunabiliyor:** `\Energy Meter(RAPL_Package0_PKG)\Power`, birim **mW** (÷1000 → W). LibreHardwareMonitor/PawnIO/admin gerekmedi.
3. **ASUS ACPI** (`\\.\ATKACPI`, `CreateFile` GENERIC_READ|WRITE, normal kullanıcıyla açılıyor): `DeviceIoControl(0x0022240C)`, giriş tamponu = `DSTS (0x53545344)` + uzunluk + cihaz kimliği; çıkışın ilk 4 baytı − 65536 = değer.
   - Kimlikler: CPU fan `0x00110013`, GPU fan `0x00110014`, orta fan `0x00110031`, CPU sıcaklığı `0x00120094`. GPU sıcaklığı `0x00120097` bu modelde 0 döndü, o yüzden NVML kullanılıyor.
   - Fan değeri **×100 = devir/dk**. Geçerli aralık 1..200 (sıcaklık için 1..129).
4. **NVML** (`nvml.dll`, System32'de): sıcaklık, `GetPowerUsage` (mW), kullanım, grafik saat hızı. Bu laptopta fan %'si N/A.
5. **% Processor Utility** turbo'da %100'ü aşıyor (%123 görüldü) → kodda 100'e sınırlandı. CPU GHz = `Processor Frequency × % Processor Performance / 100`.
6. **Windows 11'de deskband yok.** Çözüm: `WS_EX_LAYERED|TOOLWINDOW|NOACTIVATE|TOPMOST` pencere + `UpdateLayeredWindow` (Format32bppPArgb). Arka plan alfa=1 (görünmez ama fare tıklamasını yakalar). Z-sırası için 400 ms'lik zamanlayıcı `HWND_TOPMOST` tekrar atıyor.
7. **Simgeler ortalı** olduğundan sağ taraf (tepsinin solu) simgelerin üstüne biniyordu → varsayılan konum **sol uç** yapıldı (`Settings.LeftGap`, varsayılan 12×ölçek px). `TrayNotifyWnd` Win11'de güvenilmez olduğu için tamamen kaldırıldı.
8. Yüksek DPI: csproj'da `ApplicationHighDpiMode=PerMonitorV2`; yazı tipleri piksel birimiyle `GraphicsUnit.Pixel`, boyut × (DPI/96).
9. Sütun genişliği, her sütun için sabit bir "en geniş örnek" metinle ölçülüyor (`Metric.Sample`), böylece değer değişince pencere titremiyor.

## 4. Geliştirme / doğrulama yöntemi

```powershell
dotnet build -c Release
Get-Process TaskbarMonitor -ErrorAction SilentlyContinue | Stop-Process -Force   # exe kilitli olmasın
Start-Process bin\Release\net10.0-windows\win-x64\TaskbarMonitor.exe

# GUI'siz sensör yoklaması (WinExe olduğu için -Wait şart, çıktı dosyaya yazılır):
Start-Process bin\Release\net10.0-windows\win-x64\TaskbarMonitor.exe -ArgumentList '--probe','C:\temp\probe.txt' -Wait
```

Görsel doğrulama: PowerShell'de `SetProcessDPIAware()` çağırıp `Graphics.CopyFromScreen` ile ekranın alt şeridini PNG'ye kaydet, sonra görüntüyü oku. Konum/ boyut için `FindWindow("Shell_TrayWnd")` + `GetWindowRect`.

**Uyarı:** Kullanıcı aynı anda bilgisayarı kullanıyor olabilir. Fare/klavye girdisi enjekte etmek (`mouse_event` vb.) onun oturumuna karışır; bir kez kullanıcının açtığı menüyle çakıştı. Girdi göndermeden, salt okuma ile doğrula; etkileşim testini kullanıcıdan iste.

## 5. Açık işler (öncelik sırasıyla)

1. ~~**Yazı bloğu görev çubuğundan "uzun" görünüyor**~~ — **ÇÖZÜLDÜ (2026-10-20).** Kullanıcı "sekme boy olarak uzadı, boyu uzatmayacak yazı boyutu olsun" dedi ve ekran görüntüsü gönderdi: iki satır görev çubuğunun (72 px) tamamını dolduruyor, simgeler ise ortadaki ~24 px'lik bantta duruyordu. Çözüm: satır aralığı artık `FontSizes[].Pitch` (dp) ile sabit ve blok dikey ortalanıyor; varsayılan "Orta" = etiket 10 / değer 11,5 / satır 14 dp (eskisi 11 / 13 / 24 dp). Sağ tık → **Yazı boyutu: Küçük/Orta/Büyük** (`Settings.FontSize`). Ölçüm: yazı bloğu ~33 px, simge bandı ~24 px, satırlar simge bandıyla hizalı. Kullanıcı hâlâ büyük bulursa varsayılanı "Küçük"e çek.
2. **Otomatik başlatma yolu kalıcı olsun.** Menüdeki "Windows ile birlikte başlat" `Environment.ProcessPath` yazıyor. Kullanıcı bunu, geçici oturum klasöründeki derleme çıktısı için açmış; o klasör silinirse başlatma boşa düşer. Çözüm: `dotnet publish -c Release -o publish` ile tek dosya üret, kalıcı bir konuma koy (ör. `%LOCALAPPDATA%\TaskbarMonitor`), seçeneği kapatıp yeniden aç. İstenirse uygulama açılışta kayıtlı yolun var olduğunu kontrol edip kendini düzeltebilir.
3. **Doğrulanmamışları sına** (bkz. §2): sürükleme, tam ekran gizleme, tek seferlik kaybolma (muhtemel neden: bir tıklama sonrası görev çubuğunun z-sırasında öne geçmesi; `KeepOnTop` aralığını/izlemeyi gözden geçir).
4. Olası özellikler (hepsi isteğe bağlı): yenileme aralığı ayarı, renk/tema ayarı, fare üstünde ayrıntı ipucu, SSD/çekirdek başına sıcaklık, çoklu monitör, GPU uykudayken NVML'in uyandırıp uyandırmadığının ölçümü (pil ömrü), yayın için GitHub Release / Actions.
5. Bellek ~90 MB: istenirse font/Bitmap tahsislerini döngüden çıkar (her `Redraw`'da yeni `Font`/`Bitmap` üretiliyor) ve ölç.

## 6. Yapma

- LibreHardwareMonitor/WinRing0/PawnIO gibi sürücü gerektiren kütüphane ekleme; gerek kalmadı.
- Varsayılan konumu tekrar sağ tarafa (tepsinin soluna) alma; simgelerin üstüne biniyor.
- Kullanıcının makinesine habersiz fare/klavye girdisi gönderme.
- Kullanıcıya İngilizce arayüz metni gösterme; menü ve mesajlar Türkçe.
