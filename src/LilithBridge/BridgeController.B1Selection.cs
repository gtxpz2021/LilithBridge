using System;
using UnityEngine;
namespace LilithBridge;

internal sealed partial class BridgeController
{
    private B1SelectionSession? _b1SelectionSession;
    private readonly NativeB1SelectionWindow _b1SelectionWindow = new();
    private void OpenB1Selection()
    {
        if (!_listening || _historyWindow.IsOpen || _sharedWindow.OwnsKeyboard) return;
        try
        {
            // Lazy construction: opening this local entry neither enables B1 nor calls a model.
            _b1SelectionSession ??= new B1SelectionSession(_memory, () => _config);
            _inputField.SuspendForHistory(true);
            _b1SelectionWindow.Show(transform, _inputField.StyleText, _b1SelectionSession, CloseB1Selection);
        }
        catch (Exception error)
        {
            _b1SelectionWindow.Hide();
            _inputField.SuspendForHistory(false);
            _ignoreEnterUntil = Time.unscaledTime + .35f;
            Plugin.PluginLog.LogWarning("B1 selection window unavailable: " + error.GetType().Name);
        }
    }
    private void CloseB1Selection()
    {
        // Hide/Close retain reason and any uncertain operation identity. They never write a selection.
        _b1SelectionWindow.Hide();
        _inputField.SuspendForHistory(false);
        _ignoreEnterUntil = Time.unscaledTime + .35f;
    }
}
