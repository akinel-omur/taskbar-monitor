# TaskbarMonitor — Claude için notlar

Windows 11 görev çubuğunda sıcaklık/watt/fan devri gösteren küçük bir WinForms (.NET 10) aracı. **Önce `HANDOFF.md`'yi oku**: yapılanlar, teknik bulgular ve açık işler orada.

- Kullanıcı Türkçe konuşuyor; yanıtları ve arayüz metinlerini Türkçe yaz. Kod tanımlayıcıları İngilizce kalır.
- Derleme: `dotnet build -c Release` (SDK 10). Çalıştırmadan önce çalışan `TaskbarMonitor` sürecini durdur, yoksa exe kilitli kalır.
- GUI'siz sensör denemesi: `TaskbarMonitor.exe --probe <dosya>` (`Start-Process -Wait` ile çalıştır).
- Sürücü gerektiren sensör kütüphanesi ekleme (yönetici yetkisi/ek kurulum yok kuralı); sensör kaynakları `Sensors.cs` başında listeli.
- Kullanıcı bilgisayarı kullanırken fare/klavye girdisi enjekte etme; görsel doğrulamayı ekran görüntüsüyle, salt okuma yap.
- Claude masaüstü uygulaması MSIX paketli: Claude'un kabuğundan yazılan `AppData`/`HKCU` değerleri kullanıcının gerçek sistemine gitmez (ayrıntı: `HANDOFF.md` §3 madde 10). Kalıcı kurulum `Documents\TaskbarMonitor`; gerçek kayıt/ayar yazımını kullanıcının kısayoldan açtığı örnek yapar. Paket yalıtımını WMI vb. ile aşmaya çalışma.
- Windows Türkçe: performans sayaçlarını yalnızca İngilizce adla (`PdhAddEnglishCounterW`) ekle.
