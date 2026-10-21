using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Drawing.Text;
using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace TaskbarMonitor;

/// <summary>
/// Görev çubuğunun üzerine oturan, arka planı saydam, tıklanabilir küçük gösterge.
/// Windows 11 eski "deskband" yöntemini desteklemediği için çubuğun boş alanına
/// her zaman üstte duran katmanlı (per-pixel alpha) bir pencere yerleştirilir.
/// Simgeler ortalı olduğundan varsayılan konum çubuğun sol ucudur; sürükleyerek taşınır.
/// </summary>
sealed class Widget : Form
{
    const string AutostartKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    const string AutostartName = "TaskbarMonitor";

    // Piksel (DPI=96'ya göre) cinsinden: etiket yazısı, değer yazısı, satır aralığı. İki satır = 2 x Pitch.
    static readonly (string Name, float Label, float Value, float Pitch)[] FontSizes =
    [
        ("Küçük", 9f, 10.5f, 12.5f),
        ("Orta", 10f, 11.5f, 14f),
        ("Büyük", 11f, 13f, 16f),
    ];

    static readonly (string Name, int Ms)[] RefreshOptions =
    [
        ("0,5 saniye", 500),
        ("1 saniye", 1000),
        ("2 saniye", 2000),
        ("5 saniye", 5000),
    ];

    // Sıcaklık sarı/kırmızı eşiklerine eklenen °C kayması (Metrics.cs'teki eşikler varsayılandır).
    static readonly (string Name, double Offset)[] TempOptions =
    [
        ("Erken uyar (−10 °C)", -10),
        ("Varsayılan", 0),
        ("Geç uyar (+5 °C)", 5),
        ("Renklendirme kapalı", double.PositiveInfinity),
    ];

    readonly Settings _settings = Settings.Load();
    readonly Sensors _sensors = new();
    readonly System.Windows.Forms.Timer _tick = new() { Interval = 1000 };
    readonly System.Windows.Forms.Timer _zorder = new() { Interval = 400 };
    readonly ContextMenuStrip _menu = new();

    Snapshot _snap = new();
    bool _busy;
    bool _hidden;
    bool _dragging;
    Point _dragStartMouse;
    int _dragStartLeftGap;
    Rectangle _bounds;          // son çizilen pencerenin ekran dikdörtgeni
    Rectangle _taskbar;
    string _fontName = "Segoe UI";
    Font? _labelFont, _valueFont;
    (int Size, uint Dpi) _fontKey;
    Bitmap? _measureBmp;
    int _iconsLeft;             // simge şeridinin sol kenarı (ekran px); 0: bilinmiyor
    bool _iconBusy;
    int _iconTick;
    int _shownCount = -1;
    UpdateInfo? _update;        // bulunmuş yeni sürüm
    bool _updating;
    Graphics? _measureG;
    readonly UserPreferenceChangedEventHandler _prefChanged;

    public Widget()
    {
        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.Manual;
        TopMost = true;
        Size = new Size(1, 1);
        Text = "Görev Çubuğu İzleyici";

        if (new InstalledFontCollection().Families.Any(f => f.Name == "Segoe UI Variable Text"))
            _fontName = "Segoe UI Variable Text";

        _tick.Interval = RefreshOptions[Math.Clamp(_settings.RefreshIndex, 0, RefreshOptions.Length - 1)].Ms;
        BuildMenu();
        _tick.Tick += (_, _) => Poll();
        _zorder.Tick += (_, _) => KeepOnTop();
        Load += (_, _) => { Poll(); _tick.Start(); _zorder.Start(); if (_settings.AutoUpdate) _ = CheckUpdatesAsync(manual: false); };
        _prefChanged = (_, _) => { if (IsHandleCreated) BeginInvoke(Redraw); };
        SystemEvents.UserPreferenceChanged += _prefChanged;
        FormClosed += (_, _) =>
        {
            _tick.Stop(); _zorder.Stop();
            SystemEvents.UserPreferenceChanged -= _prefChanged;
            _sensors.Dispose();
            _labelFont?.Dispose(); _valueFont?.Dispose(); _measureG?.Dispose(); _measureBmp?.Dispose();
        };
    }

    // ---------------------------------------------------------------- pencere stili

    protected override CreateParams CreateParams
    {
        get
        {
            var cp = base.CreateParams;
            cp.ExStyle |= 0x00080000 /*LAYERED*/ | 0x00000080 /*TOOLWINDOW*/ | 0x08000000 /*NOACTIVATE*/ | 0x00000008 /*TOPMOST*/;
            return cp;
        }
    }

    protected override bool ShowWithoutActivation => true;

    protected override void OnPaint(PaintEventArgs e) { /* UpdateLayeredWindow ile çiziliyor */ }
    protected override void OnPaintBackground(PaintEventArgs e) { }

    // ---------------------------------------------------------------- okuma döngüsü

    /// <summary>UI Automation yavaş olabilir: arka planda, birkaç turda bir ölç.</summary>
    void RefreshIconsLeft()
    {
        if (_iconBusy || _iconTick++ % 3 != 0) return;
        _iconBusy = true;
        Task.Run(() =>
        {
            try
            {
                var tb = FindWindow("Shell_TrayWnd", null);
                if (tb != 0 && TaskbarIcons.LeftEdge(tb) is { } left && left != _iconsLeft)
                {
                    _iconsLeft = left;
                    DebugLog.Write($"Simge şeridi sol kenarı: {left}");
                    if (IsHandleCreated && !IsDisposed) BeginInvoke(Redraw);
                }
            }
            catch { }
            finally { _iconBusy = false; }
        });
    }

    void Poll()
    {
        RefreshIconsLeft();
        if (_busy) return;
        _busy = true;
        Task.Run(() =>
        {
            try { return _sensors.Read(); }
            catch { return null; }
        }).ContinueWith(t =>
        {
            if (IsDisposed || !IsHandleCreated) return;
            BeginInvoke(() =>
            {
                _busy = false;
                if (t.Result is { } s) _snap = s;
                Redraw();
            });
        });
    }

    void KeepOnTop()
    {
        if (_hidden || !IsHandleCreated) return;
        SetWindowPos(Handle, new nint(-1), 0, 0, 0, 0, 0x0002 | 0x0001 | 0x0010 | 0x0200); // NOMOVE|NOSIZE|NOACTIVATE|NOOWNERZORDER
    }

    // ---------------------------------------------------------------- çizim

    void Redraw()
    {
        if (!IsHandleCreated || IsDisposed) return;

        if (ShouldHide(out var why))
        {
            if (!_hidden) { ShowWindow(Handle, 0 /*SW_HIDE*/); _hidden = true; DebugLog.Write($"GİZLE: {why}"); }
            return;
        }

        if (FindTaskbar() is not { } taskbar) return;
        _taskbar = taskbar;

        var dpi = GetDpiForWindow(Handle);
        var scale = (dpi == 0 ? 96 : dpi) / 96f;

        var metrics = _settings.Items
            .Select(id => Metrics.All.FirstOrDefault(m => m.Id == id))
            .OfType<Metric>()
            .ToList();

        var tempOffset = TempOptions[Math.Clamp(_settings.TempThreshold, 0, TempOptions.Length - 1)].Offset;
        var light = IsLightTaskbar();
        var textColor = light ? Color.FromArgb(255, 20, 20, 20) : Color.FromArgb(255, 245, 245, 245);
        var dimColor = light ? Color.FromArgb(255, 95, 95, 95) : Color.FromArgb(255, 170, 170, 170);

        var preset = FontSizes[Math.Clamp(_settings.FontSize, 0, FontSizes.Length - 1)];
        var fontKey = (Math.Clamp(_settings.FontSize, 0, FontSizes.Length - 1), dpi);
        if (_labelFont is null || _valueFont is null || _fontKey != fontKey)
        {
            _labelFont?.Dispose(); _valueFont?.Dispose();
            _labelFont = new Font(_fontName, preset.Label * scale, FontStyle.Regular, GraphicsUnit.Pixel);
            _valueFont = new Font(_fontName, preset.Value * scale, FontStyle.Bold, GraphicsUnit.Pixel);
            _fontKey = fontKey;
        }
        var labelFont = _labelFont!;
        var valueFont = _valueFont!;
        var fmt = (StringFormat)StringFormat.GenericTypographic.Clone();
        fmt.FormatFlags |= StringFormatFlags.NoWrap | StringFormatFlags.MeasureTrailingSpaces;

        _measureBmp ??= new Bitmap(1, 1);
        var mg = _measureG ??= Graphics.FromImage(_measureBmp);
        mg.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
        float Measure(string t, Font f) => mg.MeasureString(t, f, PointF.Empty, fmt).Width;

        float gap = 5 * scale, colGap = 9 * scale, padX = 4 * scale;

        // Sütun genişlikleri: en geniş olası değere göre sabit, böylece pencere titremez.
        var colWAll = metrics.Select(m =>
        {
            float w = 0;
            foreach (var cell in new[] { m.Top, m.Bottom })
            {
                if (cell is null) continue;
                var lw = cell.Label is null ? 0 : Measure(cell.Label, labelFont) + gap;
                w = Math.Max(w, lw + Measure(m.Sample, valueFont));
            }
            return w;
        }).ToList();

        // Satır etiketleri (CPU / GPU) yalnızca etiketsiz hücre varsa gerekir.
        var rowLabelW0 = Math.Max(Measure("CPU", labelFont), Measure("GPU", labelFont));
        bool NeedsRowLabels(int n) => metrics.Take(n).Any(m => m.Top is { Label: null } || m.Bottom is { Label: null });
        float ContentW(int n) => (NeedsRowLabels(n) ? rowLabelW0 + colGap : 0) + colWAll.Take(n).Sum() + Math.Max(0, n - 1) * colGap;

        // Sığmayan sütunlar sağdan başlayarak çizilmez (ayar dosyası değişmez); en az bir sütun kalır.
        var wantedGap = Math.Max(0, _settings.LeftGap ?? (int)(4 * scale));
        var count = metrics.Count;
        if (_iconsLeft > taskbar.Left + wantedGap)
        {
            var avail = _iconsLeft - (taskbar.Left + wantedGap) - 6 * scale - padX * 2;
            while (count > 1 && ContentW(count) > avail) count--;
        }
        if (count != _shownCount) { DebugLog.Write($"Görünen sütun: {count}/{metrics.Count} (şerit sol={_iconsLeft})"); _shownCount = count; }
        metrics = metrics.Take(count).ToList();
        var colW = colWAll.Take(count).ToList();
        var rowLabels = NeedsRowLabels(count);
        var rowLabelW = rowLabels ? rowLabelW0 : 0;
        var contentW = ContentW(count);
        var width = Math.Max((int)Math.Ceiling(24 * scale), (int)Math.Ceiling(contentW + padX * 2));
        var height = Math.Max(1, taskbar.Height);

        using var bmp = new Bitmap(width, height, PixelFormat.Format32bppPArgb);
        using (var g = Graphics.FromImage(bmp))
        {
            g.Clear(Color.FromArgb(1, 0, 0, 0)); // neredeyse görünmez ama fare tıklamasını yakalar
            g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;

            // İki satır görev çubuğunun tamamını değil, ortadaki simge bandını doldursun.
            var rowH = preset.Pitch * scale;
            var top = (height - 2 * rowH) / 2f;
            using var dimBrush = new SolidBrush(dimColor);

            void DrawText(string text, Font font, Brush brush, float x, int row, bool alignRight, float right)
            {
                var size = g.MeasureString(text, font, PointF.Empty, fmt);
                g.DrawString(text, font, brush, alignRight ? right - size.Width : x, top + row * rowH + rowH / 2f - size.Height / 2f, fmt);
            }

            var x0 = padX;
            if (rowLabels)
            {
                DrawText("CPU", labelFont, dimBrush, x0, 0, false, 0);
                DrawText("GPU", labelFont, dimBrush, x0, 1, false, 0);
                x0 += rowLabelW + colGap;
            }

            for (var i = 0; i < metrics.Count; i++)
            {
                var m = metrics[i];
                for (var r = 0; r < 2; r++)
                {
                    if ((r == 0 ? m.Top : m.Bottom) is not { } cell) continue;
                    var v = cell.Get(_snap);
                    var color = v >= cell.Hot + tempOffset ? Color.FromArgb(255, 235, 60, 60)
                              : v >= cell.Warn + tempOffset ? Color.FromArgb(255, 235, 150, 20)
                              : textColor;
                    if (cell.Label is { } label) DrawText(label, labelFont, dimBrush, x0, r, false, 0);
                    using var vb = new SolidBrush(color);
                    DrawText(v is { } d ? cell.Format(d) : "–", valueFont, vb, 0, r, true, x0 + colW[i]);
                }
                x0 += colW[i] + colGap;
            }
        }

        // Konum: görev çubuğunun sol ucu (ya da kullanıcının sürüklediği yer), çubukla aynı yükseklik
        var leftGap = _settings.LeftGap ?? (int)(4 * scale);
        leftGap = Math.Clamp(leftGap, 0, Math.Max(0, taskbar.Width - width));
        _bounds = new Rectangle(taskbar.Left + leftGap, taskbar.Top, width, height);

        if (_hidden) { ShowWindow(Handle, 4 /*SW_SHOWNOACTIVATE*/); _hidden = false; DebugLog.Write("GÖSTER"); }
        Present(bmp, _bounds);
    }

    void Present(Bitmap bmp, Rectangle bounds)
    {
        var screenDc = GetDC(0);
        var memDc = CreateCompatibleDC(screenDc);
        var hBmp = bmp.GetHbitmap(Color.FromArgb(0));
        var old = SelectObject(memDc, hBmp);
        try
        {
            var dst = new POINT { X = bounds.Left, Y = bounds.Top };
            var size = new SIZE { cx = bounds.Width, cy = bounds.Height };
            var src = new POINT();
            var blend = new BLENDFUNCTION { BlendOp = 0, SourceConstantAlpha = 255, AlphaFormat = 1 /*AC_SRC_ALPHA*/ };
            UpdateLayeredWindow(Handle, screenDc, ref dst, ref size, memDc, ref src, 0, ref blend, 2 /*ULW_ALPHA*/);
        }
        finally
        {
            SelectObject(memDc, old);
            DeleteObject(hBmp);
            DeleteDC(memDc);
            ReleaseDC(0, screenDc);
        }
    }

    // ---------------------------------------------------------------- görev çubuğu bilgisi

    static Rectangle? FindTaskbar()
    {
        var tb = FindWindow("Shell_TrayWnd", null);
        if (tb == 0 || !GetWindowRect(tb, out var r)) return null;
        return Rectangle.FromLTRB(r.Left, r.Top, r.Right, r.Bottom);
    }

    static bool IsLightTaskbar()
    {
        try
        {
            using var k = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            return k?.GetValue("SystemUsesLightTheme") is int v && v == 1;
        }
        catch { return false; }
    }

    /// <summary>Tam ekran bir uygulama/oyun öndeyken (görev çubuğu görünmezken) gizle.</summary>
    bool ShouldHide(out string why)
    {
        why = "";
        var fg = GetForegroundWindow();
        if (fg == 0 || fg == Handle) return false;

        var cls = new System.Text.StringBuilder(64);
        GetClassName(fg, cls, cls.Capacity);
        if (cls.ToString() is "Progman" or "WorkerW" or "Shell_TrayWnd" or "Shell_SecondaryTrayWnd") return false;

        if (!GetWindowRect(fg, out var r)) return false;
        var screen = Screen.FromHandle(fg).Bounds;
        var full = r.Left <= screen.Left && r.Top <= screen.Top && r.Right >= screen.Right && r.Bottom >= screen.Bottom;
        if (full) why = $"sınıf={cls} pencere={r.Left},{r.Top},{r.Right},{r.Bottom} ekran={screen}";
        return full;
    }

    // ---------------------------------------------------------------- fare: sürükle + sağ tık menüsü

    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);
        if (e.Button != MouseButtons.Left) return;
        _dragging = true;
        _dragStartMouse = Cursor.Position;
        _dragStartLeftGap = _bounds.Left - _taskbar.Left;
        Capture = true;
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        if (!_dragging) return;
        _settings.LeftGap = _dragStartLeftGap + (Cursor.Position.X - _dragStartMouse.X);
        Redraw();
    }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        base.OnMouseUp(e);
        if (e.Button == MouseButtons.Left && _dragging)
        {
            _dragging = false;
            Capture = false;
            _settings.Save();
        }
        else if (e.Button == MouseButtons.Right)
        {
            SyncMenu();
            _menu.Show(Cursor.Position);
        }
    }

    // ---------------------------------------------------------------- menü

    void BuildMenu()
    {
        foreach (var m in Metrics.All)
        {
            var item = new ToolStripMenuItem(m.MenuText) { Tag = m, CheckOnClick = true };
            item.CheckedChanged += (_, _) =>
            {
                if (!item.Checked) _settings.Items.Remove(m.Id);
                else if (!_settings.Items.Contains(m.Id)) _settings.Items.Add(m.Id);
                // Sıra her zaman menüdeki sıra olsun
                _settings.Items = [.. Metrics.All.Select(x => x.Id).Where(_settings.Items.Contains)];
                _settings.Save();
                Redraw();
            };
            _menu.Items.Add(item);
        }

        _menu.Items.Add(new ToolStripSeparator());

        var sizeMenu = new ToolStripMenuItem("Yazı boyutu") { Name = "size" };
        for (var i = 0; i < FontSizes.Length; i++)
        {
            var index = i;
            var item = new ToolStripMenuItem(FontSizes[i].Name);
            item.Click += (_, _) => { _settings.FontSize = index; _settings.Save(); Redraw(); };
            sizeMenu.DropDownItems.Add(item);
        }
        _menu.Items.Add(sizeMenu);

        var refreshMenu = new ToolStripMenuItem("Yenileme aralığı") { Name = "refresh" };
        for (var i = 0; i < RefreshOptions.Length; i++)
        {
            var index = i;
            var item = new ToolStripMenuItem(RefreshOptions[i].Name);
            item.Click += (_, _) =>
            {
                _settings.RefreshIndex = index;
                _tick.Interval = RefreshOptions[index].Ms;
                _settings.Save();
            };
            refreshMenu.DropDownItems.Add(item);
        }
        _menu.Items.Add(refreshMenu);

        var tempMenu = new ToolStripMenuItem("Sıcaklık uyarı eşiği") { Name = "temp" };
        for (var i = 0; i < TempOptions.Length; i++)
        {
            var index = i;
            var item = new ToolStripMenuItem(TempOptions[i].Name);
            item.Click += (_, _) => { _settings.TempThreshold = index; _settings.Save(); Redraw(); };
            tempMenu.DropDownItems.Add(item);
        }
        _menu.Items.Add(tempMenu);

        var auto =new ToolStripMenuItem("Windows ile birlikte başlat") { Name = "auto", CheckOnClick = true };
        auto.CheckedChanged += (_, _) => SetAutostart(auto.Checked);
        _menu.Items.Add(auto);

        var reset = new ToolStripMenuItem("Konumu sıfırla (sol uç)");
        reset.Click += (_, _) => { _settings.LeftGap = null; _settings.Save(); Redraw(); };
        _menu.Items.Add(reset);

        var autoUpdate = new ToolStripMenuItem("Açılışta güncellemeyi denetle") { Name = "autoupdate", CheckOnClick = true };
        autoUpdate.CheckedChanged += (_, _) =>
        {
            if (_settings.AutoUpdate == autoUpdate.Checked) return;
            _settings.AutoUpdate = autoUpdate.Checked;
            _settings.Save();
        };
        _menu.Items.Add(autoUpdate);

        var update = new ToolStripMenuItem { Name = "update" };
        update.Click += async (_, _) => await OnUpdateClickAsync();
        _menu.Items.Add(update);

        var exit = new ToolStripMenuItem("Çıkış");
        exit.Click += (_, _) => Close();
        _menu.Items.Add(exit);

        // Gösterge işaretlerken menü kapanmasın
        _menu.Closing += (_, e) =>
        {
            if (e.CloseReason == ToolStripDropDownCloseReason.ItemClicked &&
                _menu.GetItemAt(_menu.PointToClient(Cursor.Position)) is ToolStripMenuItem { Tag: Metric })
                e.Cancel = true;
        };
    }

    void SyncMenu()
    {
        foreach (ToolStripItem i in _menu.Items)
        {
            if (i is not ToolStripMenuItem mi) continue;
            if (mi.Tag is Metric m) mi.Checked = _settings.Items.Contains(m.Id);
            else if (mi.Name == "auto") mi.Checked = IsAutostart();
            else if (mi.Name == "autoupdate") mi.Checked = _settings.AutoUpdate;
            else if (mi.Name == "update")
                mi.Text = _update is { } u ? $"Yeni sürüm var: v{u.Version} — güncelle" : $"Güncellemeleri denetle (v{Updater.Current})";
            else if (mi.Name is "size" or "refresh" or "temp")
            {
                var current = mi.Name switch
                {
                    "size" => Math.Clamp(_settings.FontSize, 0, FontSizes.Length - 1),
                    "refresh" => Math.Clamp(_settings.RefreshIndex, 0, RefreshOptions.Length - 1),
                    _ => Math.Clamp(_settings.TempThreshold, 0, TempOptions.Length - 1),
                };
                for (var s = 0; s < mi.DropDownItems.Count; s++)
                    ((ToolStripMenuItem)mi.DropDownItems[s]).Checked = s == current;
            }
        }
    }

    // ---------------------------------------------------------------- güncelleme

    async Task CheckUpdatesAsync(bool manual)
    {
        try
        {
            if (!manual) await Task.Delay(5000); // açılışı yavaşlatma
            _update = await Updater.CheckAsync();
            DebugLog.Write(_update is { } u ? $"Yeni sürüm: v{u.Version}" : "Güncel");
            if (manual && _update is null)
                MessageBox.Show($"Güncel sürümü kullanıyorsun (v{Updater.Current}).", "Güncelleme", MessageBoxButtons.OK, MessageBoxIcon.Information);
        }
        catch (Exception ex)
        {
            DebugLog.Write("Güncelleme denetimi başarısız: " + ex.Message);
            if (manual) MessageBox.Show("Güncelleme denetlenemedi:\n" + ex.Message, "Güncelleme", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
    }

    async Task OnUpdateClickAsync()
    {
        if (_updating) return;
        if (_update is null) { await CheckUpdatesAsync(manual: true); if (_update is null) return; }

        var ask = MessageBox.Show(
            $"v{_update.Version} indirilip yüklenecek ve uygulama yeniden başlayacak.\n(Şu anki sürüm: v{Updater.Current})\n\nDevam edilsin mi?",
            "Güncelle", MessageBoxButtons.YesNo, MessageBoxIcon.Question);
        if (ask != DialogResult.Yes) return;

        _updating = true;
        try
        {
            await Updater.InstallAsync(_update);
            Close(); // yeni süreç bunun kapanmasını bekler
        }
        catch (Exception ex)
        {
            _updating = false;
            DebugLog.Write("Güncelleme başarısız: " + ex.Message);
            MessageBox.Show("Güncelleme yüklenemedi:\n" + ex.Message, "Güncelleme", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    static bool IsAutostart()
    {
        using var k = Registry.CurrentUser.OpenSubKey(AutostartKey);
        return k?.GetValue(AutostartName) is string;
    }

    static void SetAutostart(bool on)
    {
        using var k = Registry.CurrentUser.OpenSubKey(AutostartKey, writable: true);
        if (k is null) return;
        if (on && Environment.ProcessPath is { } exe) k.SetValue(AutostartName, $"\"{exe}\"");
        else k.DeleteValue(AutostartName, throwOnMissingValue: false);
    }

    // ---------------------------------------------------------------- Win32

    [StructLayout(LayoutKind.Sequential)] struct POINT { public int X, Y; }
    [StructLayout(LayoutKind.Sequential)] struct SIZE { public int cx, cy; }
    [StructLayout(LayoutKind.Sequential)] struct RECT { public int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential)]
    struct BLENDFUNCTION { public byte BlendOp, BlendFlags, SourceConstantAlpha, AlphaFormat; }

    [DllImport("user32.dll", SetLastError = true)]
    static extern bool UpdateLayeredWindow(nint hwnd, nint hdcDst, ref POINT pptDst, ref SIZE psize, nint hdcSrc, ref POINT pptSrc, int crKey, ref BLENDFUNCTION pblend, int flags);
    [DllImport("user32.dll")] static extern bool SetWindowPos(nint hwnd, nint after, int x, int y, int cx, int cy, uint flags);
    [DllImport("user32.dll")] static extern bool ShowWindow(nint hwnd, int cmd);
    [DllImport("user32.dll")] static extern nint GetDC(nint hwnd);
    [DllImport("user32.dll")] static extern int ReleaseDC(nint hwnd, nint dc);
    [DllImport("user32.dll")] static extern uint GetDpiForWindow(nint hwnd);
    [DllImport("user32.dll")] static extern nint GetForegroundWindow();
    [DllImport("user32.dll")] static extern bool GetWindowRect(nint hwnd, out RECT r);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int GetClassName(nint hwnd, System.Text.StringBuilder sb, int max);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern nint FindWindow(string? cls, string? title);
    [DllImport("gdi32.dll")] static extern nint CreateCompatibleDC(nint dc);
    [DllImport("gdi32.dll")] static extern nint SelectObject(nint dc, nint obj);
    [DllImport("gdi32.dll")] static extern bool DeleteObject(nint obj);
    [DllImport("gdi32.dll")] static extern bool DeleteDC(nint dc);
}
