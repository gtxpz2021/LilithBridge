using System;
using System.Reflection;

namespace LilithBridge;

/// <summary>
/// Reads and writes the body text of a memo/scratch entry.
///
/// The game exposes that text as an il2cpp *property* (`get_text` / `set_text`),
/// not as a field. The earlier implementation looked only for a field and called
/// `field?.SetValue`, so it silently wrote nothing while MemoBook.Touch(entry)
/// still bumped the entry's date — the user saw a memo whose date changed with no
/// new content (confirmed in game, 2026-09-19). Both shapes are accepted here and
/// a missing member throws instead of doing nothing.
/// </summary>
internal static class MemoTextAccess
{
    private const BindingFlags Flags =
        BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;

    public static string Read(object entry)
    {
        Type type = entry.GetType();
        FieldInfo? field = type.GetField("text", Flags);
        if (field != null)
            return field.GetValue(entry) as string ?? "";
        PropertyInfo? property = type.GetProperty("text", Flags);
        if (property != null && property.CanRead)
            return property.GetValue(entry) as string ?? "";
        throw new InvalidOperationException("memo entry has no readable 'text' member");
    }

    public static void Write(object entry, string value)
    {
        Type type = entry.GetType();
        FieldInfo? field = type.GetField("text", Flags);
        if (field != null)
        {
            field.SetValue(entry, value);
            return;
        }
        PropertyInfo? property = type.GetProperty("text", Flags);
        if (property != null && property.CanWrite)
        {
            property.SetValue(entry, value);
            return;
        }
        throw new InvalidOperationException("memo entry has no writable 'text' member");
    }
}
