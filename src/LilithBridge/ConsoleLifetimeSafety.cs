using System;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using BepInEx;
using BepInEx.Logging;

namespace LilithBridge;

internal static class ConsoleLifetimeSafety
{
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool FreeConsole();
    [DllImport("kernel32.dll")]
    private static extern IntPtr GetConsoleWindow();
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int MessageBoxW(IntPtr owner, string message, string caption, uint flags);

    internal static void Detach()
    {
        if (!OperatingSystem.IsWindows()) return;
        try
        {
            // The shared BepInEx console belongs to this game process. Windows
            // terminates attached clients when its close button is used.
            // Disk/custom listeners remain installed; no closing event is swallowed.
            foreach (var listener in Logger.Listeners.OfType<ConsoleLogListener>().ToArray())
                Logger.Listeners.Remove(listener);
            try { ConsoleManager.DetachConsole(); }
            catch (Exception error)
            {
                // A console inherited from a launcher may not be tracked by the
                // BepInEx driver. Native detachment below is still required.
                Plugin.PluginLog.LogWarning("Loader console cleanup: " + error.GetType().Name);
            }
            // An inherited launcher console is not necessarily owned by BepInEx.
            if (GetConsoleWindow() != IntPtr.Zero && !FreeConsole())
                throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
            Console.SetOut(TextWriter.Null);
            Console.SetError(TextWriter.Null);
            Plugin.PluginLog.LogInfo("Game detached from startup console; file logging remains available");
        }
        catch (Exception error)
        {
            Plugin.PluginLog.LogWarning("Startup console detachment unavailable: " + error.GetType().Name);
            ShowError("启动日志窗口未能安全分离。请先退出游戏，再运行新版安装器关闭启动控制台。", error.GetType().Name);
        }
    }

    internal static void ShowError(string message, string errorCode)
    {
        if (!OperatingSystem.IsWindows()) return;
        try
        {
            MessageBoxW(IntPtr.Zero, message + "\n\n日志位置：BepInEx/LogOutput.log\n错误代码：" + errorCode,
                "LilithBridge 启动提示", 0x00000010u | 0x00010000u);
        }
        catch { }
    }
}
