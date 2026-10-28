using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using UnityEditor;
using UnityEditor.UIElements;
using UnityEngine;
using UnityEngine.UIElements;

namespace Zetta3D.Editor
{
    /// <summary>Small builders so every panel looks like the web Studio without repeating styling.</summary>
    public static class ZettaUI
    {
        const string StylePath = "Packages/com.zetta3d.unity/Editor/UI/Zetta.uss";
        static StyleSheet _style;

        public static StyleSheet Style
        {
            get
            {
                if (_style != null) return _style;
                _style = AssetDatabase.LoadAssetAtPath<StyleSheet>(StylePath);
                if (_style == null)
                {
                    // Installed somewhere unusual (embedded under Assets/, renamed folder): find it by name.
                    var guid = AssetDatabase.FindAssets("Zetta t:StyleSheet").FirstOrDefault();
                    if (guid != null) _style = AssetDatabase.LoadAssetAtPath<StyleSheet>(AssetDatabase.GUIDToAssetPath(guid));
                }
                return _style;
            }
        }

        public static VisualElement Root(VisualElement host)
        {
            if (Style != null && !host.styleSheets.Contains(Style)) host.styleSheets.Add(Style);
            host.AddToClassList("z-root");
            return host;
        }

        public static T Classes<T>(this T element, params string[] classes) where T : VisualElement
        {
            foreach (var c in classes) if (!string.IsNullOrEmpty(c)) element.AddToClassList(c);
            return element;
        }

        public static T Children<T>(this T element, params VisualElement[] children) where T : VisualElement
        {
            foreach (var child in children) if (child != null) element.Add(child);
            return element;
        }

        public static T Visible<T>(this T element, bool visible) where T : VisualElement
        {
            element.style.display = visible ? DisplayStyle.Flex : DisplayStyle.None;
            return element;
        }

        public static Label Text(string text, params string[] classes) => new Label(text).Classes(classes.Length == 0 ? new[] { "z-text" } : classes);
        public static Label Title(string text) => new Label(text).Classes("z-section-title");
        public static Label FieldLabel(string text) => new Label(text).Classes("z-label");
        public static Label Hint(string text) => new Label(text).Classes("z-hint");
        public static VisualElement Row(params VisualElement[] children) => new VisualElement().Classes("z-row").Children(children);
        public static VisualElement Spacer() => new VisualElement().Classes("z-spacer");
        public static VisualElement Card(params VisualElement[] children) => new VisualElement().Classes("z-card").Children(children);

        public static Button Button(string text, Action onClick, params string[] classes)
        {
            var button = new Button(onClick) { text = text };
            button.AddToClassList("z-button");
            foreach (var c in classes) button.AddToClassList(c);
            return button;
        }

        public static Label Chip(string text, string variant = null) => new Label(text).Classes("z-chip", variant == null ? null : "z-chip--" + variant);

        public static TextField TextArea(string placeholder, string value = "", int maxLength = 5000)
        {
            var field = new TextField { multiline = true, value = value, maxLength = maxLength };
            field.AddToClassList("z-textarea");
            SetPlaceholder(field, placeholder);
            return field;
        }

        public static TextField TextInput(string placeholder, string value = "", int maxLength = 255)
        {
            var field = new TextField { value = value, maxLength = maxLength };
            field.AddToClassList("z-field");
            SetPlaceholder(field, placeholder);
            return field;
        }

        /// <summary>Placeholder text; UI Toolkit only gained native support in 2023.1.</summary>
        public static void SetPlaceholder(TextField field, string placeholder)
        {
#if UNITY_2023_1_OR_NEWER
            field.textEdition.placeholder = placeholder;
            field.textEdition.hidePlaceholderOnFocus = true;
#else
            field.tooltip = placeholder;
#endif
        }

        public static Toggle Toggle(string label, bool value, Action<bool> onChange, string tooltip = null)
        {
            // `text` (checkbox first, then the words) rather than a field label: the stylesheet styles
            // .unity-toggle__text, and a field label's fixed width clips longer option names.
            var toggle = new Toggle { text = label, value = value, tooltip = tooltip };
            toggle.AddToClassList("z-toggle");
            toggle.RegisterValueChangedCallback(e => onChange?.Invoke(e.newValue));
            return toggle;
        }

        public static PopupField<string> Dropdown(string label, List<string> values, List<string> labels, string current, Action<string> onChange)
        {
            var index = Math.Max(0, values.IndexOf(current));
            var field = new PopupField<string>(label, values, index,
                v => labels[Math.Max(0, values.IndexOf(v))],
                v => labels[Math.Max(0, values.IndexOf(v))]);
            field.AddToClassList("z-field");
            field.RegisterValueChangedCallback(e => onChange?.Invoke(e.newValue));
            return field;
        }

        /// <summary>A row of mutually exclusive options, the Studio's segmented control.</summary>
        public class Segmented : VisualElement
        {
            readonly Dictionary<string, Button> _buttons = new Dictionary<string, Button>();
            public string Value { get; private set; }
            public event Action<string> Changed;

            public Segmented(IList<string> values, IList<string> labels, string current)
            {
                AddToClassList("z-segmented");
                for (var i = 0; i < values.Count; i++)
                {
                    var value = values[i];
                    var button = new Button(() => Set(value, true)) { text = labels[i] };
                    button.AddToClassList("z-segment");
                    _buttons[value] = button;
                    Add(button);
                }
                Set(current, false);
            }

            public void Set(string value, bool notify)
            {
                if (!_buttons.ContainsKey(value)) value = _buttons.Keys.First();
                Value = value;
                foreach (var pair in _buttons) pair.Value.EnableInClassList("z-segment--active", pair.Key == value);
                if (notify) Changed?.Invoke(value);
            }

            public void SetOptionEnabled(string value, bool enabled, string tooltip = null)
            {
                if (!_buttons.TryGetValue(value, out var button)) return;
                button.SetEnabled(enabled);
                button.tooltip = tooltip ?? "";
            }
        }

        public static void Toast(EditorWindow window, string message) =>
            window?.ShowNotification(new GUIContent(message), 2.5);

        // ─── Images ─────────────────────────────────────────────────────────────────

        static readonly Dictionary<string, Texture2D> Textures = new Dictionary<string, Texture2D>();

        public static Texture2D TextureFrom(byte[] bytes)
        {
            var texture = new Texture2D(2, 2, TextureFormat.RGBA32, false) { hideFlags = HideFlags.HideAndDontSave };
            texture.LoadImage(bytes);
            return texture;
        }

        /// <summary>Loads a remote image into an element's background, cached by key (not by signed URL).</summary>
        public static async void LoadInto(VisualElement element, string url, string cacheKey = null)
        {
            if (string.IsNullOrEmpty(url)) return;
            cacheKey ??= url.Split('?')[0];
            if (Textures.TryGetValue(cacheKey, out var cached) && cached != null)
            {
                element.style.backgroundImage = new StyleBackground(cached);
                return;
            }
            try
            {
                var bytes = await ZettaHttp.DownloadAsync(url);
                var texture = TextureFrom(bytes);
                Textures[cacheKey] = texture;
                element.style.backgroundImage = new StyleBackground(texture);
            }
            catch (Exception)
            {
                // A thumbnail that fails to load leaves the tile blank; nothing to report.
            }
        }

        /// <summary>
        /// An image input: drop a file or a Texture2D asset from the Project window, or click to browse.
        /// Holds PNG/JPEG/WebP bytes; textures from the project are re-encoded as PNG.
        /// </summary>
        public class ImagePicker : VisualElement
        {
            public byte[] Bytes { get; private set; }
            public string ContentType { get; private set; }
            public string FileName { get; private set; }
            public event Action Changed;
            readonly Label _label;

            public ImagePicker(string prompt = "Drop an image or click to choose")
            {
                AddToClassList("z-drop");
                _label = new Label(prompt).Classes("z-muted");
                Add(_label);
                RegisterCallback<ClickEvent>(_ => Browse());
                RegisterCallback<DragUpdatedEvent>(_ =>
                {
                    DragAndDrop.visualMode = DragAndDropVisualMode.Copy;
                    AddToClassList("z-drop--hover");
                });
                RegisterCallback<DragLeaveEvent>(_ => RemoveFromClassList("z-drop--hover"));
                RegisterCallback<DragPerformEvent>(_ =>
                {
                    RemoveFromClassList("z-drop--hover");
                    DragAndDrop.AcceptDrag();
                    var texture = DragAndDrop.objectReferences.OfType<Texture2D>().FirstOrDefault();
                    if (texture != null) SetFromAsset(texture);
                    else if (DragAndDrop.paths.Length > 0) SetFromFile(DragAndDrop.paths[0]);
                });
            }

            void Browse()
            {
                var path = EditorUtility.OpenFilePanelWithFilters("Choose an image", "", new[] { "Images", "png,jpg,jpeg,webp" });
                if (!string.IsNullOrEmpty(path)) SetFromFile(path);
            }

            public void SetFromFile(string path)
            {
                var ext = Path.GetExtension(path).ToLowerInvariant();
                var type = ext switch { ".png" => "image/png", ".jpg" or ".jpeg" => "image/jpeg", ".webp" => "image/webp", _ => null };
                if (type == null)
                {
                    EditorUtility.DisplayDialog("Zetta 3D", "Choose a PNG, JPEG or WebP image.", "OK");
                    return;
                }
                Set(File.ReadAllBytes(path), type, Path.GetFileName(path));
            }

            public void SetFromAsset(Texture2D texture)
            {
                var path = AssetDatabase.GetAssetPath(texture);
                if (!string.IsNullOrEmpty(path) && (path.EndsWith(".png") || path.EndsWith(".jpg") || path.EndsWith(".jpeg")))
                {
                    SetFromFile(path);
                    return;
                }
                Set(ZettaCapture.EncodeReadable(texture), "image/png", texture.name + ".png");
            }

            public void Set(byte[] bytes, string contentType, string name)
            {
                Bytes = bytes;
                ContentType = contentType;
                FileName = name;
                style.backgroundImage = bytes == null ? null : new StyleBackground(TextureFrom(bytes));
                _label.Visible(bytes == null);
                Changed?.Invoke();
            }

            public void Reset() => Set(null, null, null);
        }

        /// <summary>A slim progress bar with a line of text under it ("Downloading… 45%").</summary>
        public class ProgressBar : VisualElement
        {
            readonly VisualElement _fill;
            readonly Label _label;

            public ProgressBar()
            {
                AddToClassList("z-progress");
                var track = new VisualElement().Classes("z-progress-track");
                _fill = new VisualElement().Classes("z-progress-fill");
                track.Add(_fill);
                Add(track);
                _label = new Label().Classes("z-progress-label");
                Add(_label);
                Set(0f);
            }

            public void Set(float progress, string text = null)
            {
                progress = Mathf.Clamp01(progress);
                _fill.style.width = Length.Percent(progress * 100f);
                _label.text = text ?? (progress >= 0.96f ? "Importing…" : $"Downloading… {Mathf.RoundToInt(progress * 100f)}%");
            }
        }

        /// <summary>Raised when "Place in scene" changes anywhere, so every toggle showing it agrees.</summary>
        public static event Action<bool> PlaceInSceneChanged;

        /// <summary>
        /// The one switch for what Import does after the download: also put the model in the open scene.
        /// Stored per project (ZettaProjectSettings.PlaceImportedInScene) and shared by every view that imports.
        /// </summary>
        public static Toggle PlaceInSceneToggle()
        {
            var toggle = Toggle("Place in scene", ZettaProjectSettings.Instance.PlaceImportedInScene, value =>
            {
                if (ZettaProjectSettings.Instance.PlaceImportedInScene == value) return;
                ZettaProjectSettings.Instance.PlaceImportedInScene = value;
                ZettaProjectSettings.Instance.Save();
                PlaceInSceneChanged?.Invoke(value);
            }, "When on, Import also places the model in the open scene, resting on the ground.");
            toggle.AddToClassList("z-place-toggle");
            void Sync(bool value) => toggle.SetValueWithoutNotify(value);
            toggle.RegisterCallback<AttachToPanelEvent>(_ =>
            {
                toggle.SetValueWithoutNotify(ZettaProjectSettings.Instance.PlaceImportedInScene);
                PlaceInSceneChanged -= Sync;
                PlaceInSceneChanged += Sync;
            });
            toggle.RegisterCallback<DetachFromPanelEvent>(_ => PlaceInSceneChanged -= Sync);
            return toggle;
        }

        // ─── Account ────────────────────────────────────────────────────────────────

        /// <summary>
        /// The Zetta 3D window's header: brand, the page tabs, account, credits, sign out. <paramref name="navigate"/>
        /// gets the page id (<see cref="ZettaWindow.Studio"/>, <see cref="ZettaWindow.Library"/>…) a tab was clicked for.
        /// </summary>
        public static VisualElement Header(Action<string> navigate)
        {
            var header = new VisualElement().Classes("z-header");
            header.Add(new VisualElement().Classes("z-brand-dot"));
            header.Add(new Label("Zetta 3D").Classes("z-brand"));

            var nav = new VisualElement().Classes("z-row", "z-header-nav");
            foreach (var (page, label, tip) in new[]
                     {
                         (ZettaWindow.Studio, "3D Studio", "Generate, process and import models"),
                         (ZettaWindow.Library, "My Generations", "Everything you have made on Zetta 3D — import it into the project"),
                         (ZettaWindow.Pipelines, "Pipelines", "Chain the Studio's tools into reusable 3D pipelines"),
                         (ZettaWindow.Agent, "Agent", "The Zetta Unity Agent — builds your game in the Editor"),
                     })
            {
                var button = Button(label, () => navigate(page), "z-nav", "z-button--small");
                button.name = "nav-" + page;
                button.tooltip = tip;
                nav.Add(button);
            }
            header.Add(nav);
            header.Add(Spacer());
            var account = new VisualElement().Classes("z-row");
            header.Add(account);

            void Render()
            {
                account.Clear();
                if (!ZettaAuth.IsSignedIn) return;
                var user = ZettaAuth.User;
                account.Add(Chip(user.PlanLabel, "signal"));
                account.Add(Chip($"{user.Credits.ThreeD.Total:N0} credits"));
                var menu = Button("⋯", null, "z-button--ghost", "z-button--small");
                menu.clicked += () =>
                {
                    var m = new GenericMenu();
                    m.AddDisabledItem(new GUIContent(user.Email));
                    m.AddSeparator("");
                    m.AddItem(new GUIContent("Refresh account"), false, () => _ = ZettaAuth.RefreshAsync());
                    m.AddItem(new GUIContent("Buy credits / upgrade…"), false, () => Application.OpenURL(ZettaConfig.SiteBase + "/pricing"));
                    m.AddItem(new GUIContent("Open zetta3d.ai"), false, () => Application.OpenURL(ZettaConfig.SiteBase));
                    m.AddItem(new GUIContent("Settings…"), false, () => SettingsService.OpenUserPreferences("Preferences/Zetta 3D"));
                    m.AddSeparator("");
                    m.AddItem(new GUIContent("Sign out"), false, () => ZettaAuth.SignOut());
                    m.ShowAsContext();
                };
                account.Add(menu);
            }

            Render();
            WhileOnScreen(header, h => ZettaAuth.Changed += h, h => ZettaAuth.Changed -= h, Render);
            return header;
        }

        /// <summary>Highlights the header tab for the page shown (a <see cref="ZettaWindow"/> page id).</summary>
        public static void SetActiveNav(VisualElement header, string page)
        {
            header?.Query<Button>(className: "z-nav").ForEach(b => b.EnableInClassList("z-nav--active", b.name == "nav-" + page));
        }

        /// <summary>
        /// Keeps <paramref name="handler"/> on an event while <paramref name="element"/> is on screen, and runs it each
        /// time the element comes back. A window whose tab is hidden behind another tab in its dock area is taken off
        /// its panel; an element that stopped listening then and never listened again missed the sign-in, and showed
        /// "signed out" until the window was reopened.
        /// </summary>
        public static void WhileOnScreen(VisualElement element, Action<Action> subscribe, Action<Action> unsubscribe, Action handler)
        {
            subscribe(handler);
            element.RegisterCallback<AttachToPanelEvent>(_ =>
            {
                unsubscribe(handler);
                subscribe(handler);
                handler();
            });
            element.RegisterCallback<DetachFromPanelEvent>(_ => unsubscribe(handler));
        }

        /// <summary>
        /// The panel a window shows until the user is signed in. Returns a container that swaps between
        /// the login card and <paramref name="signedIn"/> on its own.
        /// </summary>
        public static VisualElement Gate(Func<VisualElement> signedIn)
        {
            var host = new VisualElement { style = { flexGrow = 1 } };
            VisualElement content = null;

            void Render()
            {
                if (ZettaAuth.IsSignedIn)
                {
                    if (content != null && content.parent == host) return;
                    host.Clear();
                    content = signedIn();
                    host.Add(content);
                    return;
                }
                content = null;
                host.Clear();
                host.Add(LoginCard());
            }

            Render();
            WhileOnScreen(host, h => ZettaAuth.Changed += h, h => ZettaAuth.Changed -= h, Render);
            return host;
        }

        static VisualElement LoginCard()
        {
            var wrap = new VisualElement().Classes("z-login");
            var card = new VisualElement().Classes("z-login-card");
            wrap.Add(card);
            card.Add(new Label("Zetta 3D").Classes("z-login-title"));

            switch (ZettaAuth.Current)
            {
                case ZettaAuth.State.WaitingForBrowser:
                    card.Add(new Label("Finish signing in in your browser, then press Connect Unity. This window updates on its own.").Classes("z-login-sub"));
                    card.Add(Button("Open the page again", ZettaAuth.BeginLogin, "z-button--primary"));
                    card.Add(Button("Cancel", () => ZettaAuth.CancelLogin(), "z-button--ghost"));
                    break;
                case ZettaAuth.State.Checking:
                    card.Add(new Label("Checking your account…").Classes("z-login-sub"));
                    card.Add(Button("Sign in again", ZettaAuth.BeginLogin, "z-button--ghost"));
                    break;
                default:
                    card.Add(new Label("Generate 3D models and images, and build your game with the Zetta Unity Agent — right here in the Editor.").Classes("z-login-sub"));
                    card.Add(Button("Sign in with your browser", ZettaAuth.BeginLogin, "z-button--primary"));
                    card.Add(Button("Create a free account", () => Application.OpenURL(ZettaConfig.SiteBase + "/signup"), "z-button--ghost"));
                    break;
            }

            if (!string.IsNullOrEmpty(ZettaAuth.LastError))
                card.Add(new Label(ZettaAuth.LastError).Classes("z-error"));
            if (!ZettaConfig.IsProduction)
                card.Add(Hint($"Using {ZettaConfig.ApiBase}"));
            return wrap;
        }

        /// <summary>Runs an async action with a busy button and a friendly error line.</summary>
        public static async Task Run(Button button, Label error, Func<Task> action)
        {
            if (button != null) button.SetEnabled(false);
            if (error != null) error.Visible(false);
            try
            {
                await action();
            }
            catch (ZettaApiException e)
            {
                if (error != null)
                {
                    error.text = ZettaCatalog.Explain(e);
                    error.Visible(true);
                }
                else Debug.LogWarning("[Zetta 3D] " + ZettaCatalog.Explain(e));
            }
            catch (Exception e)
            {
                if (error != null)
                {
                    error.text = e.Message;
                    error.Visible(true);
                }
                else Debug.LogException(e);
            }
            finally
            {
                if (button != null) button.SetEnabled(true);
            }
        }
    }
}
