using System;
using System.Collections;
using System.Collections.Generic;
using Photon.Pun;
using UnityEngine;
using UnityEngine.UI;
using Zorro.UI;

namespace PeakRelay.Client;

/// <summary>
/// In-game server browser, built entirely from code (no prefab/asset dependencies —
/// a game scene reshuffle cannot break it). Injected as a child of MainMenuPageHandler in
/// a Start postfix; navigation uses the two-arg TransistionToPage overload, which needs no
/// entry in the handler's type dictionary. Join flow mirrors the vanilla shape exactly:
/// LoadingScreenHandler.Load(PhotonDriven, …) wrapping PhotonNetwork.JoinRoom(joinKey) +
/// message-queue wait + WaitForCharacterSpawn — no 6-character code checks, no region
/// logic. The join coroutine is executed by the loading-screen singleton (vanilla
/// behavior), so it survives the menu scene unload.
///
/// Zorro UI framework types (UIPage, PageTransistion) are referenced directly — they are
/// the framework the game itself ships; game-code members go through GameAPI and the
/// injection self-disables on unknown game versions, leaving the menu vanilla.
/// </summary>
public sealed class ServerBrowserPage : UIPage, IHaveParentPage
{
    private static ServerBrowserPage? _instance;

    private Text? _status;
    private RectTransform? _rows;
    private GameObject? _passwordPanel;
    private InputField? _passwordInput;
    private ServerEntry? _pendingJoin;
    private bool _joining;
    private readonly List<GameObject> _rowObjects = new();

    // ---------------------------------------------------------------- injection

    /// <summary>
    /// Creates the page under the menu handler (idempotent) and repurposes the Join
    /// button to open it. Called from both Start postfixes; whichever runs first wins.
    /// </summary>
    public static void EnsureInjected(GameObject menuObject)
    {
        if (!GameAPI.BrowserUiAvailable)
            return;
        if (_instance != null)
        {
            RewireJoinButton(menuObject);
            return;
        }

        try
        {
            var handler = menuObject.GetComponentInParent<UIPageHandler>()
                          ?? menuObject.GetComponentInChildren<UIPageHandler>(true);
            if (handler == null)
            {
                RelayPlugin.LogWarning("no UIPageHandler found — browser not injected");
                return;
            }

            var go = new GameObject("PeakRelayServerBrowserPage", typeof(RectTransform));
            go.transform.SetParent(handler.transform, false);
            var rect = (RectTransform)go.transform;
            rect.anchorMin = Vector2.zero;
            rect.anchorMax = Vector2.one;
            rect.offsetMin = Vector2.zero;
            rect.offsetMax = Vector2.zero;
            go.SetActive(false);
            _instance = go.AddComponent<ServerBrowserPage>();

            BuildUi(_instance);
            RewireJoinButton(menuObject);
            RelayPlugin.LogInfo("server browser page injected into the main menu");
        }
        catch (Exception ex)
        {
            RelayPlugin.LogWarning($"server browser injection failed ({ex.Message}) — vanilla menu continues");
        }
    }

    /// <summary>m_joinButton: strip the vanilla code-join listener, open our page instead.</summary>
    private static void RewireJoinButton(GameObject menuObject)
    {
        try
        {
            var mainPage = GameAPI.FindMainPage(menuObject);
            var button = mainPage != null ? GameAPI.GetJoinButton(mainPage) : null;
            if (button == null)
                return;
            button.onClick.RemoveAllListeners();
            button.onClick.AddListener(() =>
            {
                if (_instance != null && GameAPI.TransitionToPage(_instance))
                    _instance.Refresh();
            });
            GameAPI.SetButtonLabel(button, "SERVERS");
        }
        catch (Exception ex)
        {
            RelayPlugin.LogWarning($"join-button rewire failed ({ex.Message})");
        }
    }

    // ---------------------------------------------------------------- lifecycle

    public override void OnPageEnter()
    {
        base.OnPageEnter();
        Refresh();
    }

    /// <summary>Back navigation: the main page + default active-set transition.</summary>
    public (UIPage, PageTransistion) GetParentPage()
    {
        var (page, transition) = GameAPI.ParentPageOf(this);
        return (page ?? this, transition ?? new SetActivePageTransistion());
    }

    private void Refresh()
    {
        StartCoroutine(RefreshRoutine());
    }

    private IEnumerator RefreshRoutine()
    {
        if (_status != null) _status.text = "loading server list…";
        var task = ServerDirectoryClient.FetchAsync(RelayConfig.Host, RelayConfig.Port);
        while (!task.IsCompleted)
            yield return null;
        var servers = task.GetAwaiter().GetResult();

        ClearRows();
        if (servers.Count == 0)
        {
            if (_status != null)
                _status.text = "no servers online — start a dedicated host or check the relay";
            yield break;
        }

        if (_status != null) _status.text = "";
        foreach (var server in servers)
            AddRow(server);
    }

    // ---------------------------------------------------------------- join flow

    private void RequestJoin(ServerEntry server)
    {
        if (_joining)
            return;
        if (server.PasswordRequired)
        {
            _pendingJoin = server;
            if (_passwordInput != null) _passwordInput.text = "";
            if (_passwordPanel != null) _passwordPanel.SetActive(true);
            return;
        }
        StartJoin(server, "");
    }

    private void ConfirmPassword()
    {
        if (_pendingJoin == null)
            return;
        var password = _passwordInput != null ? _passwordInput.text : "";
        var server = _pendingJoin;
        _pendingJoin = null;
        if (_passwordPanel != null) _passwordPanel.SetActive(false);
        StartJoin(server, password);
    }

    private void CancelPassword()
    {
        _pendingJoin = null;
        if (_passwordPanel != null) _passwordPanel.SetActive(false);
    }

    /// <summary>
    /// Hands the join coroutine to the loading-screen singleton (the vanilla pattern —
    /// MainMenuJoinRoomPage.JoinRoomInternal), so the wait survives the menu unload.
    /// Falls back to a plain coroutine when the loading screen is unavailable.
    /// </summary>
    private void StartJoin(ServerEntry server, string password)
    {
        if (_joining)
            return;
        _joining = true;
        var driven = GameAPI.PhotonDriven();
        var process = JoinProcess(server, password);
        if (driven == null || !GameAPI.LoadWithLoadingScreen(driven, null, process))
        {
            StartCoroutine(process);
        }
    }

    /// <summary>
    /// The plan's custom join, vanilla-shaped: no code-format checks, no region logic.
    /// Wait order matches MainMenuJoinRoomPage.JoinRoomAndWaitForSpawn. Note: C# forbids
    /// yield inside try/catch, so the join work runs in the inner iterator and the guard
    /// wraps only the non-yielding parts.
    /// </summary>
    private IEnumerator JoinProcess(ServerEntry server, string password)
    {
        GameAPI.SetEnteringRoom(true);
        GameAPI.UpdateNickname();
        PhotonNetwork.JoinRoom(server.JoinKey);

        yield return new WaitForSeconds(1f);
        int guard = 0;
        while (GameAPI.GetEnteringRoom() && !PhotonNetwork.IsMessageQueueRunning && guard++ < 10000)
            yield return null;

        if (!PhotonNetwork.InRoom)
        {
            GameAPI.SetEnteringRoom(false);
            _joining = false;
            yield break; // loading screen closes; PUN's own failure modal may show
        }

        var waitForSpawn = GameAPI.WaitForCharacterSpawn();
        if (waitForSpawn != null)
            yield return waitForSpawn;
        GameAPI.SetEnteringRoom(false);
        _joining = false;
    }

    // ---------------------------------------------------------------- UI construction

    private static void BuildUi(ServerBrowserPage page)
    {
        var root = (RectTransform)page.transform;

        var bg = NewRect("Background", root);
        Stretch(bg);
        var bgImage = bg.gameObject.AddComponent<Image>();
        bgImage.color = new Color(0.06f, 0.08f, 0.11f, 0.97f);

        var title = NewRect("Title", root);
        Anchor(title, new Vector2(0.05f, 0.85f), new Vector2(0.7f, 0.93f));
        AddText(title, "PEAKRELAY SERVERS", 28, TextAnchor.MiddleLeft);

        var refresh = NewRect("Refresh", root);
        Anchor(refresh, new Vector2(0.78f, 0.855f), new Vector2(0.95f, 0.925f));
        StyleButton(refresh, "REFRESH", page.Refresh);

        var status = NewRect("Status", root);
        Anchor(status, new Vector2(0.05f, 0.115f), new Vector2(0.95f, 0.16f));
        page._status = AddText(status, "", 16, TextAnchor.MiddleLeft);

        var listBg = NewRect("ListBackground", root);
        Anchor(listBg, new Vector2(0.05f, 0.17f), new Vector2(0.95f, 0.84f));
        var listImage = listBg.gameObject.AddComponent<Image>();
        listImage.color = new Color(0.1f, 0.13f, 0.18f, 0.9f);

        var viewport = NewRect("Viewport", listBg);
        Anchor(viewport, new Vector2(0f, 0f), new Vector2(1f, 1f));
        viewport.gameObject.AddComponent<RectMask2D>();

        var scroll = viewport.gameObject.AddComponent<ScrollRect>();
        scroll.horizontal = false;
        scroll.movementType = ScrollRect.MovementType.Clamped;
        scroll.scrollSensitivity = 24;

        var content = NewRect("Rows", viewport);
        Anchor(content, new Vector2(0f, 1f), new Vector2(1f, 1f));
        content.pivot = new Vector2(0.5f, 1f);
        var layout = content.gameObject.AddComponent<VerticalLayoutGroup>();
        layout.childForceExpandHeight = false;
        layout.childForceExpandWidth = true;
        layout.spacing = 4f;
        layout.padding = new RectOffset(8, 8, 8, 8);
        var fitter = content.gameObject.AddComponent<ContentSizeFitter>();
        fitter.verticalFit = ContentSizeFitter.FitMode.PreferredSize;

        scroll.content = content;
        scroll.viewport = viewport;
        page._rows = content;

        BuildPasswordPanel(page, root);
    }

    private static void BuildPasswordPanel(ServerBrowserPage page, RectTransform root)
    {
        var panel = NewRect("PasswordPanel", root);
        Anchor(panel, new Vector2(0.25f, 0.35f), new Vector2(0.75f, 0.65f));
        var image = panel.gameObject.AddComponent<Image>();
        image.color = new Color(0.13f, 0.16f, 0.22f, 0.98f);
        page._passwordPanel = panel.gameObject;
        panel.gameObject.SetActive(false);

        var label = NewRect("Label", panel);
        Anchor(label, new Vector2(0.05f, 0.62f), new Vector2(0.95f, 0.9f));
        AddText(label, "This server is password-protected", 18, TextAnchor.MiddleCenter);

        var input = NewRect("Input", panel);
        Anchor(input, new Vector2(0.05f, 0.4f), new Vector2(0.95f, 0.6f));
        var inputImage = input.gameObject.AddComponent<Image>();
        inputImage.color = new Color(0, 0, 0, 0.6f);
        var field = input.gameObject.AddComponent<InputField>();
        var fieldText = AddText(input, "", 16, TextAnchor.MiddleLeft);
        fieldText.color = Color.white;
        field.textComponent = fieldText;
        field.characterLimit = 32;
        field.contentType = InputField.ContentType.Password;
        page._passwordInput = field;

        var confirm = NewRect("Confirm", panel);
        Anchor(confirm, new Vector2(0.05f, 0.08f), new Vector2(0.47f, 0.32f));
        StyleButton(confirm, "JOIN", page.ConfirmPassword);

        var cancel = NewRect("Cancel", panel);
        Anchor(cancel, new Vector2(0.53f, 0.08f), new Vector2(0.95f, 0.32f));
        StyleButton(cancel, "CANCEL", page.CancelPassword);
    }

    private void AddRow(ServerEntry server)
    {
        var row = NewRect($"Row_{_rowObjects.Count}", _rows!);
        var layoutElement = row.gameObject.AddComponent<LayoutElement>();
        layoutElement.minHeight = 42f;
        layoutElement.preferredHeight = 42f;

        var rowImage = row.gameObject.AddComponent<Image>();
        rowImage.color = new Color(0.14f, 0.18f, 0.25f, 0.9f);

        var label = NewRect("Label", row!);
        Anchor(label, new Vector2(0.02f, 0f), new Vector2(0.98f, 1f));
        var lockIcon = server.PasswordRequired ? "  [LOCKED]" : "";
        AddText(label,
            $"  {server.DisplayName}  ({server.JoinKey}){lockIcon}      {server.Players}/{server.Max}      {server.Mode}",
            17, TextAnchor.MiddleLeft);

        var button = row.gameObject.AddComponent<Button>();
        var entry = server;
        button.onClick.AddListener(() => RequestJoin(entry));
        _rowObjects.Add(row.gameObject);
    }

    private void ClearRows()
    {
        foreach (var row in _rowObjects)
            if (row != null)
                Destroy(row);
        _rowObjects.Clear();
    }

    // ---------------------------------------------------------------- uGUI helpers

    private static RectTransform NewRect(string name, RectTransform parent)
    {
        var go = new GameObject(name, typeof(RectTransform));
        var rect = (RectTransform)go.transform;
        rect.SetParent(parent, false);
        return rect;
    }

    private static void Stretch(RectTransform rect)
    {
        rect.anchorMin = Vector2.zero;
        rect.anchorMax = Vector2.one;
        rect.offsetMin = Vector2.zero;
        rect.offsetMax = Vector2.zero;
    }

    private static void Anchor(RectTransform rect, Vector2 min, Vector2 max)
    {
        rect.anchorMin = min;
        rect.anchorMax = max;
        rect.offsetMin = Vector2.zero;
        rect.offsetMax = Vector2.zero;
    }

    private static readonly List<string> FontNames = new()
    {
        "LegacyRuntime.ttf", // Unity 2022.2+ builtin
        "Arial.ttf",         // older builtin
    };

    private static Font? _font;

    private static Font BuiltinFont()
    {
        if (_font != null)
            return _font;
        foreach (var name in FontNames)
        {
            try
            {
                _font = Resources.GetBuiltinResource<Font>(name);
                if (_font != null)
                    return _font;
            }
            catch
            {
                // try next
            }
        }
        _font = Font.CreateDynamicFontFromOSFont("Arial", 14);
        return _font;
    }

    private static Text AddText(RectTransform parent, string content, int size, TextAnchor anchor)
    {
        var text = parent.gameObject.AddComponent<Text>();
        text.font = BuiltinFont();
        text.text = content;
        text.fontSize = size;
        text.alignment = anchor;
        text.color = new Color(0.87f, 0.9f, 0.95f, 1f);
        text.horizontalOverflow = HorizontalWrapMode.Overflow;
        text.verticalOverflow = VerticalWrapMode.Overflow;
        return text;
    }

    private static void StyleButton(RectTransform rect, string label, Action onClick)
    {
        var image = rect.gameObject.AddComponent<Image>();
        image.color = new Color(0.22f, 0.45f, 0.3f, 0.95f);
        var button = rect.gameObject.AddComponent<Button>();
        var colors = button.colors;
        colors.highlightedColor = new Color(0.3f, 0.6f, 0.4f, 1f);
        colors.pressedColor = new Color(0.18f, 0.35f, 0.24f, 1f);
        button.colors = colors;
        AddText(rect, label, 16, TextAnchor.MiddleCenter);
        button.onClick.AddListener(() => onClick());
    }
}
