using System;
using System.Collections;
using System.Reflection;
using HarmonyLib;
using Photon.Pun;
using UnityEngine;
using Zorro.UI;

namespace PeakRelay.Client;

/// <summary>
/// Single gate between the mod and game-version-specific members. Types we subclass or
/// that Harmony must name statically (UIPage, MainMenuPageHandler) are referenced via
/// AccessTools/cached reflection; direct references are limited to stable Photon API.
/// A missing member means "unknown game version": the browser self-disables (logs once,
/// skips injection) and the game stays fully vanilla.
/// </summary>
public static class GameAPI
{
    /// <summary>True when every member the browser needs resolved against this game build.</summary>
    public static bool BrowserUiAvailable { get; private set; }

    private static bool _initialized;

    // --- cached members ---
    private static MethodInfo? _pageHandlerStart;
    private static MethodInfo? _mainPageStart;
    private static FieldInfo? _joinButtonField;
    private static MethodInfo? _transitionTwoArg;       // TransistionToPage(UIPage, PageTransistion)
    private static PropertyInfo? _singletonInstance;
    private static MethodInfo? _loadingScreenLoad;
    private static MethodInfo? _waitForCharacterSpawn;
    private static MethodInfo? _updateNickname;
    private static PropertyInfo? _enteringRoom;

    // game types (names only — resolved lazily through AccessTools)
    private const string PageHandlerName = "MainMenuPageHandler";
    private const string MainPageName = "MainMenuMainPage";
    private const string LoadingHandlerName = "LoadingScreenHandler";
    private const string PhotonUtilityName = "Portningsbolaget.Photon.PhotonUtility";

    public static void Initialize(Harmony harmony)
    {
        if (_initialized)
            return;
        _initialized = true;

        var pageHandler = AccessTools.TypeByName(PageHandlerName);
        var mainPage = AccessTools.TypeByName(MainPageName);
        var loadingHandler = AccessTools.TypeByName(LoadingHandlerName);
        var uiPageHandler = AccessTools.TypeByName("Zorro.UI.UIPageHandler");
        var pageBase = AccessTools.TypeByName("Zorro.UI.UIPage");
        var pageTransition = AccessTools.TypeByName("Zorro.UI.PageTransistion");
        var setActiveTransition = AccessTools.TypeByName("Zorro.UI.SetActivePageTransistion");
        var photonUtility = AccessTools.TypeByName(PhotonUtilityName);
        var networkingUtilities = AccessTools.TypeByName("Peak.Network.NetworkingUtilities");

        _pageHandlerStart = AccessTools.Method(pageHandler, "Start");
        _mainPageStart = AccessTools.Method(mainPage, "Start");
        _joinButtonField = AccessTools.Field(mainPage, "m_joinButton");
        _transitionTwoArg = uiPageHandler != null && pageBase != null && pageTransition != null
            ? AccessTools.Method(uiPageHandler, "TransistionToPage", new[] { pageBase, pageTransition }) : null;
        _singletonInstance = loadingHandler != null
            ? AccessTools.Property(loadingHandler, "Instance") : null;
        _loadingScreenLoad = loadingHandler != null
            ? AccessTools.Method(loadingHandler, "Load") : null;
        _waitForCharacterSpawn = loadingHandler != null
            ? AccessTools.Method(loadingHandler, "WaitForCharacterSpawn") : null;
        _updateNickname = photonUtility != null
            ? AccessTools.Method(photonUtility, "UpdateNickname") : null;
        _enteringRoom = networkingUtilities != null
            ? AccessTools.Property(networkingUtilities, "EnteringRoom") : null;

        BrowserUiAvailable = pageHandler != null
                             && mainPage != null
                             && _pageHandlerStart != null
                             && _mainPageStart != null
                             && _joinButtonField != null
                             && uiPageHandler != null
                             && pageBase != null
                             && pageTransition != null
                             && setActiveTransition != null
                             && loadingHandler != null
                             && _singletonInstance != null
                             && _loadingScreenLoad != null
                             && _waitForCharacterSpawn != null;

        if (BrowserUiAvailable)
        {
            // observer postfixes only: run after the game's own code, never change it
            harmony.Patch(_pageHandlerStart,
                postfix: new HarmonyMethod(typeof(GameAPI), nameof(PageHandlerStartPostfix)));
            harmony.Patch(_mainPageStart,
                postfix: new HarmonyMethod(typeof(GameAPI), nameof(MainPageStartPostfix)));
        }
        else
        {
            var missing = new (string, bool)[]
            {
                ("MainMenuPageHandler", pageHandler != null),
                ("MainMenuMainPage", mainPage != null),
                ("MainMenuPageHandler.Start", _pageHandlerStart != null),
                ("MainMenuMainPage.Start", _mainPageStart != null),
                ("m_joinButton", _joinButtonField != null),
                ("Zorro.UI.UIPageHandler", uiPageHandler != null),
                ("Zorro.UI.UIPage", pageBase != null),
                ("Zorro.UI.PageTransistion", pageTransition != null),
                ("Zorro.UI.SetActivePageTransistion", setActiveTransition != null),
                ("LoadingScreenHandler", loadingHandler != null),
                ("LoadingScreenHandler.Instance", _singletonInstance != null),
                ("LoadingScreenHandler.Load", _loadingScreenLoad != null),
                ("LoadingScreenHandler.WaitForCharacterSpawn", _waitForCharacterSpawn != null),
            };
            foreach (var (name, ok) in missing)
                if (!ok)
                    RelayPlugin.LogWarning($"GameAPI: '{name}' not found (game update?)");
            RelayPlugin.LogWarning("GameAPI: server browser unavailable — game stays fully vanilla");
        }
    }

    // ---------------------------------------------------------------- patches

    private static void PageHandlerStartPostfix(MonoBehaviour __instance)
    {
        try
        {
            ServerBrowserPage.EnsureInjected(__instance.gameObject);
        }
        catch (Exception ex)
        {
            RelayPlugin.LogWarning($"page injection failed ({ex.Message}) — vanilla menu continues");
        }
    }

    private static void MainPageStartPostfix(MonoBehaviour __instance)
    {
        try
        {
            ServerBrowserPage.EnsureInjected(__instance.gameObject);
        }
        catch (Exception ex)
        {
            RelayPlugin.LogWarning($"main-page injection failed ({ex.Message})");
        }
    }

    // ---------------------------------------------------------------- browser support

    /// <summary>The main page's Join button (vanilla field), or null.</summary>
    public static UnityEngine.UI.Button? GetJoinButton(Component mainPage)
    {
        if (_joinButtonField?.GetValue(mainPage) is not Component buttonComponent)
            return null;
        return buttonComponent.GetComponent<UnityEngine.UI.Button>();
    }

    /// <summary>Find the main-page instance under a menu object.</summary>
    public static Component? FindMainPage(GameObject menuObject)
    {
        var mainPage = AccessTools.TypeByName(MainPageName);
        return mainPage != null ? menuObject.GetComponentInChildren(mainPage, true) : null;
    }

    /// <summary>Transition the menu handler to a page instance (two-arg overload).</summary>
    public static bool TransitionToPage(Component page)
    {
        if (_transitionTwoArg == null)
            return false;
        var handler = page.GetComponentInParent(AccessTools.TypeByName("Zorro.UI.UIPageHandler"));
        if (handler == null)
            return false;
        var transition = AccessTools.TypeByName("Zorro.UI.SetActivePageTransistion");
        if (transition == null)
            return false;
        try
        {
            _transitionTwoArg.Invoke(handler, new[] { page, Activator.CreateInstance(transition) });
            return true;
        }
        catch (Exception ex)
        {
            RelayPlugin.LogWarning($"TransitionToPage failed: {ex.Message}");
            return false;
        }
    }

    /// <summary>GetParentPage return for IHaveParentPage implementations.</summary>
    public static (UIPage?, PageTransistion?) ParentPageOf(Component page)
    {
        var mainPage = page.GetComponentInParent(AccessTools.TypeByName("Zorro.UI.UIPageHandler"))
            ?.GetComponentInChildren(AccessTools.TypeByName(MainPageName), true);
        var transition = AccessTools.TypeByName("Zorro.UI.SetActivePageTransistion");
        return (mainPage as UIPage, transition != null ? (PageTransistion)Activator.CreateInstance(transition) : null);
    }

    /// <summary>LoadingScreen.LoadingScreenType.PhotonDriven (boxed enum) or null.</summary>
    public static object? PhotonDriven()
    {
        var loadingScreen = AccessTools.TypeByName("LoadingScreen");
        var nested = loadingScreen != null ? AccessTools.Inner(loadingScreen, "LoadingScreenType") : null;
        if (nested == null)
            return null;
        try
        {
            return Enum.Parse(nested, "PhotonDriven");
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// LoadingScreenHandler.Load(PhotonDriven, runAfter, processes) — the exact vanilla
    /// join wrapper. Returns false (and does nothing) when unavailable.
    /// </summary>
    public static bool LoadWithLoadingScreen(object loadingType, Action? runAfter, params IEnumerator[] processes)
    {
        if (_singletonInstance == null || _loadingScreenLoad == null)
            return false;
        var instance = _singletonInstance.GetValue(null);
        if (instance == null)
            return false;
        _loadingScreenLoad.Invoke(instance, new[] { loadingType, runAfter, processes });
        return true;
    }

    /// <summary>LoadingScreenHandler.WaitForCharacterSpawn() coroutine, if available.</summary>
    public static IEnumerator? WaitForCharacterSpawn()
    {
        return _waitForCharacterSpawn?.Invoke(null, new object[] { 200f }) as IEnumerator;
    }

    public static void UpdateNickname()
    {
        try { _updateNickname?.Invoke(null, null); }
        catch (Exception ex) { RelayPlugin.LogWarning($"UpdateNickname failed: {ex.Message}"); }
    }

    public static bool GetEnteringRoom()
    {
        try { return _enteringRoom != null && _enteringRoom.GetValue(null) is true; }
        catch { return false; }
    }

    public static void SetEnteringRoom(bool value)
    {
        try { _enteringRoom?.SetValue(null, value); }
        catch { /* best effort */ }
    }

    /// <summary>Replaces a Button's top-level label text (first Text child), if present.</summary>
    public static void SetButtonLabel(UnityEngine.UI.Button button, string label)
    {
        var text = button.GetComponentInChildren<UnityEngine.UI.Text>(true);
        if (text != null)
            text.text = label;
    }
}
