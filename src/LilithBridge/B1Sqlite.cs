using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;

namespace LilithBridge;

// The Windows system SQLite component is used through the documented SQLite C
// interface. No downloaded native binary, server, NuGet provider or network I/O.
internal sealed class B1Sqlite : IDisposable
{
    private IntPtr _db;
    private const int Ok = 0, Row = 100, Done = 101;
    internal B1Sqlite(string path, bool create = false, bool readOnly = false)
    {
        int flags = readOnly ? 1 : 2 | (create ? 4 : 0);
        int result = sqlite3_open_v2(Utf8(path), out _db, flags | 0x10000, IntPtr.Zero);
        if (result != Ok)
        {
            string message = Error(result);
            Dispose();
            throw new IOException(message);
        }
        try
        {
            Check(sqlite3_busy_timeout(_db, 100));
            if (!readOnly)
            {
                Execute("PRAGMA synchronous=FULL");
                Execute("PRAGMA secure_delete=ON");
                if (Scalar("PRAGMA secure_delete") != "1")
                    throw new IOException("sqlite_secure_delete_unavailable");
            }
        }
        catch { Dispose(); throw; }
    }
    internal static string Version => Marshal.PtrToStringUTF8(sqlite3_libversion()) ?? "unknown";
    private static byte[] Utf8(string value) => Encoding.UTF8.GetBytes(value + "\0");
    private string Error(int code) => "sqlite_" + code + ":" + (_db == IntPtr.Zero ? "open_failed" : Marshal.PtrToStringUTF8(sqlite3_errmsg(_db)));
    private void Check(int result) { if (result != Ok) throw new IOException(Error(result)); }
    private IntPtr Prepare(string sql, string[] values)
    {
        Check(sqlite3_prepare_v2(_db, Utf8(sql), -1, out IntPtr statement, IntPtr.Zero));
        try
        {
            for (int i = 0; i < values.Length; i++)
            {
                byte[] bytes = Utf8(values[i]);
                Check(sqlite3_bind_text(statement, i + 1, bytes, bytes.Length - 1, new IntPtr(-1)));
            }
            return statement;
        }
        catch { sqlite3_finalize(statement); throw; }
    }
    internal void Execute(string sql, params string[] values)
    {
        IntPtr statement = Prepare(sql, values);
        try
        {
            int result;
            do { result = sqlite3_step(statement); } while (result == Row);
            if (result != Done) throw new IOException(Error(result));
        }
        finally { sqlite3_finalize(statement); }
    }
    internal List<string[]> Rows(string sql, params string[] values)
    {
        var rows = new List<string[]>();
        IntPtr statement = Prepare(sql, values);
        try
        {
            int result;
            while ((result = sqlite3_step(statement)) == Row)
            {
                var row = new string[sqlite3_column_count(statement)];
                for (int i = 0; i < row.Length; i++)
                {
                    IntPtr value = sqlite3_column_text(statement, i);
                    byte[] bytes = new byte[sqlite3_column_bytes(statement, i)];
                    if (bytes.Length > 0) Marshal.Copy(value, bytes, 0, bytes.Length);
                    row[i] = Encoding.UTF8.GetString(bytes);
                }
                rows.Add(row);
            }
            if (result != Done) throw new IOException(Error(result));
            return rows;
        }
        finally { sqlite3_finalize(statement); }
    }
    internal string? Scalar(string sql, params string[] values)
    {
        var rows = Rows(sql, values);
        return rows.Count == 0 || rows[0].Length == 0 ? null : rows[0][0];
    }
    public void Dispose()
    {
        if (_db == IntPtr.Zero) return;
        sqlite3_close(_db); _db = IntPtr.Zero;
    }

    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [DllImport("winsqlite3.dll", CallingConvention = CallingConvention.Cdecl)]
    private static extern int sqlite3_open_v2(byte[] filename, out IntPtr db, int flags, IntPtr vfs);
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [DllImport("winsqlite3.dll", CallingConvention = CallingConvention.Cdecl)]
    private static extern int sqlite3_close(IntPtr db);
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [DllImport("winsqlite3.dll", CallingConvention = CallingConvention.Cdecl)]
    private static extern int sqlite3_busy_timeout(IntPtr db, int milliseconds);
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [DllImport("winsqlite3.dll", CallingConvention = CallingConvention.Cdecl)]
    private static extern IntPtr sqlite3_errmsg(IntPtr db);
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [DllImport("winsqlite3.dll", CallingConvention = CallingConvention.Cdecl)]
    private static extern int sqlite3_prepare_v2(IntPtr db, byte[] sql, int bytes, out IntPtr statement, IntPtr tail);
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [DllImport("winsqlite3.dll", CallingConvention = CallingConvention.Cdecl)]
    private static extern int sqlite3_bind_text(IntPtr statement, int index, byte[] value, int length, IntPtr destructor);
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [DllImport("winsqlite3.dll", CallingConvention = CallingConvention.Cdecl)]
    private static extern int sqlite3_step(IntPtr statement);
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [DllImport("winsqlite3.dll", CallingConvention = CallingConvention.Cdecl)]
    private static extern int sqlite3_finalize(IntPtr statement);
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [DllImport("winsqlite3.dll", CallingConvention = CallingConvention.Cdecl)]
    private static extern int sqlite3_column_count(IntPtr statement);
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [DllImport("winsqlite3.dll", CallingConvention = CallingConvention.Cdecl)]
    private static extern IntPtr sqlite3_column_text(IntPtr statement, int column);
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [DllImport("winsqlite3.dll", CallingConvention = CallingConvention.Cdecl)]
    private static extern int sqlite3_column_bytes(IntPtr statement, int column);
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [DllImport("winsqlite3.dll", CallingConvention = CallingConvention.Cdecl)]
    private static extern IntPtr sqlite3_libversion();
}
