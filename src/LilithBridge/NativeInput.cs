using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using UnityEngine;

namespace LilithBridge;

/// <summary>
/// Windows keyboard polling for the transparent desktop window. Unity's legacy
/// Input API stops reporting keys when another application owns the foreground,
/// so this reads Win32 state directly and never consumes the key.
/// </summary>
internal static class NativeInput
{
    private static readonly Dictionary<KeyCode, bool> LastDown = new();

    [DllImport("user32.dll")]
    private static extern short GetAsyncKeyState(int virtualKey);

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr window, out uint processId);

    [DllImport("user32.dll")]
    private static extern uint GetCurrentThreadId();

    [DllImport("user32.dll")]
    private static extern bool ShowWindow(IntPtr window, int command);

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr window);

    [DllImport("user32.dll")]
    private static extern bool BringWindowToTop(IntPtr window);

    [DllImport("user32.dll")]
    private static extern IntPtr SetFocus(IntPtr window);

    [DllImport("user32.dll")]
    private static extern bool AttachThreadInput(uint attach, uint attachTo, bool attachFlag);

    /// <summary>
    /// Returns true only on the first frame a key transitions from up to down.
    /// </summary>
    public static bool IsPressed(KeyCode key)
    {
        bool down = IsKeyDown(key);
        bool wasDown = LastDown.TryGetValue(key, out bool previous) && previous;
        LastDown[key] = down;
        return down && !wasDown;
    }

    public static bool IsKeyDown(KeyCode key)
    {
        if (OperatingSystem.IsWindows() && TryGetVirtualKey(key, out int virtualKey))
        {
            // Win32 state is authoritative even when the transparent desktop
            // window is receiving keyboard input without owning the focus.
            if ((GetAsyncKeyState(virtualKey) & 0x8000) != 0)
                return true;
        }
        try
        {
            return Input.GetKey(key);
        }
        catch
        {
            return false;
        }
    }

    public static string DescribeFocus()
    {
        try
        {
            IntPtr foreground = GetForegroundWindow();
            if (foreground == IntPtr.Zero)
                return "foreground=none";
            GetWindowThreadProcessId(foreground, out uint processId);
            return $"foreground={processId};self={System.Diagnostics.Process.GetCurrentProcess().Id}";
        }
        catch
        {
            return "foreground=unknown";
        }
    }

    public static bool IsEscapePressed()
    {
        return IsPressed(KeyCode.Escape);
    }

    public static bool IsGameForeground()
    {
        try
        {
            IntPtr foreground = GetForegroundWindow();
            if (foreground == IntPtr.Zero)
                return false;
            GetWindowThreadProcessId(foreground, out uint processId);
            return processId == System.Diagnostics.Process.GetCurrentProcess().Id;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Used only when the user has already chosen to open the in-game input
    /// box. It asks the native window to accept keyboard input, but does not
    /// steal focus during normal right-click / Enter interaction.
    /// </summary>
    public static bool BringGameToForeground()
    {
        if (!OperatingSystem.IsWindows())
            return false;
        try
        {
            IntPtr window = System.Diagnostics.Process.GetCurrentProcess().MainWindowHandle;
            if (window == IntPtr.Zero)
                window = TransparentWindowNew.Hwnd;
            if (window == IntPtr.Zero)
                return false;

            ShowWindow(window, 9);
            if (SetForegroundWindow(window))
                return true;

            IntPtr foreground = GetForegroundWindow();
            uint foregroundThread = foreground == IntPtr.Zero
                ? 0u
                : GetWindowThreadProcessId(foreground, out _);
            uint currentThread = GetCurrentThreadId();
            bool attached = foregroundThread != 0
                && foregroundThread != currentThread
                && AttachThreadInput(currentThread, foregroundThread, true);
            try
            {
                BringWindowToTop(window);
                SetForegroundWindow(window);
                SetFocus(window);
            }
            finally
            {
                if (attached)
                    AttachThreadInput(currentThread, foregroundThread, false);
            }
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static bool TryGetVirtualKey(KeyCode key, out int virtualKey)
    {
        virtualKey = 0;
        int code = (int)key;
        if (code >= (int)KeyCode.Alpha0 && code <= (int)KeyCode.Alpha9)
            virtualKey = 0x30 + code - (int)KeyCode.Alpha0;
        else if (code >= (int)KeyCode.A && code <= (int)KeyCode.Z)
            virtualKey = 0x41 + code - (int)KeyCode.A;
        else if (code >= (int)KeyCode.Keypad0 && code <= (int)KeyCode.Keypad9)
            virtualKey = 0x60 + code - (int)KeyCode.Keypad0;
        else if (code >= (int)KeyCode.F1 && code <= (int)KeyCode.F15)
            virtualKey = 0x70 + code - (int)KeyCode.F1;
        else
        {
            virtualKey = key switch
            {
                KeyCode.Backspace => 0x08,
                KeyCode.Return or KeyCode.KeypadEnter => 0x0D,
                KeyCode.Escape => 0x1B,
                KeyCode.Space => 0x20,
                KeyCode.LeftArrow => 0x25,
                KeyCode.UpArrow => 0x26,
                KeyCode.RightArrow => 0x27,
                KeyCode.DownArrow => 0x28,
                KeyCode.Delete => 0x2E,
                KeyCode.LeftShift => 0xA0,
                KeyCode.RightShift => 0xA1,
                KeyCode.LeftControl => 0xA2,
                KeyCode.RightControl => 0xA3,
                KeyCode.LeftAlt => 0xA4,
                KeyCode.RightAlt => 0xA5,
                KeyCode.Semicolon => 0xBA,
                KeyCode.Equals => 0xBB,
                KeyCode.Comma => 0xBC,
                KeyCode.Minus => 0xBD,
                KeyCode.Period => 0xBE,
                KeyCode.Slash => 0xBF,
                KeyCode.BackQuote => 0xC0,
                KeyCode.LeftBracket => 0xDB,
                KeyCode.Backslash => 0xDC,
                KeyCode.RightBracket => 0xDD,
                KeyCode.Quote => 0xDE,
                _ => 0
            };
        }
        return virtualKey != 0;
    }
}
