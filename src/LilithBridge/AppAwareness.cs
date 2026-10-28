using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

namespace LilithBridge;

internal sealed record ForegroundAppInfo(
    string ProcessName,
    string WindowTitle,
    string WindowClass,
    string Category)
{
    public bool SameAs(ForegroundAppInfo other)
    {
        return other != null
            && string.Equals(ProcessName, other.ProcessName, StringComparison.OrdinalIgnoreCase)
            && string.Equals(WindowTitle, other.WindowTitle, StringComparison.Ordinal)
            && string.Equals(WindowClass, other.WindowClass, StringComparison.Ordinal);
    }

    public string Describe()
    {
        string title = string.IsNullOrWhiteSpace(WindowTitle)
            ? ""
            : ";title=" + WindowTitle;
        return "app=" + ProcessName
            + ";category=" + Category
            + title
            + ";class=" + WindowClass;
    }

    public string DescribeRedacted()
    {
        string title = string.IsNullOrWhiteSpace(WindowTitle)
            ? ""
            : ";title=" + PrivacySafety.RedactWindowTitle(WindowTitle);
        return "app=" + PrivacySafety.Redact(ProcessName)
            + ";category=" + Category
            + title
            + ";class=" + PrivacySafety.Redact(WindowClass);
    }
}

internal static class AppAwareness
{
    public static ForegroundAppInfo? GetCurrent()
    {
        try
        {
            IntPtr window = GetForegroundWindow();
            if (window == IntPtr.Zero)
                return null;
            GetWindowThreadProcessId(window, out uint processId);
            if (processId == 0)
                return null;

            string processName = "";
            try
            {
                processName = Process.GetProcessById((int)processId).ProcessName;
            }
            catch
            {
                // The process may have exited between calls.
            }

            string title = ReadWindowText(window);
            string className = ReadClassName(window);
            string category = Classify(processName, title, className);
            if (string.IsNullOrWhiteSpace(processName)
                && string.IsNullOrWhiteSpace(title)
                && string.IsNullOrWhiteSpace(className))
                return null;

            return new ForegroundAppInfo(processName, title, className, category);
        }
        catch
        {
            return null;
        }
    }

    private static string Classify(string process, string title, string windowClass)
    {
        string value = (process + " " + title + " " + windowClass).ToLowerInvariant();
        if (value.Contains("lilith") || value.Contains("the noexistencen"))
            return "desktop";
        if (ContainsAny(value, "chrome", "msedge", "firefox", "brave", "opera", "vivaldi", "arc"))
            return IsVideo(value) ? "video" : "browser";
        if (ContainsAny(value, "youtube", "bilibili", "netflix", "stream", "twitch"))
            return "video";
        if (ContainsAny(value, "vlc", "mpv", "potplayer", "kmplayer", "obs", "streamlabs"))
            return "video";
        if (ContainsAny(value, "code", "cursor", "devenv", "pycharm", "idea", "rider",
            "webstorm", "sublime", "vim", "visual studio"))
            return "coding";
        if (ContainsAny(value, "spotify", "cloudmusic", "qqmusic", "netease", "music",
            "foobar", "wmplayer"))
            return "music";
        if (ContainsAny(value, "wechat", "weixin", "qq", "telegram", "discord", "slack",
            "feishu", "dingtalk", "teams"))
            return "chat";
        if (ContainsAny(value, "winword", "excel", "powerpnt", "wps", "office",
            "onenote", "outlook"))
            return "office";
        if (ContainsAny(value, "photoshop", "illustrator", "figma", "blender", "krita",
            "paint", "clipstudio", "design"))
            return "design";
        if (ContainsAny(value, "unity", "unreal", "gameassembly", "steam", "epic",
            "origin", "battle", "minecraft", "genshin", "原神", "elden", "blackmyth"))
            return "game";
        return "unknown";
    }

    private static bool IsVideo(string value)
    {
        return ContainsAny(value, "youtube", "bilibili", "netflix", "twitch",
            "video", "播放", "视频", "直播");
    }

    private static bool ContainsAny(string value, params string[] terms)
    {
        foreach (string term in terms)
        {
            if (value.Contains(term, StringComparison.OrdinalIgnoreCase))
                return true;
        }
        return false;
    }

    private static string ReadWindowText(IntPtr window)
    {
        int length = GetWindowTextLength(window);
        if (length <= 0)
            return "";
        var buffer = new StringBuilder(Math.Min(length + 1, 257));
        GetWindowText(window, buffer, buffer.Capacity);
        string value = buffer.ToString().Replace("\r", " ").Replace("\n", " ").Trim();
        return value.Length > 160 ? value[..160] : value;
    }

    private static string ReadClassName(IntPtr window)
    {
        var buffer = new StringBuilder(256);
        int length = GetClassName(window, buffer, buffer.Capacity);
        return length > 0 ? buffer.ToString() : "";
    }

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr window, out uint processId);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetWindowTextLength(IntPtr window);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetWindowText(IntPtr window, StringBuilder text, int maxCount);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetClassName(IntPtr window, StringBuilder text, int maxCount);
}
