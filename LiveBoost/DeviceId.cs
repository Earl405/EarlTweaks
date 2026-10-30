using System.Management;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace LiveBoost;

/// <summary>
/// Builds a hardware fingerprint from four stable parts that survive a Windows reinstall:
/// SMBIOS system UUID, motherboard serial, CPU id, and the main internal disk's serial.
/// Each part is SHA-256 hashed on this PC, so raw serial numbers never leave the machine; the server
/// then re-hashes them with its own secret. The server tolerates ONE changed part (disk/CPU upgrade).
/// Placeholder values that cheap OEM boards report ("To Be Filled By O.E.M.", all zeros...) are ignored.
/// Requires the NuGet package System.Management.
/// </summary>
public static class DeviceId
{
    static readonly string[] Junk =
    {
        "to be filled", "default string", "o.e.m", "not specified", "not applicable", "unknown",
        "system serial", "base board serial", "serial number", "n/a", "none"
    };

    static readonly object Gate = new();
    static Dictionary<string, string>? _components;

    public static string MachineName => Environment.MachineName;

    /// <summary>name -> hex hash, or "" when this PC doesn't report a usable value for that part.</summary>
    public static Dictionary<string, string> Components()
    {
        lock (Gate) return _components ??= Build();
    }

    /// <summary>Value for the X-Device header (base64 JSON, ASCII-safe).</summary>
    public static string Header()
    {
        var json = JsonSerializer.Serialize(new { components = Components(), name = MachineName });
        return Convert.ToBase64String(Encoding.UTF8.GetBytes(json));
    }

    static Dictionary<string, string> Build()
    {
        var raw = new Dictionary<string, string?>
        {
            ["uuid"] = Query("SELECT UUID FROM Win32_ComputerSystemProduct", "UUID"),
            ["board"] = Query("SELECT SerialNumber FROM Win32_BaseBoard", "SerialNumber"),
            ["cpu"] = Query("SELECT ProcessorId FROM Win32_Processor", "ProcessorId"),
            ["disk"] = InternalDiskSerial(),
        };
        return raw.ToDictionary(kv => kv.Key, kv => kv.Value == null ? "" : Hash(kv.Key, kv.Value));
    }

    static string Hash(string part, string value) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes($"EARLTWEAKS-DEVICE-v1|{part}|{value}"))).ToLowerInvariant();

    static string? Clean(string? value, bool lettersAndDigitsOnly = false)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        var v = value.Trim();
        if (lettersAndDigitsOnly) v = new string(v.Where(char.IsLetterOrDigit).ToArray());
        v = v.ToLowerInvariant();
        if (v.Length < 4) return null;
        if (Junk.Any(j => v.Contains(j))) return null;
        if (v.Where(char.IsLetterOrDigit).Distinct().Count() <= 1) return null; // 0000..., ffff...
        return v;
    }

    static string? Query(string wql, string property)
    {
        try
        {
            using var searcher = new ManagementObjectSearcher(wql);
            foreach (ManagementBaseObject item in searcher.Get())
            {
                using (item)
                {
                    var v = Clean(item[property]?.ToString());
                    if (v != null) return v;
                }
            }
        }
        catch { /* WMI unavailable: this part is simply skipped */ }
        return null;
    }

    static string? InternalDiskSerial()
    {
        try
        {
            using var searcher = new ManagementObjectSearcher("SELECT Index, SerialNumber, InterfaceType FROM Win32_DiskDrive");
            return searcher.Get().Cast<ManagementObject>()
                .Where(d => !string.Equals(d["InterfaceType"] as string, "USB", StringComparison.OrdinalIgnoreCase))
                .OrderBy(d => Convert.ToUInt32(d["Index"] ?? 0u))
                .Select(d => Clean(d["SerialNumber"] as string, lettersAndDigitsOnly: true))
                .FirstOrDefault(s => s != null);
        }
        catch { return null; }
    }
}
