using System.Diagnostics;
using System.Runtime.InteropServices;
using LibreHardwareMonitor.Hardware;

namespace LiveBoost;

/// <summary>One reading. Null means "not available on this system" - never a made-up value.</summary>
public record Snapshot(double CpuPercent, double? CpuTempC, double? GpuPercent, double? GpuTempC,
                       double? VramUsedMb, double? VramTotalMb, double? EncoderPercent, double RamUsedMb, double RamTotalMb);

public sealed class MonitorService : IDisposable
{
    readonly Computer _pc = new() { IsCpuEnabled = true, IsGpuEnabled = true };
    readonly PerformanceCounter _cpu = new("Processor", "% Processor Time", "_Total");
    public string CpuName { get; private set; } = "Unknown CPU";
    public string GpuName { get; private set; } = "No supported GPU detected";

    public MonitorService()
    {
        try { _pc.Open(); } catch { /* sensors unavailable: values show as unavailable */ }
        foreach (var hw in _pc.Hardware)
        {
            if (hw.HardwareType == HardwareType.Cpu) CpuName = hw.Name;
            if (hw.HardwareType is HardwareType.GpuNvidia or HardwareType.GpuAmd) GpuName = hw.Name;
            else if (hw.HardwareType == HardwareType.GpuIntel && GpuName.StartsWith("No")) GpuName = hw.Name;
        }
        _cpu.NextValue();
    }

    public Snapshot Read()
    {
        double? cpuTemp = null, gpuLoad = null, gpuTemp = null, vramUsed = null, vramTotal = null, enc = null;
        foreach (var hw in _pc.Hardware)
        {
            try { hw.Update(); } catch { continue; }
            bool gpu = hw.HardwareType is HardwareType.GpuNvidia or HardwareType.GpuAmd or HardwareType.GpuIntel;
            foreach (var s in hw.Sensors)
            {
                if (s.Value is not float v) continue;
                if (hw.HardwareType == HardwareType.Cpu && s.SensorType == SensorType.Temperature)
                {
                    if (s.Name.Contains("Package") || s.Name.Contains("Tctl") || s.Name.Contains("Tdie") || cpuTemp == null)
                        cpuTemp = v;
                }
                else if (gpu && hw.Name == GpuName)
                {
                    if (s.SensorType == SensorType.Load && s.Name == "GPU Core") gpuLoad = v;
                    else if (s.SensorType == SensorType.Load && s.Name == "GPU Video Engine") enc = v;
                    else if (s.SensorType == SensorType.Temperature && s.Name == "GPU Core") gpuTemp = v;
                    else if (s.SensorType == SensorType.SmallData && s.Name == "GPU Memory Used") vramUsed = v;
                    else if (s.SensorType == SensorType.SmallData && s.Name == "GPU Memory Total") vramTotal = v;
                }
            }
        }
        var m = new MEMORYSTATUSEX { dwLength = (uint)Marshal.SizeOf<MEMORYSTATUSEX>() };
        GlobalMemoryStatusEx(ref m);
        double total = m.ullTotalPhys / 1048576.0, avail = m.ullAvailPhys / 1048576.0;
        return new Snapshot(_cpu.NextValue(), cpuTemp, gpuLoad, gpuTemp, vramUsed, vramTotal, enc, total - avail, total);
    }

    public void Dispose() { _pc.Close(); _cpu.Dispose(); }

    [StructLayout(LayoutKind.Sequential)]
    struct MEMORYSTATUSEX
    {
        public uint dwLength, dwMemoryLoad;
        public ulong ullTotalPhys, ullAvailPhys, ullTotalPageFile, ullAvailPageFile, ullTotalVirtual, ullAvailVirtual, ullAvailExtendedVirtual;
    }
    [DllImport("kernel32.dll", SetLastError = true)]
    static extern bool GlobalMemoryStatusEx(ref MEMORYSTATUSEX lpBuffer);
}
