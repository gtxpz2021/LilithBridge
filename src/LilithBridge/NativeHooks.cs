using System;
using System.Collections.Generic;
using System.Reflection;
using System.Linq;
using HarmonyLib;

namespace LilithBridge;

/// <summary>
/// Observes the original player-line UI and shields native mouse polling only
/// while our input hit area or modal UI owns it. Normal native interactions,
/// dialogue bubbles, animations and voice keep their original path.
/// </summary>
internal static class NativeHooks
{
    private sealed record InstalledHook(MethodBase Target, MethodInfo Hook, string Phase);
    private static readonly object RegistrationLock=new();
    private static readonly Dictionary<string,InstalledHook[]> Registrations=new(StringComparer.Ordinal);
    internal static IReadOnlyList<Type> PatchClasses => typeof(NativeHooks).GetNestedTypes(BindingFlags.Public|BindingFlags.NonPublic)
        .Where(t=>t.GetCustomAttributes(typeof(HarmonyPatch),false).Length>0).OrderBy(t=>t.FullName,StringComparer.Ordinal).ToArray();
    private static InstalledHook[] Installed(string owner)=>Harmony.GetAllPatchedMethods().SelectMany(target=>
    {
        var info=Harmony.GetPatchInfo(target);if(info==null)return Array.Empty<InstalledHook>();
        return info.Prefixes.Select(x=>(Patch:x,Phase:"prefix")).Concat(info.Postfixes.Select(x=>(Patch:x,Phase:"postfix")))
            .Concat(info.Finalizers.Select(x=>(Patch:x,Phase:"finalizer")))
            .Where(x=>x.Patch.owner==owner&&x.Patch.PatchMethod.DeclaringType?.DeclaringType==typeof(NativeHooks))
            .Select(x=>new InstalledHook(target,x.Patch.PatchMethod,x.Phase));
    }).ToArray();
    internal sealed record RegistrationPlan(string OwnerId, IReadOnlyList<PatchClassProcessor> Processors);

    internal static RegistrationPlan Prepare(Harmony harmony)
    {
        var types = PatchClasses;
        foreach (var type in types)
        {
            MethodInfo? targetProvider = type.GetMethods(BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic)
                .SingleOrDefault(m => m.GetCustomAttributes(typeof(HarmonyTargetMethods), false).Length > 0);
            if (targetProvider != null)
            {
                var targets = ((IEnumerable<MethodBase>?)targetProvider.Invoke(null, null))?.ToArray();
                if (targets == null || targets.Length == 0 || targets.Any(t => t == null))
                    throw new MissingMethodException("Native hook target set is empty: " + type.Name);
            }
            else
            {
                var attributes = type.GetCustomAttributes(typeof(HarmonyPatch), false).Cast<HarmonyPatch>();
                var info = HarmonyMethod.Merge(attributes.Select(a => a.info).ToList());
                if (info.declaringType == null || info.methodName == null
                    || AccessTools.Method(info.declaringType, info.methodName, info.argumentTypes) == null)
                    throw new MissingMethodException("Native hook target unavailable: " + type.Name);
            }
            // Resolve callback signature types before any owner patches are active.
            foreach (var method in type.GetMethods(BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic))
            {
                _ = method.ReturnType;
                foreach (var parameter in method.GetParameters()) _ = parameter.ParameterType;
            }
        }
        return new RegistrationPlan(harmony.Id, types.Select(harmony.CreateClassProcessor).ToArray());
    }

    internal static void ForgetRegistration(string owner)
    {
        lock (RegistrationLock) Registrations.Remove(owner);
    }

    // Plugin.Load and the isolated registration test use this exact entry, not PatchAll on the shell type.
    internal static IReadOnlyList<MethodBase> Apply(Harmony harmony) => Apply(harmony, Prepare(harmony));

    internal static IReadOnlyList<MethodBase> Apply(Harmony harmony, RegistrationPlan plan)
    {
        if (plan.OwnerId != harmony.Id) throw new InvalidOperationException("Native hook plan owner mismatch");
        lock(RegistrationLock)
        {
            var installed=Installed(harmony.Id);
            if(Registrations.TryGetValue(harmony.Id,out var previous)&&installed.Length==previous.Length
                &&new HashSet<InstalledHook>(previous).SetEquals(installed))return installed.Select(x=>x.Target).Distinct().ToArray();
            if(installed.Length>0)throw new InvalidOperationException("Native hook registration is partial or externally changed; refusing duplicate installation");
            foreach(var processor in plan.Processors) processor.Patch();
            installed=Installed(harmony.Id);Registrations[harmony.Id]=installed;
            return installed.Select(x=>x.Target).Distinct().ToArray();
        }
    }

    private static readonly object OptionLock = new();
    private static Il2CppSystem.Collections.Generic.IReadOnlyList<string>? _textOptions;
    private static Il2CppSystem.Collections.Generic.IReadOnlyList<PlayerLineEntry>? _entryOptions;
    private static readonly List<string> ButtonTexts = new();
    private static float _snackMenuPoseHoldUntil = -1f;

    internal static bool IsSnackMenuPoseHoldActive =>
        UnityEngine.Time.unscaledTime < _snackMenuPoseHoldUntil;

    internal static void ClearSnackMenuPoseHold()
    {
        _snackMenuPoseHoldUntil = -1f;
    }

    [HarmonyPatch(typeof(CharacterInteractionHandler), "HandleHookChatRequest")]
    private static class DoubleClickPoseWakePatch
    {
        private static bool Prefix()
        {
            if (BridgeController.Instance?.BlocksNativePointerInput == true) return false;
            if (NativePoseGate.IsPoseBlocked)
                NativePoseGate.WakeForInput();
            return true;
        }
    }

    // The native interaction handler polls mouse state instead of UI raycasts.
    // Scope this gate to our input hit area/menu and the modal history window.
    [HarmonyPatch(typeof(CharacterInteractionHandler), "Update")]
    private static class NativeUiPointerShieldPatch
    {
        private static bool Prefix(CharacterInteractionHandler __instance)
        {
            if (BridgeController.Instance?.BlocksNativePointerInput != true) return true;
            if (__instance._isPressed || __instance._isDragging) __instance.CancelCurrentInteraction();
            return false;
        }
    }

    [HarmonyPatch(typeof(PlayerLineController), nameof(PlayerLineController.Show))]
    private static class RootMenuPatch
    {
        private static void Prefix()
        {
            ClearSnackMenuPoseHold();
        }

        private static void Postfix()
        {
            LogRootMenuShown();
        }
    }

    [HarmonyPatch(typeof(PlayerLineController), nameof(PlayerLineController.ShowMemoPanelMenu))]
    private static class MemoMenuPatch
    {
        private static void Prefix()
        {
            ClearSnackMenuPoseHold();
            BridgeController.Instance?.OnNativeMenuAction(
                "memo",
                "original memo panel opened",
                "native=true");
        }
    }

    [HarmonyPatch(typeof(PlayerLineController), nameof(PlayerLineController.ShowSnackTimeMenu))]
    private static class SnackMenuPatch
    {
        private static void Prefix()
        {
            _snackMenuPoseHoldUntil = UnityEngine.Time.unscaledTime + 600f;
            BridgeController.Instance?.OnNativeMenuAction(
                "snacktime",
                "original snack-time menu opened",
                "native=true");
        }

        private static void Postfix()
        {
            try
            {
                bool served = DialogueManager.instance?.HasServedSnackTimeToday() ?? false;
                NativeEventLog.Trace("native.snacktime.open", "game", "player",
                    "original snack-time menu opened",
                    "served_today=" + served);
            }
            catch
            {
                // The snack-time diagnostic is optional.
            }
        }
    }

    [HarmonyPatch(typeof(PlayerLineController), "IsSnackTimeAvailable")]
    private static class SnackAvailabilityPatch
    {
        private static void Postfix(ref bool __result)
        {
            NativeEventLog.Trace("native.snacktime.available", "game", "system",
                __result ? "snack-time entry is available" : "snack-time entry is hidden",
                "native_condition=true");
        }
    }

    [HarmonyPatch(typeof(DialogueManager), nameof(DialogueManager.MarkSnackTimeServedToday))]
    private static class SnackServedPatch
    {
        private static void Postfix()
        {
            NativeEventLog.Trace("native.snacktime.served", "game", "system",
                "original snack-time was marked as served today",
                "native_condition=true");
        }
    }

    [HarmonyPatch(typeof(PlayerLineController), nameof(PlayerLineController.ShowMemoriesMenu))]
    private static class MemoriesMenuPatch
    {
        private static void Prefix()
        {
            ClearSnackMenuPoseHold();
            BridgeController.Instance?.OnNativeMenuAction(
                "memories",
                "original memories panel opened",
                "native=true");
        }
    }

    [HarmonyPatch(typeof(DialogueManager), nameof(DialogueManager.StartDialogueByTrigger))]
    private static class NotePoolFailurePatch
    {
        // One Harmony boundary owns both rejection and its fallback receipt;
        // correctness must not depend on ordering two independent prefixes.
        private static bool Prefix(DialogueTriggerType triggerType, out bool __state)
        {
            bool allowed = AISpeechNativeGate.Prefix(triggerType);
            __state = !allowed;
            return allowed;
        }
        private static void Postfix(ref bool __result, DialogueTriggerType triggerType, bool __state)
        {
            if (__state) return; // A protection rejection is not a failed note pool.
            if (__result || (int)triggerType != 35)
                return;
            try
            {
                MethodInfo? find = typeof(DialogueManager).GetMethod(
                    "FindMatchingNodeByWeight",
                    BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                object? node = find?.Invoke(DialogueManager.instance, new object[] { triggerType });
                if (node != null)
                    return;
            }
            catch
            {
                // If the exact pool check cannot be read, fall back to the hook.
            }
            BridgeController.Instance?.OnNativeWriteNoteFailure();
        }
    }

    private static class AISpeechNativeGate
    {
        internal static bool Prefix(DialogueTriggerType triggerType)
        {
            if (!RejectNewNativeTrigger(triggerType))
                return true;

            try
            {
                NativeEventLog.Trace(
                    "native.dialogue.suppressed",
                    "mod",
                    "system",
                    triggerType.ToString(),
                    "AI conversation or tail owns the start boundary; trigger discarded");
            }
            catch
            {
                // Diagnostics must never break the conversation.
            }
            return false;
        }
    }

    [HarmonyPatch(typeof(PlayerLineController), nameof(PlayerLineController.ShowOptions))]
    private static class StringOptionsPatch
    {
        private static void Prefix(Il2CppSystem.Collections.Generic.IReadOnlyList<string> options)
        {
            SetTextOptions(options);
        }
    }

    [HarmonyPatch(typeof(PlayerLineController), nameof(PlayerLineController.ShowEntryOptions))]
    private static class EntryOptionsPatch
    {
        private static void Prefix(Il2CppSystem.Collections.Generic.IReadOnlyList<PlayerLineEntry> entries)
        {
            SetEntryOptions(entries);
        }
    }

    [HarmonyPatch(typeof(PlayerLineController), "SetEntryContents")]
    private static class EntryContentsPatch
    {
        private static void Prefix(Il2CppSystem.Collections.Generic.IReadOnlyList<PlayerLineEntry> entries)
        {
            SetEntryOptions(entries);
        }
    }

    [HarmonyPatch(typeof(PlayerLineController), "SetButtonContents")]
    private static class ButtonContentsPatch
    {
        private static void Prefix(int showCount, Il2CppSystem.Func<int, string> getText)
        {
            SetButtonTexts(showCount, getText);
        }
    }

    [HarmonyPatch(typeof(PlayerLineController), nameof(PlayerLineController.OnButtonClicked))]
    private static class ButtonClickedPatch
    {
        private static void Prefix(int index)
        {
            _choiceEntryId = 0;
            _choiceIndex = -1;
            _choiceText="";
            _choiceActive = true;
            try
            {
                lock (OptionLock)
                {
                    if (index < 0) return;
                    if (_entryOptions != null && index < TryGetCount(_entryOptions))
                        _choiceEntryId = (TryGetListItem(_entryOptions, index) as PlayerLineEntry)?.id ?? 0;
                    else if (_textOptions != null && index < TryGetCount(_textOptions))
                        _choiceIndex = index;
                    else
                    {
                        var roots = PlayerLineController.GetMenuEligibleRootEntries();
                        if (index < TryGetCount(roots) && ButtonTexts.Count == TryGetCount(roots)
                            && ResolveSelectedText(index) == TryGetEntryText(roots, index)?.Trim())
                            _choiceEntryId = (TryGetListItem(roots, index) as PlayerLineEntry)?.id ?? 0;
                    }
                }
            }
            catch { _choiceEntryId = 0; _choiceIndex = -1; }
            if(_choiceEntryId>0||_choiceIndex>=0)_choiceText=ResolveSelectedText(index);
        }
        private static Exception? Finalizer(Exception? __exception)
        { _choiceEntryId = 0; _choiceIndex = -1; _choiceText=""; _choiceActive = false; return __exception; }
        private static void Postfix(int index)
        {
            if(_choiceText.Length>0)BridgeController.Instance?.OnNativeOptionClick(_choiceText,index);
        }
    }

    // Private synchronous source scope, created from an actual clicked native
    // entry/option. Menu opening itself grants no handoff and no future bypass.
    [ThreadStatic] private static string _choiceText="";
    [ThreadStatic] private static bool _choiceActive;
    [ThreadStatic] private static int _choiceEntryId;
    [ThreadStatic] private static int _choiceIndex;
    [ThreadStatic] private static int _continuationDepth;
    private static bool RejectNewNativeTrigger(DialogueTriggerType trigger)
    {
        var bridge = BridgeController.Instance;
        if (bridge?.ProtectsNativeStarts != true) return false;
        // A trigger name (including Event) never grants an exception. Only a
        // synchronous, verified continuation call of an existing real owner does.
        if (_continuationDepth > 0) return false;
        return true;
    }

    [HarmonyPatch]
    private static class NativeContinuationScope
    {
        [HarmonyTargetMethods]
        private static IEnumerable<MethodBase> TargetMethods()
        {
            foreach (var target in new (string Name, Type[] Parameters)[]
            {
                ("AdvanceDialogue", Type.EmptyTypes),
                ("SelectOption", new[] { typeof(int) }),
                ("CompleteCurrentNode", Type.EmptyTypes),
                ("CompleteCurrentNodeAfterState", Type.EmptyTypes),
                ("RunPostStateThenComplete", new[] { typeof(DialogueNode) }),
                ("EndDialogue", new[] { typeof(bool) })
            })
                yield return typeof(DialogueManager).GetMethod(target.Name,
                    BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
                    null, target.Parameters, null)
                    ?? throw new MissingMethodException(typeof(DialogueManager).FullName, target.Name);
        }
        private static void Prefix(out bool __state)
        {
            __state = false;
            try
            {
                var manager = DialogueManager.instance;
                __state = BridgeController.Instance?.IsAiBubbleActive != true
                    && manager?.IsDialogueActive == true && manager.CurrentNode is { id: > 0 };
                if (__state) _continuationDepth++;
            }
            catch { __state = false; }
        }
        private static Exception? Finalizer(Exception? __exception, bool __state)
        { if (__state) _continuationDepth = Math.Max(0, _continuationDepth - 1); return __exception; }
    }

    [HarmonyPatch(typeof(PlayerLineController), "StartMatchingDialogue")]
    private static class ExplicitMatchingChoice
    {
        internal static bool Prefix(PlayerLineEntry entry)
        {
            if (_choiceActive && entry != null && entry.id > 0 && entry.id == _choiceEntryId)
            {
                BridgeController.Instance?.BeforeExplicitNativeChoice();
                _choiceEntryId = 0;
                return true;
            }
            return BridgeController.Instance?.ProtectsNativeStarts != true;
        }
    }
    [HarmonyPatch(typeof(PlayerLineController), "StartMemoryDialogue")]
    private static class ExplicitMemoryChoice
    {
        private static bool Prefix(PlayerLineEntry entry) => ExplicitMatchingChoice.Prefix(entry);
    }
    [HarmonyPatch(typeof(DialogueManager), "SelectOption")]
    private static class ExplicitDialogueOption
    {
        private static void Prefix(int optionIndex)
        {
            try
            {
                var node = DialogueManager.instance?.CurrentNode;
                if (_choiceActive && _choiceIndex == optionIndex && optionIndex >= 0 && node is { id: > 0 }
                    && node.options != null && optionIndex < node.options.Count)
                {
                    BridgeController.Instance?.BeforeExplicitNativeChoice();
                    _choiceIndex = -1;
                }
            }
            catch { /* An unverified click never cancels the AI owner. */ }
        }
    }

    private static void SetTextOptions(Il2CppSystem.Collections.Generic.IReadOnlyList<string> options)
    {
        lock (OptionLock)
        {
            _textOptions = options;
            _entryOptions = null;
            ButtonTexts.Clear();
        }
        LogShownOptions("text", options);
    }

    private static void SetEntryOptions(Il2CppSystem.Collections.Generic.IReadOnlyList<PlayerLineEntry> entries)
    {
        lock (OptionLock)
        {
            _entryOptions = entries;
            _textOptions = null;
            ButtonTexts.Clear();
        }
        LogShownOptions("entry", entries);
    }

    private static void LogShownOptions(string kind, object? options)
    {
        try
        {
            int count = TryGetCount(options);
            var labels = new List<string>();
            for (int i = 0; i < count; i++)
            {
                string? label = kind == "text"
                    ? TryGetString(options as Il2CppSystem.Collections.Generic.IReadOnlyList<string> ?? null, i)
                    : TryGetEntryText(options, i);
                if (!string.IsNullOrWhiteSpace(label))
                    labels.Add(label);
            }
            if (labels.Count > 0)
            {
                NativeEventLog.Trace("player.options", "game", "player",
                    "native options shown", string.Join(" | ", labels));
                BridgeController.Instance?.OnNativeOptionsShown(string.Join(" | ", labels));
            }
        }
        catch
        {
            // Option logging is best effort.
        }
    }

    private static void SetButtonTexts(int showCount, Il2CppSystem.Func<int, string> getText)
    {
        var labels = new List<string>();
        try
        {
            int count = Math.Max(0, showCount);
            for (int i = 0; i < count; i++)
            {
                string? text = InvokeFunc(getText, i);
                if (!string.IsNullOrWhiteSpace(text))
                    labels.Add(text.Trim());
            }
        }
        catch
        {
            // Label capture is best effort; the native list fallback remains.
        }

        lock (OptionLock)
        {
            ButtonTexts.Clear();
            ButtonTexts.AddRange(labels);
        }
    }

    private static void LogRootMenuShown()
    {
        try
        {
            var roots = PlayerLineController.GetMenuEligibleRootEntries();
            int count = TryGetCount(roots);
            var labels = new List<string>();
            for (int i = 0; i < count; i++)
            {
                string? label = TryGetEntryText(roots, i);
                if (!string.IsNullOrWhiteSpace(label))
                    labels.Add(label.Trim());
            }
            string detail = string.Join(" | ", labels);
            NativeEventLog.Trace("native.menu.show", "game", "player",
                "original player-line menu shown",
                "count=" + count + (detail.Length > 0 ? ";entries=" + detail : ""));
            BridgeController.Instance?.OnNativeMenuAction(
                "menu",
                "original player-line menu shown",
                "count=" + count + (detail.Length > 0 ? ";entries=" + detail : ""));
        }
        catch (Exception error)
        {
            NativeEventLog.Trace("native.menu.show_error", "mod", "system", error.Message);
        }
    }

    private static string ResolveSelectedText(int index)
    {
        try
        {
            string? label = null;
            lock (OptionLock)
            {
                if (ButtonTexts.Count > 0 && index >= 0 && index < ButtonTexts.Count)
                    label = ButtonTexts[index];
                else if (_entryOptions != null)
                    label = TryGetEntryText(_entryOptions, index);
                else if (_textOptions != null)
                    label = TryGetString(_textOptions, index);
            }
            if (!string.IsNullOrWhiteSpace(label))
                return label.Trim();

            // Some native menus build button labels after ShowOptions returns;
            // the static root list is the original game menu, not a mod menu.
            try
            {
                var roots = PlayerLineController.GetMenuEligibleRootEntries();
                int rootCount = TryGetCount(roots);
                if (rootCount > 0 && index >= 0 && index < rootCount)
                    return TryGetEntryText(roots, index)?.Trim() ?? "";
            }
            catch
            {
                // Ignore.
            }
        }
        catch
        {
            // Never let option capture affect the native click.
        }
        return "";
    }

    private static string? TryGetEntryText(
        object? entries,
        int index)
    {
        int count = TryGetCount(entries);
        if (count <= 0 || index < 0 || index >= count)
            return null;
        try
        {
            object? entry = TryGetListItem(entries, index);
            if (entry is PlayerLineEntry line)
            {
                try
                {
                    string text = PlayerLineController.GetPlayerLineText(line);
                    if (!string.IsNullOrWhiteSpace(text))
                        return text;
                }
                catch
                {
                    // Fall through to the serialized text property.
                }
                return line.text;
            }
        }
        catch
        {
            // Best effort.
        }
        return null;
    }

    private static object? TryGetListItem(object? list, int index)
    {
        if (list == null)
            return null;
        if (list is Il2CppSystem.Collections.Generic.List<string> textList
            && index >= 0 && index < textList.Count)
            return textList[index];
        if (list is Il2CppSystem.Collections.Generic.IReadOnlyList<string> textList2
            && index >= 0 && index < TryGetCount(textList2))
            return textList2[index];
        if (list is Il2CppSystem.Collections.Generic.List<PlayerLineEntry> entryList
            && index >= 0 && index < entryList.Count)
            return entryList[index];
        if (list is Il2CppSystem.Collections.Generic.IReadOnlyList<PlayerLineEntry> entryList2
            && index >= 0 && index < TryGetCount(entryList2))
            return entryList2[index];
        try
        {
            var method = list.GetType().GetMethod("get_Item")
                ?? list.GetType().GetMethod("get_Item", new[] { typeof(int) });
            return method?.Invoke(list, new object[] { index });
        }
        catch
        {
            return null;
        }
    }

    private static string? TryGetString(
        Il2CppSystem.Collections.Generic.IReadOnlyList<string>? options,
        int index)
    {
        int count = TryGetCount(options);
        if (options == null || count <= 0 || index < 0 || index >= count)
            return null;
        try
        {
            return options[index];
        }
        catch
        {
            return null;
        }
    }

    private static int TryGetCount(object? list)
    {
        if (list == null)
            return -1;
        if (list is Il2CppSystem.Collections.Generic.List<string> textList)
            return textList.Count;
        if (list is Il2CppSystem.Collections.Generic.List<PlayerLineEntry> entryList)
            return entryList.Count;
        try
        {
            int count = Convert.ToInt32(list.GetType().GetProperty("Count")?.GetValue(list));
            return count;
        }
        catch
        {
            try
            {
                return Convert.ToInt32(list.GetType().GetMethod("get_Count")?.Invoke(list, null));
            }
            catch
            {
                return -1;
            }
        }
    }

    private static string? InvokeFunc(Il2CppSystem.Func<int, string> callback, int index)
    {
        try
        {
            return callback.Invoke(index);
        }
        catch
        {
            return null;
        }
    }
}
