using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Threading;

namespace LilithBridge;

internal sealed class DesktopIconPoint
{
    public int Index { get; init; }
    public int X { get; init; }
    public int Y { get; init; }
}

internal sealed class DesktopArrangeSnapshot
{
    public int IconCount { get; init; }
    public int OriginX { get; init; }
    public int OriginY { get; init; }
    public List<DesktopIconPoint> Positions { get; init; } = new();
    public DesktopArrangeNativeState? NativeState { get; init; }
}

internal sealed class DesktopArrangeResult
{
    public bool Ok { get; init; }
    public bool DryRun { get; init; }
    public string Error { get; init; } = "";
    public string Method { get; init; } = "grid";
    public string Detail { get; init; } = "";
    public DesktopArrangeSnapshot? Before { get; init; }
    public DesktopArrangeSnapshot? After { get; init; }
}

internal static class DesktopArrangeService
{
    private const uint LvmGetItemCount = 0x1004;
    private const uint LvmSetItemPosition32 = 0x1031;
    private const uint SmtoAbortIfHung = 0x0002;
    private const uint TimeoutMs = 2000;

    public static DesktopArrangeResult Inspect(BridgeConfig config)
    {
        DesktopArrangeSnapshot? snapshot = ReadSnapshot(config, out string error);
        if (snapshot == null)
            return new DesktopArrangeResult { Error = error };
        return new DesktopArrangeResult
        {
            Ok = true,
            DryRun = true,
            Before = snapshot
        };
    }

    public static DesktopArrangeResult Arrange(BridgeConfig config)
    {
        DesktopArrangeSnapshot? before = ReadSnapshot(config, out string error, true);
        if (before == null)
            return new DesktopArrangeResult { Error = error };

        string sortMode = NormalizeSort(config.DesktopArrangeSort);
        if (config.DesktopArrangeDryRun)
        {
            if (sortMode == "item_type")
            {
                return new DesktopArrangeResult
                {
                    Ok = true,
                    DryRun = true,
                    Method = "native_item_type",
                    Detail = "read-only native Windows category sort inspection",
                    Before = before
                };
            }

            List<DesktopIconPoint> dryTargets = BuildGrid(before, config);
            return new DesktopArrangeResult
            {
                Ok = true,
                DryRun = true,
                Method = "grid",
                Before = before,
                After = new DesktopArrangeSnapshot
                {
                    IconCount = before.IconCount,
                    OriginX = before.OriginX,
                    OriginY = before.OriginY,
                    Positions = dryTargets
                }
            };
        }

        return sortMode == "item_type"
            ? ArrangeByNativeCategory(config, before)
            : ArrangeByGrid(config, before);
    }

    private static DesktopArrangeResult ArrangeByGrid(
        BridgeConfig config,
        DesktopArrangeSnapshot before)
    {
        List<DesktopIconPoint> targets = BuildGrid(before, config);
        IntPtr listView = FindListView();
        if (listView == IntPtr.Zero)
            return new DesktopArrangeResult { Error = "桌面图标列表窗口未找到" };

        bool applied = WritePositions(listView, targets, out string writeError);
        if (!applied)
        {
            WritePositions(listView, before.Positions, out _);
            return new DesktopArrangeResult
            {
                Error = "desktop icon position write failed, rolled back: " + writeError
            };
        }

        return new DesktopArrangeResult
        {
            Ok = true,
            Method = "grid",
            Before = before,
            After = new DesktopArrangeSnapshot
            {
                IconCount = before.IconCount,
                OriginX = before.OriginX,
                OriginY = before.OriginY,
                Positions = targets
            }
        };
    }

    private static DesktopArrangeResult ArrangeByNativeCategory(
        BridgeConfig config,
        DesktopArrangeSnapshot before)
    {
        DesktopFolderViewHandle? handle =
            DesktopFolderViewHandle.Open(out string openError);
        if (handle == null)
            return new DesktopArrangeResult
            {
                Error = "windows shell view unavailable: " + openError
            };

        try
        {
            if (!handle.SetCategorySort(out string sortError))
            {
                handle.RestoreNativeState(before.NativeState, out _);
                return new DesktopArrangeResult { Error = sortError };
            }

            Thread.Sleep(250);
            if (config.DesktopArrangeNativeAutoArrange
                && !handle.RefreshNativeAutoArrange(
                    before.NativeState,
                    out string autoArrangeError))
            {
                handle.RestoreNativeState(before.NativeState, out _);
                return new DesktopArrangeResult { Error = autoArrangeError };
            }

            DesktopArrangeSnapshot? after = ReadSnapshot(config, out string afterError);
            if (after == null)
            {
                handle.RestoreNativeState(before.NativeState, out _);
                return new DesktopArrangeResult
                {
                    Error = "native sort applied but post-snapshot failed: " + afterError
                };
            }

            return new DesktopArrangeResult
            {
                Ok = true,
                Method = "native_item_type",
                Detail = "native Windows category sort by item type",
                Before = before,
                After = after
            };
        }
        finally
        {
            handle.Dispose();
        }
    }

    private static string NormalizeSort(string sort)
    {
        string normalized = (sort ?? "").Trim().ToLowerInvariant();
        return normalized is "item_type" or "type" or "category" or "native" or "current"
            ? "item_type"
            : "grid";
    }

    public static bool Undo(DesktopArrangeSnapshot snapshot, out string error)
    {
        error = "";
        if (snapshot == null || snapshot.Positions.Count == 0)
        {
            error = "没有可撤销的桌面整理记录";
            return false;
        }

        IntPtr listView = FindListView();
        if (listView == IntPtr.Zero)
        {
            error = "桌面图标列表窗口未找到";
            return false;
        }

        DesktopFolderViewHandle? nativeHandle = null;
        try
        {
            nativeHandle = DesktopFolderViewHandle.Open(out string openError);
            if (nativeHandle == null && snapshot.NativeState != null)
            {
                error = "windows shell view unavailable during undo: " + openError;
                return false;
            }

            if (nativeHandle != null)
            {
                if (snapshot.NativeState?.FlagsAvailable == true)
                {
                    if (!nativeHandle.DisableAutoArrange(out string disableError))
                    {
                        error = disableError;
                        return false;
                    }
                    Thread.Sleep(120);
                }
                else if (snapshot.NativeState == null)
                {
                    nativeHandle.DisableAutoArrange(out _);
                    Thread.Sleep(120);
                }

                if (snapshot.NativeState != null
                    && !nativeHandle.RestoreSortColumns(
                        snapshot.NativeState,
                        out string sortError))
                {
                    error = sortError;
                    return false;
                }
                Thread.Sleep(120);
            }

            if (!WritePositions(listView, snapshot.Positions, out string writeError))
            {
                error = writeError;
                return false;
            }

            if (nativeHandle != null
                && snapshot.NativeState != null
                && !nativeHandle.RestoreNativeState(
                    snapshot.NativeState,
                    out string restoreError))
            {
                error = restoreError;
                return false;
            }
            return true;
        }
        finally
        {
            nativeHandle?.Dispose();
        }
    }

    private static DesktopArrangeSnapshot? ReadSnapshot(
        BridgeConfig config,
        out string error,
        bool captureNativeState = false)
    {
        error = "";
        IntPtr listView = FindListView();
        if (listView == IntPtr.Zero)
        {
            error = "desktop icon list window not found";
            return null;
        }

        long countValue = SendMessage(listView, LvmGetItemCount, IntPtr.Zero, IntPtr.Zero).ToInt64();
        if (countValue <= 0)
        {
            error = "desktop icon count is zero";
            return null;
        }
        int count = (int)Math.Min(countValue, config.DesktopArrangeMaxIcons);
        if (count <= 0)
        {
            error = "desktop icon count is zero";
            return null;
        }

        if (!GetWindowThreadProcessId(listView, out uint processId))
        {
            error = "failed to resolve explorer process id";
            return null;
        }
        IntPtr process = OpenProcess(0x438, false, processId);
        if (process == IntPtr.Zero)
        {
            error = "failed to open explorer process";
            return null;
        }

        IntPtr remote = IntPtr.Zero;
        IntPtr local = IntPtr.Zero;
        try
        {
            remote = VirtualAllocEx(process, IntPtr.Zero, 8u, 0x3000u, 0x04u);
            if (remote == IntPtr.Zero)
            {
                error = "failed to allocate remote desktop position buffer";
                return null;
            }
            local = Marshal.AllocHGlobal(8);
            if (local == IntPtr.Zero)
            {
                error = "failed to allocate local desktop position buffer";
                return null;
            }
            var positions = new List<DesktopIconPoint>(count);
            for (int i = 0; i < count; i++)
            {
                IntPtr result = SendMessage(listView, 0x1010u, (IntPtr)i, remote);
                if (result == IntPtr.Zero)
                {
                    error = "LVM_GETITEMPOSITION failed for icon " + i;
                    return null;
                }
                if (!ReadProcessMemory(process, remote, local, 8u, out _))
                {
                    error = "ReadProcessMemory failed for icon " + i;
                    return null;
                }
                byte[] bytes = new byte[8];
                Marshal.Copy(local, bytes, 0, 8);
                int first = BitConverter.ToInt32(bytes, 0);
                int second = BitConverter.ToInt32(bytes, 4);
                long packed = ((long)first << 32) | (uint)second;
                int x = (int)(packed >> 32);
                int y = (int)(packed & 0xFFFFFFFFL);
                positions.Add(new DesktopIconPoint { Index = i, X = x, Y = y });
            }
            int originX = int.MaxValue;
            int originY = int.MaxValue;
            foreach (DesktopIconPoint item in positions)
            {
                originX = Math.Min(originX, item.X);
                originY = Math.Min(originY, item.Y);
            }
            if (originX == int.MaxValue) originX = 0;
            if (originY == int.MaxValue) originY = 0;
            DesktopArrangeNativeState? nativeState = null;
            if (captureNativeState)
            {
                DesktopFolderViewHandle? nativeHandle =
                    DesktopFolderViewHandle.Open(out _);
                if (nativeHandle != null)
                {
                    try
                    {
                        nativeState = nativeHandle.CaptureState();
                    }
                    finally
                    {
                        nativeHandle.Dispose();
                    }
                }
            }
            return new DesktopArrangeSnapshot
            {
                IconCount = count,
                OriginX = originX,
                OriginY = originY,
                Positions = positions,
                NativeState = nativeState
            };
        }
        catch (Exception exception)
        {
            error = "desktop snapshot read failed: " + exception.Message;
            return null;
        }
        finally
        {
            if (local != IntPtr.Zero)
                Marshal.FreeHGlobal(local);
            if (remote != IntPtr.Zero)
                VirtualFreeEx(process, remote, 0u, 0x8000u);
            CloseHandle(process);
        }
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool GetWindowThreadProcessId(IntPtr hWnd, out uint processId);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr OpenProcess(uint access, bool inheritHandle, uint processId);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr VirtualAllocEx(
        IntPtr process,
        IntPtr address,
        uint size,
        uint allocationType,
        uint protect);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool ReadProcessMemory(
        IntPtr process,
        IntPtr address,
        IntPtr buffer,
        uint size,
        out IntPtr bytesRead);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool WriteProcessMemory(
        IntPtr process,
        IntPtr address,
        IntPtr buffer,
        uint size,
        out IntPtr bytesWritten);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool VirtualFreeEx(
        IntPtr process,
        IntPtr address,
        uint size,
        uint freeType);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(IntPtr handle);

    private static List<DesktopIconPoint> BuildGrid(
        DesktopArrangeSnapshot snapshot,
        BridgeConfig config)
    {
        int columns = Math.Clamp(config.DesktopArrangeColumns, 1, Math.Max(1, snapshot.IconCount));
        int spacing = Math.Clamp(config.DesktopArrangeSpacing, 32, 256);
        int margin = 8;
        var targets = new List<DesktopIconPoint>(snapshot.IconCount);
        for (int i = 0; i < snapshot.IconCount; i++)
        {
            int row = i / columns;
            int column = i % columns;
            targets.Add(new DesktopIconPoint
            {
                Index = i,
                X = snapshot.OriginX + margin + column * spacing,
                Y = snapshot.OriginY + margin + row * spacing
            });
        }
        return targets;
    }

    private static bool WritePositions(
        IntPtr listView,
        List<DesktopIconPoint> positions,
        out string error)
    {
        error = "";
        if (!GetWindowThreadProcessId(listView, out uint processId))
        {
            error = "GetWindowThreadProcessId failed";
            return false;
        }
        IntPtr process = OpenProcess(0x438, false, processId);
        if (process == IntPtr.Zero)
        {
            error = "OpenProcess failed";
            return false;
        }
        IntPtr remote = VirtualAllocEx(process, IntPtr.Zero, 8u, 0x3000u, 0x04u);
        IntPtr local = Marshal.AllocHGlobal(8);
        try
        {
            if (remote == IntPtr.Zero || local == IntPtr.Zero)
            {
                error = "remote or local desktop position buffer allocation failed";
                return false;
            }
            foreach (DesktopIconPoint position in positions)
            {
                var point = new Point { X = position.X, Y = position.Y };
                Marshal.StructureToPtr(point, local, false);
                if (!WriteProcessMemory(process, remote, local, 8u, out _))
                {
                    error = "WriteProcessMemory failed at icon " + position.Index;
                    return false;
                }
                IntPtr result = SendMessage(
                    listView,
                    LvmSetItemPosition32,
                    (IntPtr)position.Index,
                    remote);
                if (result == IntPtr.Zero)
                {
                    error = "LVM_SETITEMPOSITION32 failed at icon " + position.Index;
                    return false;
                }
            }
            return true;
        }
        finally
        {
            if (local != IntPtr.Zero)
                Marshal.FreeHGlobal(local);
            if (remote != IntPtr.Zero)
                VirtualFreeEx(process, remote, 0u, 0x8000u);
            CloseHandle(process);
        }
    }

    private static IntPtr FindListView()
    {
        try
        {
            IntPtr native = DesktopIconCounter.FindDesktopListView();
            if (native != IntPtr.Zero)
                return native;
        }
        catch
        {
            // Fall through to the managed implementation.
        }

        IntPtr progman = FindWindow("Progman", null);
        if (progman == IntPtr.Zero)
            progman = FindWindow("WorkerW", null);
        if (progman == IntPtr.Zero)
            return IntPtr.Zero;

        IntPtr defView = FindWindowEx(progman, IntPtr.Zero, "SHELLDLL_DefView", null);
        if (defView == IntPtr.Zero)
        {
            IntPtr worker = IntPtr.Zero;
            while ((worker = FindWindowEx(IntPtr.Zero, worker, "WorkerW", null)) != IntPtr.Zero)
            {
                defView = FindWindowEx(worker, IntPtr.Zero, "SHELLDLL_DefView", null);
                if (defView != IntPtr.Zero)
                    break;
            }
        }
        if (defView == IntPtr.Zero)
            return IntPtr.Zero;
        return FindWindowEx(defView, IntPtr.Zero, "SysListView32", null);
    }

    private static IntPtr SendMessage(IntPtr hWnd, uint message, IntPtr wParam, IntPtr lParam)
    {
        IntPtr result = IntPtr.Zero;
        SendMessageTimeout(
            hWnd,
            message,
            wParam,
            lParam,
            SmtoAbortIfHung,
            TimeoutMs,
            out result);
        return result;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Point
    {
        public int X;
        public int Y;
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr FindWindow(string? className, string? windowName);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr FindWindowEx(
        IntPtr parent,
        IntPtr childAfter,
        string? className,
        string? windowName);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr SendMessageTimeout(
        IntPtr hWnd,
        uint message,
        IntPtr wParam,
        IntPtr lParam,
        uint flags,
        uint timeout,
        out IntPtr result);
}
