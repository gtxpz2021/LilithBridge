using System;
using System.Collections.Generic;
namespace LilithBridge;

// Program-only status. Never a character utterance, memory event or model input.
internal sealed class SaveFailurePresentation
{
    private string _persona = "", _scene = "";
    private double _personaRecoveredUntil, _sceneRecoveredUntil;
    private string _cachedText = "";
    private double _refreshAt = double.PositiveInfinity;
    private double _lastNow = double.NegativeInfinity;
    internal string Update(string persona, string scene, double now)
    {
        persona ??= ""; scene ??= "";
        bool unchanged = string.Equals(_persona, persona, StringComparison.Ordinal)
            && string.Equals(_scene, scene, StringComparison.Ordinal);
        bool clockAdvanced = now >= _lastNow;
        _lastNow = now;
        // Keep observing both failure strings every frame. Only presentation is
        // cached; recovery expiry and a backwards clock invalidate it.
        if (unchanged && clockAdvanced && now < _refreshAt) return _cachedText;
        if (_persona.Length > 0 && persona.Length == 0) _personaRecoveredUntil = now + 8;
        if (_scene.Length > 0 && scene.Length == 0) _sceneRecoveredUntil = now + 8;
        if (persona.Length > 0) _personaRecoveredUntil = 0;
        if (scene.Length > 0) _sceneRecoveredUntil = 0;
        _persona = persona; _scene = scene;
        var lines = new List<string>();
        Add(lines, "v4-native-persona.json", persona, _personaRecoveredUntil > now);
        Add(lines, "v4-native-last-scene.json", scene, _sceneRecoveredUntil > now);
        _refreshAt = double.PositiveInfinity;
        if (persona.Length == 0 && _personaRecoveredUntil > now)
            _refreshAt = Math.Min(_refreshAt, _personaRecoveredUntil);
        if (scene.Length == 0 && _sceneRecoveredUntil > now)
            _refreshAt = Math.Min(_refreshAt, _sceneRecoveredUntil);
        _cachedText = lines.Count == 0 ? "" : "LilithBridge \u4fdd\u5b58\u72b6\u6001\n" + string.Join("\n", lines);
        return _cachedText;
    }
    private static void Add(List<string> lines, string file, string reason, bool recovered)
    {
        if (reason.Length == 0)
        {
            if (recovered) lines.Add("\u5df2\u6062\u590d\uff1a" + file + "\uff08\u5df2\u5b8c\u6210\u4e00\u6b21\u4fdd\u5b58\uff09");
            return;
        }
        // Display only recognized codes, never arbitrary error messages or payload.
        string code = reason is "pending_tmp" or "backup_failed" or "persona_read_failed" or "write_failed" ? reason : "unavailable";
        string action = code switch
        {
            "pending_tmp" => "\u8bf7\u5148\u5907\u4efd\u5e76\u68c0\u67e5\u540c\u540d .tmp\uff0c\u4e0d\u8981\u76f4\u63a5\u5220\u9664\u3002",
            "backup_failed" => "\u8bf7\u68c0\u67e5\u540c\u76ee\u5f55\u5907\u4efd\u8def\u5f84\u548c\u5199\u5165\u6743\u9650\u3002",
            "persona_read_failed" => "\u539f\u4ef6\u672a\u8986\u5199\uff1b\u8bf7\u5148\u5907\u4efd\u4fee\u590d\uff0c\u518d\u91cd\u65b0\u542f\u52a8\u4ee5\u91cd\u8bfb\u3002",
            _ => "\u8bf7\u68c0\u67e5\u78c1\u76d8\u7a7a\u95f4\u548c\u5199\u5165\u6743\u9650\uff1b\u539f\u4ef6\u4e0e\u4e34\u65f6\u6587\u4ef6\u4e0d\u4f1a\u81ea\u52a8\u4fee\u590d\u3002"
        };
        lines.Add("\u672a\u5b8c\u6210\u4fdd\u5b58\uff1a" + file + " [" + code + "] " + action);
    }
}
