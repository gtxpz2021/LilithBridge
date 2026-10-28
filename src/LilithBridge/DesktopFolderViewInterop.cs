using System;
using System.Collections.Generic;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Threading;

namespace LilithBridge;

internal sealed class DesktopArrangeNativeState
{
    public bool FlagsAvailable { get; set; }
    public uint FolderFlags { get; set; }
    public bool SortAvailable { get; set; }
    public bool SortColumnsReadable { get; set; }
    public List<DesktopArrangeSortColumn> SortColumns { get; set; } = new();
}

internal sealed class DesktopArrangeSortColumn
{
    public Guid FormatId { get; init; }
    public uint PropertyId { get; init; }
    public int Direction { get; init; }
}

[SupportedOSPlatform("windows")]
internal sealed class DesktopFolderViewHandle : IDisposable
{
    internal const uint FwfAutoArrange = 0x00000001;
    internal const uint FwfSnapToGrid = 0x00200000;
    internal const uint ArrangeFlagMask = FwfAutoArrange | FwfSnapToGrid;

    private const int MaxSortColumns = 16;
    private const int SortDirectionDescending = -1;

    private readonly object? _shellWindows;
    private readonly object? _browser;
    private readonly object? _shellBrowser;
    private readonly object? _shellView;
    private readonly IFolderView2 _view;
    private bool _disposed;

    private IFolderView2 View => _view;

    private DesktopFolderViewHandle(
        object shellWindows,
        object browser,
        object shellBrowser,
        object shellView,
        IFolderView2 view)
    {
        _shellWindows = shellWindows;
        _browser = browser;
        _shellBrowser = shellBrowser;
        _shellView = shellView;
        _view = view;
    }

    internal static DesktopFolderViewHandle? Open(out string error)
    {
        error = "";
        object? shellWindows = null;
        object? browser = null;
        object? shellBrowser = null;
        object? shellView = null;
        IntPtr pBrowser = IntPtr.Zero;
        IntPtr pUnknown = IntPtr.Zero;
        IntPtr pFolderView = IntPtr.Zero;

        try
        {
            Type? shellWindowsType = Type.GetTypeFromCLSID(
                new Guid("9BA05972-F6A8-11CF-A442-00A0C90A8F39"),
                true);
            if (shellWindowsType == null)
                throw new InvalidOperationException("ShellWindows CLSID not registered");
            shellWindows = Activator.CreateInstance(shellWindowsType);
            if (shellWindows == null)
                throw new InvalidOperationException("ShellWindows activation returned null");

            browser = shellWindows.GetType().InvokeMember(
                "FindWindowSW",
                BindingFlags.InvokeMethod | BindingFlags.Public | BindingFlags.Instance,
                null,
                shellWindows,
                new object[] { 0, 0, 8, 0, 1 });
            if (browser == null)
                throw new InvalidOperationException("FindWindowSW returned no desktop shell");

            var serviceProvider = (IServiceProvider)browser;
            Guid serviceId = new("4C96BE40-915C-11CF-99D3-00AA004AE837");
            Guid browserId = new("000214E2-0000-0000-C000-000000000046");
            int hResult = serviceProvider.QueryService(
                ref serviceId,
                ref browserId,
                out pBrowser);
            if (hResult < 0 || pBrowser == IntPtr.Zero)
                throw new COMException("QueryService(IShellBrowser) failed", hResult);

            shellBrowser = Marshal.GetObjectForIUnknown(pBrowser);
            hResult = ((IShellBrowser)shellBrowser).QueryActiveShellView(out shellView);
            if (hResult < 0 || shellView == null)
                throw new COMException("QueryActiveShellView failed", hResult);

            pUnknown = Marshal.GetIUnknownForObject(shellView);
            Guid folderViewId = new("1af3a467-214f-4298-908e-06b03e0b39f9");
            hResult = Marshal.QueryInterface(pUnknown, ref folderViewId, out pFolderView);
            if (hResult < 0 || pFolderView == IntPtr.Zero)
                throw new COMException("QueryInterface(IFolderView2) failed", hResult);

            var view = (IFolderView2)Marshal.GetObjectForIUnknown(pFolderView);
            return new DesktopFolderViewHandle(
                shellWindows,
                browser,
                shellBrowser,
                shellView,
                view);
        }
        catch (Exception exception)
        {
            error = FormatException(exception);
            SafeRelease(shellView);
            SafeRelease(shellBrowser);
            SafeRelease(browser);
            SafeRelease(shellWindows);
            return null;
        }
        finally
        {
            if (pBrowser != IntPtr.Zero)
                Marshal.Release(pBrowser);
            if (pUnknown != IntPtr.Zero)
                Marshal.Release(pUnknown);
            if (pFolderView != IntPtr.Zero)
                Marshal.Release(pFolderView);
        }
    }

    internal DesktopArrangeNativeState CaptureState()
    {
        var state = new DesktopArrangeNativeState();

        int flagsResult = View.GetCurrentFolderFlags(out uint folderFlags);
        if (flagsResult >= 0)
        {
            state.FlagsAvailable = true;
            state.FolderFlags = folderFlags;
        }

        int countResult = View.GetSortColumnCount(out int columnCount);
        if (countResult >= 0)
        {
            state.SortAvailable = true;
            if (columnCount > 0)
            {
                int readableCount = Math.Min(columnCount, MaxSortColumns);
                var columns = new NativeSortColumn[readableCount];
                int readResult = View.GetSortColumns(columns, readableCount);
                state.SortColumnsReadable = readResult >= 0;
                if (readResult >= 0)
                {
                    for (int i = 0; i < readableCount; i++)
                    {
                        state.SortColumns.Add(new DesktopArrangeSortColumn
                        {
                            FormatId = columns[i].PropertyKey.FormatId,
                            PropertyId = columns[i].PropertyKey.PropertyId,
                            Direction = columns[i].Direction
                        });
                    }
                }
            }
            else
            {
                state.SortColumnsReadable = true;
            }
        }

        return state;
    }

    internal bool SetCategorySort(out string error)
    {
        error = "";
        var column = new NativeSortColumn
        {
            PropertyKey = new NativePropertyKey
            {
                FormatId = new Guid("B725F130-47EF-101A-A5F1-02608C9EEBAC"),
                PropertyId = 4
            },
            Direction = SortDirectionDescending
        };
        int result = View.SetSortColumns(new[] { column }, 1);
        if (result < 0)
        {
            error = "SetSortColumns(ItemType) failed: " + FormatHResult(result);
            return false;
        }
        return true;
    }

    internal bool RefreshNativeAutoArrange(
        DesktopArrangeNativeState? before,
        out string error)
    {
        error = "";
        uint originalFlags = before?.FlagsAvailable == true ? before.FolderFlags : 0;
        bool originalAutoArrange = (originalFlags & FwfAutoArrange) != 0;
        int result;

        if (originalAutoArrange)
        {
            result = View.SetCurrentFolderFlags(FwfAutoArrange, 0);
            if (result < 0)
            {
                error = "disable auto-arrange failed: " + FormatHResult(result);
                return false;
            }
            Thread.Sleep(120);
        }

        result = View.SetCurrentFolderFlags(ArrangeFlagMask, ArrangeFlagMask);
        if (result < 0)
        {
            error = "enable auto-arrange failed: " + FormatHResult(result);
            return false;
        }

        // Windows keeps the sorted icon positions in its manual layout cache
        // while auto-arrange is off, so the sort visually reverts if the flag
        // is restored immediately. Leave native auto-arrange enabled to make
        // the category layout persist; the original state is still saved in
        // the snapshot and can be restored by <desktop_arrange:undo>.
        Thread.Sleep(2000);
        return true;
    }

    internal bool DisableAutoArrange(out string error)
    {
        error = "";
        int result = View.SetCurrentFolderFlags(ArrangeFlagMask, 0);
        if (result < 0)
        {
            error = "disable auto-arrange failed: " + FormatHResult(result);
            return false;
        }
        return true;
    }

    internal bool SetArrangeFlags(uint flags, out string error)
    {
        error = "";
        int result = View.SetCurrentFolderFlags(ArrangeFlagMask, flags & ArrangeFlagMask);
        if (result < 0)
        {
            error = "set arrange flags failed: " + FormatHResult(result);
            return false;
        }
        return true;
    }

    internal bool RestoreSortColumns(
        DesktopArrangeNativeState? state,
        out string error)
    {
        error = "";
        if (state?.SortColumnsReadable != true)
            return true;

        NativeSortColumn[] columns = state.SortColumns.Count == 0
            ? Array.Empty<NativeSortColumn>()
            : ToNativeColumns(state.SortColumns);
        int result = View.SetSortColumns(columns, columns.Length);
        if (result < 0)
        {
            error = "restore sort columns failed: " + FormatHResult(result);
            return false;
        }
        return true;
    }

    internal bool RestoreNativeState(
        DesktopArrangeNativeState? state,
        out string error)
    {
        error = "";
        bool flagsOk = true;
        bool sortOk = true;

        if (state?.FlagsAvailable == true)
        {
            int result = View.SetCurrentFolderFlags(
                ArrangeFlagMask,
                state.FolderFlags & ArrangeFlagMask);
            flagsOk = result >= 0;
            if (!flagsOk)
                error = "restore arrange flags failed: " + FormatHResult(result);
        }

        if (state?.SortColumnsReadable == true)
        {
            NativeSortColumn[] columns = state.SortColumns.Count == 0
                ? Array.Empty<NativeSortColumn>()
                : ToNativeColumns(state.SortColumns);
            int result = View.SetSortColumns(columns, columns.Length);
            sortOk = result >= 0;
            if (!sortOk)
            {
                if (error.Length > 0)
                    error += "; ";
                error += "restore sort columns failed: " + FormatHResult(result);
            }
        }

        return flagsOk && sortOk;
    }

    private static NativeSortColumn[] ToNativeColumns(
        IReadOnlyList<DesktopArrangeSortColumn> columns)
    {
        var native = new NativeSortColumn[columns.Count];
        for (int i = 0; i < columns.Count; i++)
        {
            native[i] = new NativeSortColumn
            {
                PropertyKey = new NativePropertyKey
                {
                    FormatId = columns[i].FormatId,
                    PropertyId = columns[i].PropertyId
                },
                Direction = columns[i].Direction
            };
        }
        return native;
    }

    private static string FormatException(Exception exception)
    {
        return exception.Message
            + (exception.InnerException == null
                ? ""
                : " | " + exception.InnerException.Message);
    }

    private static string FormatHResult(int hResult)
    {
        return "0x" + unchecked((uint)hResult).ToString("X8");
    }

    private static void SafeRelease(object? value)
    {
        if (value == null)
            return;
        try
        {
            Marshal.ReleaseComObject(value);
        }
        catch
        {
            // The RCW may already have been finalized.
        }
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        SafeRelease(_view);
        SafeRelease(_shellView);
        SafeRelease(_shellBrowser);
        SafeRelease(_browser);
        SafeRelease(_shellWindows);
    }

    [StructLayout(LayoutKind.Sequential, Pack = 4)]
    private struct NativePropertyKey
    {
        public Guid FormatId;
        public uint PropertyId;
    }

    [StructLayout(LayoutKind.Sequential, Pack = 4)]
    private struct NativeSortColumn
    {
        public NativePropertyKey PropertyKey;
        public int Direction;
    }

    [ComImport, Guid("6D5140C1-7436-11CE-8034-00AA006009FA"),
     InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IServiceProvider
    {
        [PreserveSig]
        int QueryService(ref Guid guidService, ref Guid riid, out IntPtr ppvObject);
    }

    [ComImport, Guid("000214E2-0000-0000-C000-000000000046"),
     InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IShellBrowser
    {
        [PreserveSig] int GetWindow(out IntPtr phwnd);
        [PreserveSig] int ContextSensitiveHelp(bool f);
        [PreserveSig] int InsertMenusSB(IntPtr h, IntPtr l);
        [PreserveSig] int SetMenuSB(IntPtr h1, IntPtr h2, IntPtr h3);
        [PreserveSig] int RemoveMenusSB(IntPtr h);
        [PreserveSig] int SetStatusTextSB(IntPtr p);
        [PreserveSig] int EnableModelessSB(bool f);
        [PreserveSig] int TranslateAcceleratorSB(IntPtr p, ushort w);
        [PreserveSig] int BrowseObject(IntPtr pidl, uint flags);
        [PreserveSig] int GetViewStateStream(uint grfMode, out IntPtr ppStrm);
        [PreserveSig] int GetControlWindow(uint id, out IntPtr phwnd);
        [PreserveSig] int SendControlMsg(uint id, uint msg, IntPtr wp, IntPtr lp, out IntPtr ret);
        [PreserveSig] int QueryActiveShellView(
            [MarshalAs(UnmanagedType.IUnknown)] out object ppshv);
    }

    [ComImport, Guid("1af3a467-214f-4298-908e-06b03e0b39f9"),
     InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IFolderView2
    {
        // IFolderView (14 methods after IUnknown) must remain in this exact
        // order because IFolderView2 shares the same vtable.
        [PreserveSig] int GetCurrentViewMode(out uint p);
        [PreserveSig] int SetCurrentViewMode(uint v);
        [PreserveSig] int GetFolder(ref Guid riid, [MarshalAs(UnmanagedType.IUnknown)] out object ppv);
        [PreserveSig] int Item(int i, out IntPtr ppidl);
        [PreserveSig] int ItemCount(uint f, out int c);
        [PreserveSig] int Items(uint f, ref Guid riid, [MarshalAs(UnmanagedType.IUnknown)] out object ppv);
        [PreserveSig] int GetSelectionMarkedItem(out int i);
        [PreserveSig] int GetFocusedItem(out int i);
        [PreserveSig] int GetItemPosition(IntPtr pidl, out long ppt);
        [PreserveSig] int GetSpacing(out long ppt);
        [PreserveSig] int GetDefaultSpacing(out long ppt);
        [PreserveSig] int GetAutoArrange();
        [PreserveSig] int SelectItem(int i, uint f);
        [PreserveSig] int SelectAndPositionItems(uint cidl, IntPtr apidl, IntPtr apt, uint f);

        // IFolderView2 methods are declared in Windows SDK vtable order.
        [PreserveSig] int SetGroupBy(ref NativePropertyKey key, [MarshalAs(UnmanagedType.Bool)] bool fAscending);
        [PreserveSig] int GetGroupBy(out NativePropertyKey key, [MarshalAs(UnmanagedType.Bool)] out bool fAscending);
        [PreserveSig] int SetViewProperty(IntPtr pidl, ref NativePropertyKey key, IntPtr propvar);
        [PreserveSig] int GetViewProperty(IntPtr pidl, ref NativePropertyKey key, IntPtr propvar);
        [PreserveSig] int SetTileViewProperties(IntPtr pidl, string props);
        [PreserveSig] int SetExtendedTileViewProperties(IntPtr pidl, string props);
        [PreserveSig] int SetText(int iType, string text);
        [PreserveSig] int SetCurrentFolderFlags(uint dwMask, uint dwFlags);
        [PreserveSig] int GetCurrentFolderFlags(out uint pdwFlags);
        [PreserveSig] int GetSortColumnCount(out int pcColumns);
        [PreserveSig] int SetSortColumns(
            [MarshalAs(UnmanagedType.LPArray)] NativeSortColumn[] rgSortColumns,
            int cColumns);
        [PreserveSig] int GetSortColumns(
            [MarshalAs(UnmanagedType.LPArray, SizeParamIndex = 1), Out]
            NativeSortColumn[] rgSortColumns,
            int cColumns);
    }
}
