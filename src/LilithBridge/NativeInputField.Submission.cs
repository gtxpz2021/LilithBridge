using System;
using UnityEngine;
namespace LilithBridge;
internal sealed partial class NativeInputField
{
    public bool SubmitCurrent()
    {
        if (_input == null || _root == null || !_root.activeSelf || UiConsumesKeyboard
            || !string.IsNullOrEmpty(Input.compositionString))
            return false;
        string value = _input.text;
        Hide();
        Action<string>? callback = _onSubmit;
        _onSubmit = null;
        _onCancel = null;
        callback?.Invoke(value);
        return true;
    }

}
