using System;
using System.IO;

namespace LilithBridge;

/// <summary>
/// Single source of truth for the plugin data root.
///
/// A copied game build can carry <c>LilithBridge.data-dir.txt</c> next to the
/// plugin assembly. When that file names a folder, every store (memory, sessions,
/// telemetry, prompt dumps, diagnostics) moves there instead of the production
/// folder under %APPDATA%\LilithAI. Relative values resolve against the game root
/// (the folder containing Lilith.exe), so a vanguard copy keeps its data inside
/// its own game folder and stays portable.
/// </summary>
internal static class DataPaths
{
    public const string EnvironmentVariable = "LILITH_BRIDGE_DATA_DIR";
    public const string MarkerFileName = "LilithBridge.data-dir.txt";

    private static string? _cached;

    public static string Root() => _cached ??= Resolve();

    /// <summary>Test hook: drop the cached value so an override can be re-evaluated.</summary>
    public static void ResetCacheForTests() => _cached = null;

    public static string DefaultProductionRoot() => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "LilithAI");

    private static string Resolve() => ResolveForDirectory(
        Environment.GetEnvironmentVariable(EnvironmentVariable), PluginDirectory(), DefaultProductionRoot());

    // Explicit inputs let offline tests exercise damaged markers without touching the game.
    internal static string ResolveForDirectory(string? environment, string pluginDirectory, string productionRoot)
    {
        if (environment != null)
            return ResolveCandidate(environment, pluginDirectory, productionRoot);

        string marker = pluginDirectory.Length == 0
            ? ""
            : Path.Combine(pluginDirectory, MarkerFileName);
        if (marker.Length > 0 && MarkerExists(marker))
        {
            try
            {
                EnsureNoReparseAncestors(pluginDirectory);
                if ((File.GetAttributes(marker) & FileAttributes.ReparsePoint) != 0)
                    throw new InvalidOperationException("Isolation marker cannot be a reparse point.");
                string? candidate = null;
                foreach (string line in File.ReadAllLines(marker))
                {
                    if (string.IsNullOrWhiteSpace(line) || line.TrimStart().StartsWith("#", StringComparison.Ordinal))
                        continue;
                    if (candidate != null)
                        throw new InvalidOperationException("Isolation marker must contain exactly one data path.");
                    candidate = line;
                }
                return ResolveCandidate(candidate ?? "", pluginDirectory, productionRoot);
            }
            catch (Exception error)
            {
                throw new InvalidOperationException("Explicit data isolation marker is invalid: " + marker, error);
            }
        }

        return productionRoot;
    }

    private static bool MarkerExists(string marker)
    {
        try { File.GetAttributes(marker); return true; }
        catch (FileNotFoundException) { return false; }
        catch (DirectoryNotFoundException) { return false; }
    }

    private static string ResolveCandidate(string value, string pluginDirectory, string productionRoot)
    {
        try
        {
            string candidate = value.Trim();
            if (candidate.StartsWith("\"", StringComparison.Ordinal) && candidate.EndsWith("\"", StringComparison.Ordinal)
                && candidate.Length >= 2)
                candidate = candidate.Substring(1, candidate.Length - 2);
            if (candidate.Length == 0 || candidate.StartsWith("#", StringComparison.Ordinal)
                || candidate.IndexOfAny(new[] { '*', '?', '"', '<', '>', '|', '\0' }) >= 0)
                throw new InvalidOperationException("Isolation path is empty or invalid.");
            if (Path.IsPathRooted(candidate) && !Path.IsPathFullyQualified(candidate))
                throw new InvalidOperationException("Isolation path cannot be drive-relative or root-relative.");
            if (!Path.IsPathRooted(candidate))
            {
                string gameRoot = GameRoot(pluginDirectory);
                if (gameRoot.Length == 0) throw new InvalidOperationException("Cannot resolve relative isolation path.");
                candidate = Path.Combine(gameRoot, candidate);
            }
            string resolved = CanonicalPath(candidate);
            EnsureIsolated(resolved, productionRoot);
            return resolved;
        }
        catch (Exception error)
        {
            throw new InvalidOperationException("Explicit data isolation could not be established; default data root was not used.", error);
        }
    }

    internal static string CanonicalPath(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || !Path.IsPathFullyQualified(path))
            throw new InvalidOperationException("Data path must be absolute.");
        string full = Path.GetFullPath(path);
        string root = Path.GetPathRoot(full)!;
        foreach (string part in path.Substring(Path.GetPathRoot(path)!.Length).Split(new[] { '\\', '/' }, StringSplitOptions.RemoveEmptyEntries))
            if (part.EndsWith('.') || part.EndsWith(' ')
                || System.Text.RegularExpressions.Regex.IsMatch(part, @"~[0-9]"))
                throw new InvalidOperationException("Ambiguous directory path segment.");
        if (full.Substring(root.Length).Contains(':'))
            throw new InvalidOperationException("Data path cannot use alternate data streams.");
        return full.Length > root.Length ? full.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) : full;
    }

    internal static bool PathsOverlap(string first, string second)
    {
        first = CanonicalPath(first);
        second = CanonicalPath(second);
        return ContainsPath(first, second) || ContainsPath(second, first);
    }

    private static bool ContainsPath(string parent, string child) =>
        string.Equals(parent, child, StringComparison.OrdinalIgnoreCase)
        || child.StartsWith(parent.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
            + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);

    internal static void EnsureIsolated(string candidate, string protectedRoot)
    {
        if (PathsOverlap(candidate, protectedRoot))
            throw new InvalidOperationException("Isolation data path overlaps the protected data root.");
        EnsureNoReparseAncestors(candidate);
        EnsureNoReparseAncestors(protectedRoot);
    }

    internal static void EnsureNoReparseAncestors(string path)
    {
        string? current = CanonicalPath(path);
        while (current != null)
        {
            try
            {
                FileAttributes attributes = File.GetAttributes(current);
                if ((attributes & FileAttributes.ReparsePoint) != 0)
                    throw new InvalidOperationException("Data isolation cannot traverse a reparse point: " + current);
                if ((attributes & FileAttributes.Directory) == 0)
                    throw new InvalidOperationException("Data directory path traverses a file: " + current);
            }
            catch (FileNotFoundException) { }
            catch (DirectoryNotFoundException) { }
            current = Path.GetDirectoryName(current);
        }
    }

    private static string PluginDirectory()
    {
        try
        {
            string location = typeof(DataPaths).Assembly.Location ?? "";
            string directory = location.Length == 0
                ? ""
                : Path.GetDirectoryName(location) ?? "";
            if (directory.Length > 0)
                return directory;
        }
        catch
        {
            // Fall through to the base directory.
        }
        try
        {
            return AppContext.BaseDirectory ?? "";
        }
        catch
        {
            return "";
        }
    }

    private static string GameRoot(string startDirectory)
    {
        try
        {
            var directory = new DirectoryInfo(startDirectory);
            for (int depth = 0; depth < 4 && directory != null; depth++, directory = directory.Parent)
            {
                if (File.Exists(Path.Combine(directory.FullName, "Lilith.exe")))
                    return directory.FullName;
            }
        }
        catch
        {
            // Ignore and fall through.
        }
        return startDirectory;
    }
}
