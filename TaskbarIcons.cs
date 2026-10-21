using System.Windows.Automation;

namespace TaskbarMonitor;

/// <summary>
/// Windows 11 görev çubuğu simgeleri XAML ile çizilir ve pencere tutamacı yoktur; konumları yalnızca
/// UI Automation ile okunabilir. Ölçüm dış süreç çağrısı olduğundan arka plan iş parçacığında yapılmalıdır.
/// </summary>
static class TaskbarIcons
{
    /// <summary>Görev çubuğundaki en soldaki görünür düğmenin sol kenarı (ekran pikseli); okunamazsa null.</summary>
    public static int? LeftEdge(nint taskbar)
    {
        try
        {
            var root = AutomationElement.FromHandle(taskbar);
            var buttons = root.FindAll(TreeScope.Descendants,
                new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Button));

            double? left = null;
            foreach (AutomationElement e in buttons)
            {
                if (e.Current.IsOffscreen) continue;
                var r = e.Current.BoundingRectangle;
                if (r.IsEmpty || r.Width <= 0 || r.Height <= 0) continue;
                left = left is null ? r.Left : Math.Min(left.Value, r.Left);
            }
            return left is { } l ? (int)Math.Floor(l) : null;
        }
        catch { return null; }
    }
}
