using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;

namespace PeakRelay.Client;

/// <summary>
/// Blocks player input while the chat input box is open, using the game's OWN gate:
/// Character.CanDoInput() returns false when GUIManager.instance.windowBlockingInput is
/// true — the same mechanism the pause menu and every MenuWindow use (the property is
/// recalculated every frame in GUIManager.UpdateWindowStatus from MenuWindow.AllActiveWindows).
///
/// The postfix runs after each recalculation and forces the flag back to true while the
/// chat box wants the keyboard, so movement/look/actions all read zeroed input exactly
/// like they do in the pause menu. When the chat box closes, the vanilla value flows
/// through untouched.
/// </summary>
[HarmonyPatch(typeof(GUIManager), nameof(GUIManager.UpdateWindowStatus))]
internal static class ChatInputBlockPatch
{
    /// <summary>Set by ChatHud while its input box holds keyboard focus.</summary>
    internal static bool ChatWantsKeyboard;

    private static readonly FieldInfo BlockingField = AccessTools.Field(typeof(GUIManager), "<windowBlockingInput>k__BackingField");

    [HarmonyPostfix]
    private static void BlockInputWhileChatting()
    {
        if (!ChatWantsKeyboard)
            return;
        BlockingField?.SetValue(GUIManager.instance, true);
    }
}
