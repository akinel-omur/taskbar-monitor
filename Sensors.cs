using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace TaskbarMonitor;

/// <summary>Tek bir okuma turunun sonucu. Okunamayan değer null kalır.</summary>
public sealed class Snapshot
{
    public double? CpuTemp, CpuPower, CpuLoad, CpuGhz;
    public double? GpuTemp, GpuPower, GpuLoad, GpuMhz;
    public double? FanCpu, FanGpu, FanMid;
    public double? Ram, BatPct, BatPower;
}

/// <summary>
/// Sensör kaynakları (hiçbiri yönetici yetkisi veya ek sürücü istemez):
///  - ASUS ACPI (\\.\ATKACPI): CPU sıcaklığı ve fan devirleri
///  - Windows "Energy Meter" (RAPL) sayacı: CPU paket gücü
///  - NVML (NVIDIA sürücüsüyle gelir): GPU sıcaklık / güç / kullanım / frekans
///  - PDH: CPU kullanımı ve frekansı
/// </summary>
public sealed class Sensors : IDisposable
{
    readonly AtkAcpi _atk = new();
    readonly Nvml _nvml = new();
    readonly PdhQuery _pdh = new();
    readonly nint? _cPkgPower, _cUtility, _cFreq, _cPerf, _cThermalZone;

    public bool HasAsus => _atk.Ok;
    public bool HasNvidia => _nvml.Ok;

    public Sensors()
    {
        _cPkgPower = _pdh.Add(@"\Energy Meter(RAPL_Package0_PKG)\Power");
        _cUtility = _pdh.Add(@"\Processor Information(_Total)\% Processor Utility")
                    ?? _pdh.Add(@"\Processor Information(_Total)\% Processor Time");
        _cFreq = _pdh.Add(@"\Processor Information(_Total)\Processor Frequency");
        _cPerf = _pdh.Add(@"\Processor Information(_Total)\% Processor Performance");
        _cThermalZone = _atk.Ok ? null : _pdh.Add(@"\Thermal Zone Information(\_TZ.TZ00)\Temperature");
        _pdh.Collect(); // hız sayaçları ilk örnekten sonra anlamlı
    }

    public Snapshot Read()
    {
        var s = new Snapshot();
        _pdh.Collect();

        // CPU
        s.CpuPower = _pdh.Get(_cPkgPower) / 1000.0; // mW -> W
        s.CpuLoad = Math.Min(100, _pdh.Get(_cUtility) ?? double.NaN); // turbo'da "utility" %100'ü aşar
        if (double.IsNaN(s.CpuLoad.Value)) s.CpuLoad = null;
        var freq = _pdh.Get(_cFreq);
        var perf = _pdh.Get(_cPerf);
        if (freq is > 0 && perf is > 0) s.CpuGhz = freq.Value * perf.Value / 100.0 / 1000.0;
        else if (freq is > 0) s.CpuGhz = freq.Value / 1000.0;

        if (_atk.Ok)
        {
            s.CpuTemp = _atk.Temp(AtkAcpi.TempCpu);
            s.FanCpu = _atk.Fan(AtkAcpi.FanCpu);
            s.FanGpu = _atk.Fan(AtkAcpi.FanGpu);
            s.FanMid = _atk.Fan(AtkAcpi.FanMid);
        }
        else if (_pdh.Get(_cThermalZone) is { } k && k > 200)
        {
            s.CpuTemp = k - 273.15;
        }

        // GPU
        if (_nvml.Ok) _nvml.Read(s);

        // RAM
        var ms = new MemoryStatusEx { dwLength = (uint)Marshal.SizeOf<MemoryStatusEx>() };
        if (GlobalMemoryStatusEx(ref ms)) s.Ram = ms.dwMemoryLoad;

        // Pil
        ReadBattery(s);
        return s;
    }

    static void ReadBattery(Snapshot s)
    {
        var size = Marshal.SizeOf<BatteryState>();
        var buf = Marshal.AllocHGlobal(size);
        try
        {
            if (CallNtPowerInformation(5 /*SystemBatteryState*/, 0, 0, buf, (uint)size) != 0) return;
            var b = Marshal.PtrToStructure<BatteryState>(buf);
            if (b.BatteryPresent == 0) return;
            if (b.MaxCapacity > 0) s.BatPct = 100.0 * b.RemainingCapacity / b.MaxCapacity;
            if (b.Rate != unchecked((int)0x80000000)) s.BatPower = b.Rate / 1000.0; // + şarj, - deşarj
        }
        finally { Marshal.FreeHGlobal(buf); }
    }

    public void Dispose()
    {
        _atk.Dispose();
        _nvml.Dispose();
        _pdh.Dispose();
    }

    [StructLayout(LayoutKind.Sequential)]
    struct BatteryState
    {
        public byte AcOnLine, BatteryPresent, Charging, Discharging;
        public byte Spare1, Spare2, Spare3, Tag;
        public uint MaxCapacity, RemainingCapacity;
        public int Rate;
        public uint EstimatedTime, DefaultAlert1, DefaultAlert2;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct MemoryStatusEx
    {
        public uint dwLength, dwMemoryLoad;
        public ulong ullTotalPhys, ullAvailPhys, ullTotalPageFile, ullAvailPageFile, ullTotalVirtual, ullAvailVirtual, ullAvailExtendedVirtual;
    }

    [DllImport("kernel32.dll")] static extern bool GlobalMemoryStatusEx(ref MemoryStatusEx m);
    [DllImport("powrprof.dll")] static extern uint CallNtPowerInformation(int level, nint inBuf, uint inLen, nint outBuf, uint outLen);
}

/// <summary>ASUS ATK ACPI arayüzü (ROG/TUF dizüstülerde fan ve sıcaklık).</summary>
sealed class AtkAcpi : IDisposable
{
    public const uint FanCpu = 0x00110013, FanGpu = 0x00110014, FanMid = 0x00110031, TempCpu = 0x00120094;
    const uint IoctlCode = 0x0022240C, Dsts = 0x53545344;

    readonly SafeFileHandle? _h;
    public bool Ok => _h is { IsInvalid: false };

    public AtkAcpi()
    {
        var h = CreateFile(@"\\.\ATKACPI", 0xC0000000, 3, 0, 3, 0x80, 0);
        if (!h.IsInvalid) _h = h;
        else h.Dispose();
    }

    int Get(uint device)
    {
        if (_h is null) return int.MinValue;
        var args = BitConverter.GetBytes(device);
        var buf = new byte[8 + args.Length];
        BitConverter.GetBytes(Dsts).CopyTo(buf, 0);
        BitConverter.GetBytes((uint)args.Length).CopyTo(buf, 4);
        args.CopyTo(buf, 8);
        var output = new byte[16];
        if (!DeviceIoControl(_h, IoctlCode, buf, buf.Length, output, output.Length, out _, 0)) return int.MinValue;
        return BitConverter.ToInt32(output, 0) - 65536;
    }

    /// <summary>Devir/dk (cihaz değeri x100).</summary>
    public double? Fan(uint device) => Get(device) is > 0 and <= 200 and var v ? v * 100.0 : null;

    public double? Temp(uint device) => Get(device) is > 0 and < 130 and var v ? v : null;

    public void Dispose() => _h?.Dispose();

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    static extern SafeFileHandle CreateFile(string name, uint access, uint share, nint sa, uint disp, uint flags, nint template);

    [DllImport("kernel32.dll", SetLastError = true)]
    static extern bool DeviceIoControl(SafeFileHandle h, uint code, byte[] inBuf, int inLen, byte[] outBuf, int outLen, out uint returned, nint overlapped);
}

/// <summary>NVIDIA GPU okumaları (nvml.dll sürücüyle birlikte gelir).</summary>
sealed class Nvml : IDisposable
{
    nint _dev;
    public bool Ok { get; private set; }

    public Nvml()
    {
        try
        {
            if (nvmlInit_v2() != 0) return;
            if (nvmlDeviceGetHandleByIndex_v2(0, out _dev) != 0) { nvmlShutdown(); return; }
            Ok = true;
        }
        catch (DllNotFoundException) { }
        catch (EntryPointNotFoundException) { }
    }

    public void Read(Snapshot s)
    {
        if (nvmlDeviceGetTemperature(_dev, 0, out var t) == 0) s.GpuTemp = t;
        if (nvmlDeviceGetPowerUsage(_dev, out var mw) == 0) s.GpuPower = mw / 1000.0;
        if (nvmlDeviceGetUtilizationRates(_dev, out var u) == 0) s.GpuLoad = u.Gpu;
        if (nvmlDeviceGetClockInfo(_dev, 0, out var mhz) == 0) s.GpuMhz = mhz;
    }

    public void Dispose()
    {
        if (!Ok) return;
        Ok = false;
        try { nvmlShutdown(); } catch { }
    }

    [StructLayout(LayoutKind.Sequential)] struct Util { public uint Gpu, Memory; }

    [DllImport("nvml.dll")] static extern int nvmlInit_v2();
    [DllImport("nvml.dll")] static extern int nvmlShutdown();
    [DllImport("nvml.dll")] static extern int nvmlDeviceGetHandleByIndex_v2(uint index, out nint dev);
    [DllImport("nvml.dll")] static extern int nvmlDeviceGetTemperature(nint dev, int sensor, out uint temp);
    [DllImport("nvml.dll")] static extern int nvmlDeviceGetPowerUsage(nint dev, out uint milliwatts);
    [DllImport("nvml.dll")] static extern int nvmlDeviceGetUtilizationRates(nint dev, out Util util);
    [DllImport("nvml.dll")] static extern int nvmlDeviceGetClockInfo(nint dev, int type, out uint mhz);
}

/// <summary>PDH sarmalayıcısı. İngilizce sayaç adları kullanır; Türkçe Windows'ta da çalışır.</summary>
sealed class PdhQuery : IDisposable
{
    nint _q;

    public PdhQuery()
    {
        if (PdhOpenQuery(null, 0, out var q) == 0) _q = q;
    }

    public nint? Add(string path)
    {
        if (_q == 0) return null;
        return PdhAddEnglishCounterW(_q, path, 0, out var c) == 0 ? c : null;
    }

    public void Collect()
    {
        if (_q != 0) PdhCollectQueryData(_q);
    }

    public double? Get(nint? counter)
    {
        if (counter is null) return null;
        if (PdhGetFormattedCounterValue(counter.Value, 0x200 /*DOUBLE*/, out _, out var v) != 0) return null;
        return v.CStatus is 0 or 1 ? v.Value : null;
    }

    public void Dispose()
    {
        if (_q == 0) return;
        PdhCloseQuery(_q);
        _q = 0;
    }

    [StructLayout(LayoutKind.Explicit)]
    struct FmtValue
    {
        [FieldOffset(0)] public uint CStatus;
        [FieldOffset(8)] public double Value;
    }

    [DllImport("pdh.dll", CharSet = CharSet.Unicode)] static extern uint PdhOpenQuery(string? source, nint user, out nint query);
    [DllImport("pdh.dll", CharSet = CharSet.Unicode)] static extern uint PdhAddEnglishCounterW(nint query, string path, nint user, out nint counter);
    [DllImport("pdh.dll")] static extern uint PdhCollectQueryData(nint query);
    [DllImport("pdh.dll")] static extern uint PdhGetFormattedCounterValue(nint counter, uint format, out uint type, out FmtValue value);
    [DllImport("pdh.dll")] static extern uint PdhCloseQuery(nint query);
}
