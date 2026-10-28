using System;
using System.Collections.Generic;
using System.Reflection;
using UnityEngine;

namespace LilithBridge;

internal static class NativeInteractionSubscriber
{
    private static readonly List<object> KeptDelegates = new();
    private static bool _subscribed;

    public static void Subscribe()
    {
        if (_subscribed)
            return;
        try
        {
            var interaction = UnityEngine.Object.FindObjectOfType<CharacterInteractionHandler>(true);
            if (interaction == null)
                return;

            Type? actionType = Type.GetType("Il2CppSystem.Action`1, Il2Cppmscorlib")
                ?.MakeGenericType(typeof(CharacterHitAreaType));
            Type? supportType = Type.GetType(
                "Il2CppInterop.Runtime.DelegateSupport, Il2CppInterop.Runtime");
            if (actionType == null || supportType == null)
                return;

            MethodInfo? convert = supportType.GetMethod(
                "ConvertDelegate",
                BindingFlags.Public | BindingFlags.Static);
            if (convert == null)
                return;
            convert = convert.MakeGenericMethod(actionType);
            var managed = new Action<CharacterHitAreaType>(OnSingleClickHitArea);
            object? il2Delegate = convert.Invoke(null, new object[] { managed });
            if (il2Delegate == null)
                return;

            MethodInfo? add = typeof(CharacterInteractionHandler).GetMethod(
                "add_OnSingleClickHitArea",
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            if (add == null)
                return;
            add.Invoke(interaction, new[] { il2Delegate });
            KeptDelegates.Add(il2Delegate);

            SubscribeNoArg(interaction, "add_OnDoubleClick", OnDoubleClick);
            SubscribeNoArg(interaction, "add_OnDragStart", OnDragStart);
            SubscribeNoArg(interaction, "add_OnDragEnd", OnDragEnd);
            SubscribeNoArg(interaction, "add_OnHoverEnter", OnHoverEnter);
            SubscribeNoArg(interaction, "add_OnHoverExit", OnHoverExit);
            _subscribed = true;
            NativeEventLog.Trace("meta.interaction_subscribe", "mod", "system",
                "native CharacterInteractionHandler event subscribed");
        }
        catch (Exception error)
        {
            NativeEventLog.Trace("meta.interaction_subscribe_error", "mod", "system",
                "native interaction subscribe failed", error.Message);
        }
    }

    private static void SubscribeNoArg(
        CharacterInteractionHandler interaction,
        string addMethodName,
        System.Action callback)
    {
        Type? actionType = Type.GetType("Il2CppSystem.Action, Il2Cppmscorlib");
        Type? supportType = Type.GetType(
            "Il2CppInterop.Runtime.DelegateSupport, Il2CppInterop.Runtime");
        if (actionType == null || supportType == null)
            return;
        MethodInfo? convert = supportType.GetMethod(
            "ConvertDelegate",
            BindingFlags.Public | BindingFlags.Static);
        if (convert == null)
            return;
        convert = convert.MakeGenericMethod(actionType);
        object? il2Delegate = convert.Invoke(null, new object[] { callback });
        if (il2Delegate == null)
            return;
        MethodInfo? add = typeof(CharacterInteractionHandler).GetMethod(
            addMethodName,
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
        if (add == null)
            return;
        add.Invoke(interaction, new[] { il2Delegate });
        KeptDelegates.Add(il2Delegate);
    }

    private static void OnSingleClickHitArea(CharacterHitAreaType hitArea)
    {
        BridgeController.Instance?.OnNativeHitArea(hitArea, Vector3.zero);
    }

    private static void OnDoubleClick()
    {
        BridgeController.Instance?.OnNativeDoubleClick();
    }

    private static void OnDragStart()
    {
        BridgeController.Instance?.OnNativeDragStart();
    }

    private static void OnDragEnd()
    {
        BridgeController.Instance?.OnNativeDragEnd();
    }

    private static void OnHoverEnter()
    {
        BridgeController.Instance?.OnNativeHoverEnter();
    }

    private static void OnHoverExit()
    {
        BridgeController.Instance?.OnNativeHoverExit();
    }
}
