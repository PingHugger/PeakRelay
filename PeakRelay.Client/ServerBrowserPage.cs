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
            // Size depends on the host: anchors only work under a RectTransform. Under a
            // plain Transform the stretched page would be 0x0 px — the live "blank page"
            // symptom — so give it an explicit screen-size rect there instead.
            if (handler.transform is RectTransform)
            {
                rect.anchorMin = Vector2.zero;
                rect.anchorMax = Vector2.one;
                rect.offsetMin = Vector2.zero;
                rect.offsetMax = Vector2.zero;
            }
            else
            {
                rect.anchorMin = new Vector2(0.5f, 0.5f);
                rect.anchorMax = new Vector2(0.5f, 0.5f);
                rect.sizeDelta = new Vector2(Screen.width, Screen.height);
                rect.anchoredPosition = Vector2.zero;
            }
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
                if (_instance == null)
                    return;
                if (GameAPI.TransitionToPage(_instance))
                {
                    // belt and braces: the transition's SetActive should cover this, but a
                    // page that is not activeInHierarchy draws nothing regardless of cause
                    _instance.gameObject.SetActive(true);
                    _instance.Refresh();
                }
            });
            GameAPI.SetButtonLabel(button, "SERVERS");
        }
        catch (Exception ex)
        {
            RelayPlugin.LogWarning($"join-button rewire failed ({ex.Message})");
        }
    }

    // ---------------------------------------------------------------- lifecycle

    /// <summary>Back navigation to the main page (the reverse of the join-button rewire).</summary>
    private void GoBack()
    {
        var mainPage = GameAPI.ParentPageOf(this).Item1;
        if (mainPage == null || !GameAPI.TransitionToPage(mainPage))
            RelayPlugin.LogWarning("back navigation failed — main page not found");
    }

    public override void OnPageEnter()
    {
        base.OnPageEnter();
        DumpHierarchy();
        Refresh();
    }

    /// <summary>
    /// Ground truth for "blank page" reports: one log line with the page's rect, active
    /// state and the ancestor chain (type/canvas/active per level) — that pinpoints which
    /// layer (size, canvas, activation) is eating the render.
    /// </summary>
    private void DumpHierarchy()
    {
        try
        {
            var rect = (RectTransform)transform;
            var line = new System.Text.StringBuilder($"browser page enter: rect={rect.rect.size} " +
                                                     "active=" + gameObject.activeInHierarchy);
            var t = transform.parent;
            for (var depth = 0; t != null && depth < 8; depth++, t = t.parent)
                line.Append($" <- {t.name}[{t.GetType().Name}]" +
                            $"(canvas={t.GetComponent<Canvas>() != null}, on={t.gameObject.activeInHierarchy})");
            RelayPlugin.LogInfo(line.ToString());
        }
        catch (Exception ex)
        {
            RelayPlugin.LogWarning($"hierarchy dump failed: {ex.Message}");
        }
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

        // Render guarantee: the menu handler is a plain Transform with NO canvas above it
        // (live-run hierarchy dump: MainMenu[Transform], canvas=False). A Canvas added here
        // therefore becomes a ROOT canvas — which defaults to World Space at the origin,
        // i.e. a giant billboard standing in the 3D scene. Force Screen Space Overlay so it
        // always draws full-screen on top; the GraphicRaycaster lets rows receive clicks.
        var canvas = page.gameObject.AddComponent<Canvas>();
        canvas.renderMode = RenderMode.ScreenSpaceOverlay;
        canvas.sortingOrder = 500;
        page.gameObject.AddComponent<GraphicRaycaster>();

        var bg = NewRect("Background", root);
        Stretch(bg);
        var bgImage = bg.gameObject.AddComponent<Image>();
        bgImage.color = new Color(0.06f, 0.08f, 0.11f, 0.97f);

        // header: title on its own top strip, buttons row below it (BACK/REFRESH used to
        // overlap the title when sharing one band)
        var title = NewRect("Title", root);
        Anchor(title, new Vector2(0.05f, 0.925f), new Vector2(0.7f, 0.99f));
        AddText(title, "PEAKRELAY SERVERS", 26, TextAnchor.MiddleLeft);

        var back = NewRect("Back", root);
        Anchor(back, new Vector2(0.05f, 0.83f), new Vector2(0.2f, 0.905f));
        StyleButton(back, "← BACK", page.GoBack);

        var refresh = NewRect("Refresh", root);
        Anchor(refresh, new Vector2(0.78f, 0.83f), new Vector2(0.95f, 0.905f));
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
        // the text must live on a CHILD rect: a GameObject can hold only one Graphic,
        // so Text directly on the Image-bearing input rect fails to add (null -> NRE)
        var fieldTextRect = NewRect("Text", input);
        Stretch(fieldTextRect);
        var fieldText = AddText(fieldTextRect, "", 16, TextAnchor.MiddleLeft);
        fieldText.rectTransform.offsetMin = new Vector2(8, 0);
        fieldText.rectTransform.offsetMax = new Vector2(-8, 0);
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
        // label on a child rect: a GameObject can hold only one Graphic, so Text directly
        // on the Image-bearing button rect fails to add (null -> NRE in BuildUi)
        var labelRect = NewRect("Label", rect);
        Stretch(labelRect);
        AddText(labelRect, label, 16, TextAnchor.MiddleCenter);
        button.onClick.AddListener(() => onClick());
    }
}
