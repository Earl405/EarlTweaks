using System.IO;
using System.Text.Json;
namespace LiveBoost;

public static class Storage
{
    public static readonly string Dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "LiveBoost");
    static readonly JsonSerializerOptions J = new() { WriteIndented = true };
    static Storage()
    {
        Directory.CreateDirectory(Dir);
        try
        {
            var p = Path.Combine(Dir, "log.txt");
            if (File.Exists(p) && new FileInfo(p).Length > 1_000_000)
                File.WriteAllLines(p, File.ReadLines(p).TakeLast(500).ToList());
        }
        catch { }
    }

    public static T Load<T>(string file, T fallback)
    {
        try
        {
            var p = Path.Combine(Dir, file);
            if (!File.Exists(p)) return fallback;
            return JsonSerializer.Deserialize<T>(File.ReadAllText(p), J) ?? fallback;
        }
        catch { return fallback; }
    }

    public static void Save<T>(string file, T data)
    {
        try { File.WriteAllText(Path.Combine(Dir, file), JsonSerializer.Serialize(data, J)); } catch { }
    }

    public static void Log(string msg)
    {
        try { File.AppendAllText(Path.Combine(Dir, "log.txt"), $"{DateTime.Now:yyyy-MM-dd HH:mm:ss}  {msg}{Environment.NewLine}"); } catch { }
    }

    public static string ReadLogTail(int lines)
    {
        try
        {
            var p = Path.Combine(Dir, "log.txt");
            if (!File.Exists(p)) return "";
            return string.Join(Environment.NewLine, File.ReadLines(p).TakeLast(lines));
        }
        catch { return ""; }
    }
}
