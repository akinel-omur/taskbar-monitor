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

        BuildMenu();
        _tick.Tick += (_, _) => Poll();
        _zorder.Tick += (_, _) => KeepOnTop();
        Load += (_, _) => { Poll(); _tick.Start(); _zorder.Start(); };
        FormClosed += (_, _) => { _tick.Stop(); _zorder.Stop(); _sensors.Dispose(); };
        SystemEvents.UserPreferenceChanged += (_, _) => { if (IsHandleCreated) BeginInvoke(Redraw); };
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

    void Poll()
    {
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

        if (ShouldHide())
        {
            if (!_hidden) { ShowWindow(Handle, 0 /*SW_HIDE*/); _hidden = true; }
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

        var light = IsLightTaskbar();
        var textColor = light ? Color.FromArgb(255, 20, 20, 20) : Color.FromArgb(255, 245, 245, 245);
        var dimColor = light ? Color.FromArgb(255, 95, 95, 95) : Color.FromArgb(255, 170, 170, 170);

        var preset = FontSizes[Math.Clamp(_settings.FontSize, 0, FontSizes.Length - 1)];
        using var labelFont = new Font(_fontName, preset.Label * scale, FontStyle.Regular, GraphicsUnit.Pixel);
        using var valueFont = new Font(_fontName, preset.Value * scale, FontStyle.Bold, GraphicsUnit.Pixel);
        var fmt = (StringFormat)StringFormat.GenericTypographic.Clone();
        fmt.FormatFlags |= StringFormatFlags.NoWrap | StringFormatFlags.MeasureTrailingSpaces;

        using var measureBmp = new Bitmap(1, 1);
        using var mg = Graphics.FromImage(measureBmp);
        mg.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
        float Measure(string t, Font f) => mg.MeasureString(t, f, PointF.Empty, fmt).Width;

        float gap = 5 * scale, colGap = 14 * scale, padX = 8 * scale;

        // Satır etiketleri (CPU / GPU) yalnızca etiketsiz hücre varsa gerekir.
        var rowLabels = metrics.Any(m => m.Top is { Label: null } || m.Bottom is { Label: null });
        var rowLabelW = rowLabels ? Math.Max(Measure("CPU", labelFont), Measure("GPU", labelFont)) : 0;

        // Sütun genişlikleri: en geniş olası değere göre sabit, böylece pencere titremez.
        var colW = metrics.Select(m =>
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

        var contentW = (rowLabels ? rowLabelW + colGap : 0) + colW.Sum() + Math.Max(0, colW.Count - 1) * colGap;
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
                    var color = v >= cell.Hot ? Color.FromArgb(255, 235, 60, 60)
                              : v >= cell.Warn ? Color.FromArgb(255, 235, 150, 20)
                              : textColor;
                    if (cell.Label is { } label) DrawText(label, labelFont, dimBrush, x0, r, false, 0);
                    using var vb = new SolidBrush(color);
                    DrawText(v is { } d ? cell.Format(d) : "–", valueFont, vb, 0, r, true, x0 + colW[i]);
                }
                x0 += colW[i] + colGap;
            }
        }

        // Konum: görev çubuğunun sol ucu (ya da kullanıcının sürüklediği yer), çubukla aynı yükseklik
        var leftGap = _settings.LeftGap ?? (int)(12 * scale);
        leftGap = Math.Clamp(leftGap, 0, Math.Max(0, taskbar.Width - width));
        _bounds = new Rectangle(taskbar.Left + leftGap, taskbar.Top, width, height);

        if (_hidden) { ShowWindow(Handle, 4 /*SW_SHOWNOACTIVATE*/); _hidden = false; }
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
    bool ShouldHide()
    {
        var fg = GetForegroundWindow();
        if (fg == 0 || fg == Handle) return false;

        var cls = new System.Text.StringBuilder(64);
        GetClassName(fg, cls, cls.Capacity);
        if (cls.ToString() is "Progman" or "WorkerW" or "Shell_TrayWnd" or "Shell_SecondaryTrayWnd") return false;

        if (!GetWindowRect(fg, out var r)) return false;
        var screen = Screen.FromHandle(fg).Bounds;
        return r.Left <= screen.Left && r.Top <= screen.Top && r.Right >= screen.Right && r.Bottom >= screen.Bottom;
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

        var auto =new ToolStripMenuItem("Windows ile birlikte başlat") { Name = "auto", CheckOnClick = true };
        auto.CheckedChanged += (_, _) => SetAutostart(auto.Checked);
        _menu.Items.Add(auto);

        var reset = new ToolStripMenuItem("Konumu sıfırla (sol uç)");
        reset.Click += (_, _) => { _settings.LeftGap = null; _settings.Save(); Redraw(); };
        _menu.Items.Add(reset);

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
            else if (mi.Name == "size")
                for (var s = 0; s < mi.DropDownItems.Count; s++)
                    ((ToolStripMenuItem)mi.DropDownItems[s]).Checked = s == Math.Clamp(_settings.FontSize, 0, FontSizes.Length - 1);
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
