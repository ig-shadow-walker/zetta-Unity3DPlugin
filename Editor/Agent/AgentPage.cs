using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using UnityEditor;
using UnityEngine;
using UnityEngine.UIElements;

namespace Zetta3D.Editor.Agent
{
    /// <summary>
    /// The Zetta Unity Agent — a game-building assistant that sees the scene and works in the Editor. A page of the
    /// <see cref="ZettaWindow"/>.
    /// </summary>
    public class AgentPage : ZettaPage
    {
        public AgentPage(ZettaWindow window) : base(window)
        {
        }

        /// <summary>Opens the Zetta 3D window on the Agent.</summary>
        public static AgentPage Open() => ZettaWindow.Open(ZettaWindow.Agent).AgentPage;

        /// <summary>Brings the Agent forward when it needs the user (an approval card).</summary>
        public static void Reveal() => Open();

        ScrollView _scroll;
        VisualElement _list;
        TextField _input;
        Button _send;
        Label _phase;
        VisualElement _attachments;
        VisualElement _memoryPanel;
        PopupField<string> _tier;
        PopupField<string> _thinking;
        /// <summary>On the user's own keys: whose key the agent runs on. Null on the Zetta account.</summary>
        PopupField<string> _provider;
        /// <summary>The provider, model and thinking menus — rebuilt when the account, a key or the catalog changes.</summary>
        VisualElement _pickers;
        Button _attach;
        bool _canSee = true;
        readonly List<ToolImage> _images = new List<ToolImage>();
        ThinkingIndicator _working;
        /// <summary>Files attached from disk: PDFs, text (inlined) and project copies (tagged as assets).</summary>
        readonly List<AgentAttachment> _files = new List<AgentAttachment>();
        /// <summary>Scene objects and assets tagged with @ (typed, picked or dragged in).</summary>
        readonly List<AgentMention> _mentions = new List<AgentMention>();
        MentionPopup _mentionPopup;
        VisualElement _composerBox;
        /// <summary>Where the @ being typed starts in the text, or −1 when no tag is being typed.</summary>
        int _mentionStart = -1;
        /// <summary>What each attachment is ("Scene view", "Game view (Main Camera)", a file name), told to the agent.</summary>
        readonly List<string> _imageLabels = new List<string>();
        /// <summary>Each attached picture's "attached:name", so the agent can edit or compose it.</summary>
        readonly List<string> _imageKeys = new List<string>();
        bool _dirty;

        // The account menu's first entry; never a provider key.
        const string ZettaAccount = "zetta";
        static readonly List<string> Tiers =new List<string> { "auto", "fast", "capable", "intelligent" };
        static readonly List<string> TierLabels = new List<string> { "Auto", "Fast", "Capable", "Intelligent" };
        static readonly List<string> Thinking = new List<string> { "auto", "off", "low", "medium", "high", "xhigh", "max" };
        static readonly List<string> ThinkingLabels = new List<string>
            { "Auto thinking", "No thinking", "Low thinking", "Medium thinking", "High thinking", "Extra high thinking", "Max thinking" };

        public override void Enable()
        {
            AgentRuntime.Changed += MarkDirty;
            EditorApplication.update += Tick;
            AgentCatalog.Changed += PickersChanged;
            ZettaKeychain.Changed += PickersChanged;
            ZettaAuth.Changed += RefreshCatalog;
        }

        public override void Disable()
        {
            AgentRuntime.Changed -= MarkDirty;
            EditorApplication.update -= Tick;
            AgentCatalog.Changed -= PickersChanged;
            ZettaKeychain.Changed -= PickersChanged;
            ZettaAuth.Changed -= RefreshCatalog;
        }

        // Signing in, or coming back to the window, asks the server again: a list fetched before it offered
        // own keys (or before sign-in) would otherwise hide the provider menus until the window is reopened.
        static void RefreshCatalog() => _ = AgentCatalog.RefreshAsync();

        public override void OnWindowFocus()
        {
            if (AgentCatalog.Providers == null) RefreshCatalog();
        }

        // Posted, not run inline: a change can come from one of these menus' own callbacks, or off the main thread.
        void PickersChanged() => MainThread.Post(RebuildPickers);

        void MarkDirty() => _dirty = true;

        void Tick()
        {
            if (!_dirty || _list == null) return;
            _dirty = false;
            RenderTranscript();
        }

        protected override VisualElement BuildContent()
        {
            // Which providers and models the user's own keys can run — the server's list, never a built-in one.
            _ = AgentCatalog.RefreshAsync();
            return BuildChat();
        }

        VisualElement BuildChat()
        {
            var shell = new VisualElement { style = { flexGrow = 1 } };

            // Toolbar: history, new chat, memory, settings.
            var bar = new VisualElement().Classes("z-row");
            bar.style.paddingLeft = 14;
            bar.style.paddingRight = 14;
            bar.style.paddingTop = 6;
            bar.style.paddingBottom = 6;
            _historyButton = ZettaUI.Button("History", ToggleHistory, "z-button--ghost", "z-button--small");
            _historyButton.tooltip = "Your conversations in this project";
            bar.Add(_historyButton);
            bar.Add(ZettaUI.Button("New chat", () =>
            {
                AgentRuntime.NewChat();
                RenderTranscript();
            }, "z-button--ghost", "z-button--small"));
            bar.Add(ZettaUI.Spacer());
            _memoryButton = ZettaUI.Button("Memory", ToggleMemory, "z-button--ghost", "z-button--small");
            _memoryButton.tooltip = "What the agent remembers about this project, and its brief";
            bar.Add(_memoryButton);
            var settings = ZettaUI.Button("⚙", null, "z-button--ghost", "z-button--small");
            settings.clicked += ShowSettings;
            bar.Add(settings);
            shell.Add(bar);

            _historyPanel = new VisualElement().Classes("z-side-panel").Visible(false);
            shell.Add(_historyPanel);

            _memoryPanel = new VisualElement().Classes("z-side-panel").Visible(false);
            _memoryPanel.style.maxHeight = 360;
            shell.Add(_memoryPanel);

            _scroll = new ScrollView(ScrollViewMode.Vertical).Classes("z-scroll", "z-chat");
            // A running step's dot breathes: the class flips every half second and USS eases the change.
            _scroll.schedule.Execute(() => _scroll.ToggleInClassList("z-pulse")).Every(520);
            _list = new VisualElement();
            _scroll.Add(_list);
            shell.Add(_scroll);

            _phase = new Label().Classes("z-hint", "z-agent-phase");
            shell.Add(_phase);

            shell.Add(BuildComposer());

            // The @ picker floats above the message box, inside the chat.
            _mentionPopup = new MentionPopup();
            _mentionPopup.Picked += OnMentionPicked;
            _mentionPopup.Closed += () => _mentionStart = -1;
            shell.Add(_mentionPopup);
            RegisterDrop(shell);
            RenderTranscript();
            // Tags sent here before the chat existed ("Add to Zetta Agent" with the window closed, or before sign-in).
            shell.schedule.Execute(TakePending);
            return shell;
        }

        static readonly List<string> Autonomy = new List<string> { "ask", "full" };
        static readonly List<string> AutonomyLabels = new List<string> { "Ask first", "Full autonomy" };
        PopupField<string> _autonomy;

        VisualElement BuildComposer()
        {
            var composer = new VisualElement().Classes("z-composer");
            var box = new VisualElement().Classes("z-composer-box");
            _composerBox = box;
            composer.Add(box);

            _attachments = new VisualElement().Classes("z-row", "z-row--wrap", "z-composer-attachments");
            box.Add(_attachments);

            _input = new TextField { multiline = true, maxLength = 16000 };
            _input.AddToClassList("z-composer-input");
#if UNITY_2023_1_OR_NEWER
            _input.verticalScrollerVisibility = ScrollerVisibility.Auto;
#endif
            ZettaUI.SetPlaceholder(_input, "Ask the agent to build something — “add a third-person player with a follow camera”…");
            // Enter sends; Shift+Enter starts a new line. Both halves of the key press (its key code and its
            // character) are taken here, so the text field never adds a stray line break of its own.
            _input.RegisterCallback<KeyDownEvent>(e =>
            {
                // While the @ picker is open it owns ↑ ↓ Enter Tab Esc (and their character halves).
                if (_mentionPopup != null && _mentionPopup.IsOpen &&
                    (_mentionPopup.HandleKey(e.keyCode) || (e.keyCode == KeyCode.None && e.character is '\n' or '\r' or '\t')))
                {
                    e.StopImmediatePropagation();
#if UNITY_2023_2_OR_NEWER
                    _input.focusController?.IgnoreEvent(e);
#else
                    e.PreventDefault();
#endif
                    return;
                }
                var enter = e.keyCode == KeyCode.Return || e.keyCode == KeyCode.KeypadEnter || e.character == '\n' || e.character == '\r';
                if (!enter) return;
                e.StopImmediatePropagation();
#if UNITY_2023_2_OR_NEWER
                _input.focusController?.IgnoreEvent(e);
#else
                e.PreventDefault();
#endif
                if (e.keyCode == KeyCode.None) return; // the character half of a press already handled
                if (e.shiftKey) InsertNewline();
                else Submit();
            }, TrickleDown.TrickleDown);
            box.Add(_input);
            _input.RegisterValueChangedCallback(_ => _input.schedule.Execute(UpdateMentionQuery));
            _input.RegisterCallback<KeyUpEvent>(e =>
            {
                if (e.keyCode is KeyCode.LeftArrow or KeyCode.RightArrow or KeyCode.Home or KeyCode.End) UpdateMentionQuery();
            });
            _input.RegisterCallback<FocusOutEvent>(_ =>
            {
                var caret = Cursor();
                _input.schedule.Execute(() =>
                {
                    if (_mentionPopup == null || !_mentionPopup.IsOpen || _input.focusController?.focusedElement == _input) return;
                    // A click inside the picker (‹ back, its scrollbar) keeps it open: the message box gets its focus back.
                    if (_mentionPopup.PointerInside)
                    {
                        _input.Focus();
                        _input.schedule.Execute(() => _input.SelectRange(caret, caret));
                        return;
                    }
                    _mentionPopup.Close();
                }).StartingIn(150);
            });

            var bar = new VisualElement().Classes("z-composer-bar");
            var attach = new VisualElement().Classes("z-composer-group");
            _attach = ZettaUI.Button("+", null, "z-composer-chip", "z-composer-plus");
            _attach.name = "composer-attach";
            _attach.clicked += () => ShowAttachMenu(_attach.worldBound);
            _attach.tooltip = "Attach a picture of the Scene or Game view, or any file — or drag files onto the chat. Type @, or select items and press ⇧⌘M (Ctrl+Shift+M), to tag scene objects and project assets.";
            attach.Add(_attach);
            bar.Add(attach);

            _pickers = new VisualElement().Classes("z-composer-group");
            bar.Add(_pickers);
            RebuildPickers();

            var options = new VisualElement().Classes("z-composer-group");
            _autonomy = new PopupField<string>(Autonomy, Math.Max(0, Autonomy.IndexOf(ZettaPrefs.AgentAutonomy)),
                v => AutonomyLabels[Autonomy.IndexOf(v)], v => AutonomyLabels[Autonomy.IndexOf(v)]);
            _autonomy.AddToClassList("z-composer-popup");
            _autonomy.AddToClassList("z-composer-autonomy");
            SyncAutonomy();
            _autonomy.RegisterValueChangedCallback(e =>
            {
                ZettaPrefs.AgentAutonomy = e.newValue;
                SyncAutonomy();
            });
            options.Add(_autonomy);
            bar.Add(options);

            bar.Add(ZettaUI.Spacer());
            var hint = new Label("⇧↵ new line").Classes("z-composer-hint");
            bar.Add(hint);
            // The pickers fill a narrow window: the hint goes before Send would wrap onto a line of its own.
            bar.RegisterCallback<GeometryChangedEvent>(e => hint.Visible(e.newRect.width >= (_provider != null ? 900 : 760)));
            _send = ZettaUI.Button("Send", Submit, "z-composer-send");
            bar.Add(_send);
            box.Add(bar);
            return composer;
        }

        /// <summary>
        /// The menus under the message box. On the Zetta account: model (Auto, Fast, Capable, Intelligent) and thinking.
        /// On the user's own keys: provider (only those with a saved key), that provider's models, and the thinking
        /// levels the model has — each list from the server. With no key saved, a button to add one instead.
        /// Whenever a key is saved, the first menu also offers "Zetta Account", to go back to the Zetta account.
        /// </summary>
        void RebuildPickers()
        {
            if (_pickers == null) return;
            _pickers.Clear();
            _provider = null;
            _tier = null;
            _thinking = null;
            var vision = true;

            var keyed = AgentCatalog.KeyedProviders;
            if (AgentCatalog.OwnKeys && keyed.Count == 0)
            {
                var add = ZettaUI.Button("🔑  Add an API key", OpenSettings, "z-composer-chip");
                add.tooltip = "The agent is set to run on your own API keys, and none is saved yet. Add one, or switch back to your Zetta account, in Preferences › Zetta 3D.";
                _pickers.Add(add);
                SetCaptureChips(true);
                return;
            }

            // With a key saved, the first menu says what the agent runs on: the Zetta account, or a provider's key.
            if (keyed.Count > 0)
            {
                var accountKeys = new List<string> { ZettaAccount };
                accountKeys.AddRange(keyed.Select(p => p.Key));
                string AccountLabel(string key) => key == ZettaAccount ? "Zetta Account" : keyed.First(p => p.Key == key).Label;
                var current = AgentCatalog.OwnKeys ? AgentCatalog.ActiveProvider.Key : ZettaAccount;
                _provider = new PopupField<string>(accountKeys, accountKeys.IndexOf(current), AccountLabel, AccountLabel);
                _provider.tooltip = "What the agent runs on: your Zetta account and credits, or your own API key with a provider. Add or remove keys in Preferences › Zetta 3D.";
                _provider.AddToClassList("z-composer-popup");
                _provider.RegisterValueChangedCallback(e =>
                {
                    if (e.newValue == ZettaAccount) ZettaPrefs.AgentAccount = "zetta";
                    else
                    {
                        ZettaPrefs.AgentAccount = "own";
                        ZettaPrefs.AgentProvider = e.newValue;
                    }
                    // Preferences shows the same choice; it and these menus rebuild from the event.
                    AgentCatalog.NotifyChanged();
                });
                _pickers.Add(_provider);
            }

            if (AgentCatalog.OwnKeys)
            {
                var active = AgentCatalog.ActiveProvider;
                var model = AgentCatalog.ActiveModel;
                var modelKeys = active.Models.Select(m => m.Key).ToList();
                string ModelLabel(string key) => active.Models.First(m => m.Key == key).Label;
                _tier = new PopupField<string>(modelKeys, Math.Max(0, modelKeys.IndexOf(model.Key)), ModelLabel, ModelLabel);
                _tier.tooltip = $"{model.Label} — {model.Blurb} Used for every message: Auto never changes the model on your own key.";
                _tier.AddToClassList("z-composer-popup");
                _tier.RegisterValueChangedCallback(e =>
                {
                    ZettaPrefs.SetAgentModel(active.Key, e.newValue);
                    MainThread.Post(RebuildPickers);
                });
                _pickers.Add(_tier);

                // Auto, then only the levels this model has.
                var levels = new List<string> { "auto" };
                levels.AddRange(model.Thinking.Where(Thinking.Contains));
                var shown = AgentCatalog.ThinkingFor(model, ZettaPrefs.AgentThinking);
                _thinking = new PopupField<string>(levels, Math.Max(0, levels.IndexOf(shown)), ThinkingLabel, ThinkingLabel);
                _thinking.tooltip = "How hard the model thinks — Auto decides for each message, and thinks harder on long jobs.";
                _thinking.AddToClassList("z-composer-popup");
                _thinking.RegisterValueChangedCallback(e => ZettaPrefs.AgentThinking = e.newValue);
                _pickers.Add(_thinking);
                vision = model.Vision;
            }
            else
            {
                _tier = new PopupField<string>(Tiers, Math.Max(0, Tiers.IndexOf(ZettaPrefs.AgentTier)),
                    v => TierLabels[Tiers.IndexOf(v)], v => TierLabels[Tiers.IndexOf(v)]);
                _tier.tooltip = "Model — Auto picks the model for each message.";
                _tier.AddToClassList("z-composer-popup");
                _tier.RegisterValueChangedCallback(e => ZettaPrefs.AgentTier = e.newValue);
                _pickers.Add(_tier);

                // Its own Auto, independent of the model's: Auto + Auto lets the agent decide both for each message.
                _thinking = new PopupField<string>(Thinking, Math.Max(0, Thinking.IndexOf(ZettaPrefs.AgentThinking)), ThinkingLabel, ThinkingLabel);
                _thinking.tooltip = "How hard the model thinks — Auto decides for each message, from how hard the request is.";
                _thinking.AddToClassList("z-composer-popup");
                _thinking.RegisterValueChangedCallback(e => ZettaPrefs.AgentThinking = e.newValue);
                _pickers.Add(_thinking);
            }
            SetCaptureChips(vision);
            _pickers.SetEnabled(!AgentRuntime.Busy);
        }

        static string ThinkingLabel(string level) => ThinkingLabels[Math.Max(0, Thinking.IndexOf(level))];

        /// <summary>The pictures of the Scene and Game views: not offered to a model that can't see.</summary>
        void SetCaptureChips(bool vision) => _canSee = vision;

        /// <summary>What + offers, in order: a label, whether it can be picked now, and what it does. A null label is a separator.</summary>
        internal List<(string label, bool enabled, Action run)> AttachOptions()
        {
            var why = _canSee ? "" : " — this model can't see pictures";
            return new List<(string, bool, Action)>
            {
                ("📷  Picture of the Scene view" + why, _canSee, AttachScene),
                ("🎮  Picture of the Game view" + why, _canSee, AttachGame),
                (null, false, null),
                ("🖼  Picture from the project…", true, AttachProjectPicture),
                ("📎  File…", true, AttachFile),
            };
        }

        /// <summary>+ under the message box: the three ways to attach something, in one menu.</summary>
        void ShowAttachMenu(Rect below)
        {
            var menu = new GenericMenu();
            foreach (var (label, enabled, run) in AttachOptions())
            {
                if (label == null) menu.AddSeparator("");
                else if (enabled) menu.AddItem(new GUIContent(label), false, () => run());
                else menu.AddDisabledItem(new GUIContent(label));
            }
            menu.DropDown(below);
        }

        static void OpenSettings() => SettingsService.OpenUserPreferences("Preferences/Zetta 3D");

        void SyncAutonomy()
        {
            var full = ZettaPrefs.AgentFullAutonomy;
            _autonomy.EnableInClassList("z-composer-autonomy--full", full);
            _autonomy.tooltip = full
                ? "Full autonomy — the agent never asks: it edits, deletes and spends credits on its own, and keeps going until the job is done. Everything in the scene can still be undone."
                : "Ask first — the agent asks before deleting anything and before spending credits.";
        }

        void InsertNewline()
        {
            var value = _input.value ?? "";
#if UNITY_2023_1_OR_NEWER
            int a = _input.textSelection.cursorIndex, b = _input.textSelection.selectIndex;
#else
            int a = _input.cursorIndex, b = _input.selectIndex;
#endif
            a = Mathf.Clamp(a, 0, value.Length);
            b = Mathf.Clamp(b, 0, value.Length);
            var start = Math.Min(a, b);
            var end = Math.Max(a, b);
            _input.value = value.Substring(0, start) + "\n" + value.Substring(end);
            _input.SelectRange(start + 1, start + 1);
        }

        void Submit()
        {
            if (AgentRuntime.Busy)
            {
                AgentRuntime.Stop();
                return;
            }
            var text = _input.value ?? "";
            if (string.IsNullOrWhiteSpace(text) && _images.Count == 0 && _files.Count == 0 && _mentions.Count == 0) return;
            // A model on the user's own key may not take pictures or PDFs: say so, and keep the attachments.
            var ownModel = AgentCatalog.OwnKeys ? AgentCatalog.ActiveModel : null;
            if (ownModel != null && !ownModel.Vision && _images.Count > 0)
            {
                AgentRuntime.Notice($"{ownModel.Label} can't see pictures. Remove them, or pick a model that can.");
                return;
            }
            if (ownModel != null && !ownModel.Documents && _files.Any(f => f.Kind == "document"))
            {
                AgentRuntime.Notice($"{ownModel.Label} can't read PDFs. Remove them, or pick a model that can.");
                return;
            }
            if (string.IsNullOrWhiteSpace(text)) text = _mentions.Count > 0 && _images.Count == 0 && _files.Count == 0
                ? "Take a look at what I tagged."
                : "Take a look at what I attached.";
            _mentionPopup?.Close();
            _input.value = "";
            // Say what each picture is, so the agent knows a Game view from a Scene view.
            if (_imageLabels.Count > 0)
                text += "\n\n(Attached: " + string.Join("; ", _imageLabels.Select((label, i) => _imageKeys[i] != null ? $"{label} [{_imageKeys[i]}]" : label)) + ")";
            foreach (var file in _files.Where(f => f.Kind == "text")) text += file.TextBlock();
            // Every tag chip goes along as an address the agent works with (it reads the file itself when it needs to).
            var mentions = _mentions.ToList();
            foreach (var file in _files.Where(f => f.Kind == "asset" && f.Mention != null))
                if (mentions.All(m => m.Path != file.Mention.Path)) mentions.Add(file.Mention);
            text += AgentMention.Block(mentions);
            var documents = _files.Where(f => f.Kind == "document").Select(f => f.Document).ToList();
            AgentRuntime.Send(text, _images.ToList(), documents);
            _images.Clear();
            _imageLabels.Clear();
            _imageKeys.Clear();
            _files.Clear();
            _mentions.Clear();
            RenderAttachments();
        }

        void AttachScene()
        {
            var shot = ZettaCapture.SceneView(1024);
            if (shot == null)
            {
                ShowNotification(new GUIContent("Open a Scene view first."));
                return;
            }
            AddImage(ToolImage.Jpeg(shot.Jpeg), "Scene view", shot.Jpeg, "scene-view.jpg");
        }

        /// <summary>What the player sees: the game camera (Main Camera, or the first active camera), at the Game view's aspect.</summary>
        void AttachGame()
        {
            var shot = ZettaCapture.GameView(1024);
            if (shot == null)
            {
                ShowNotification(new GUIContent("There is no camera in the scene to capture."));
                return;
            }
            AddImage(ToolImage.Jpeg(shot.Jpeg), shot.Label, shot.Jpeg, "game-view.jpg");
        }

        /// <summary>
        /// A picture in the project, tagged so the agent can look at it, edit it or build from it ("make this night time").
        /// Opens in Assets, showing images only; a file picked from outside the project is attached as any file is.
        /// </summary>
        void AttachProjectPicture()
        {
            var path = EditorUtility.OpenFilePanelWithFilters("Pick a picture from the project", Application.dataPath,
                new[] { "Images", "png,jpg,jpeg,webp,psd,tga,tif,tiff,bmp,exr,hdr,gif", "All files", "*" });
            if (!string.IsNullOrEmpty(path)) AddFiles(new[] { path });
        }

        void AttachFile()
        {
            var path = EditorUtility.OpenFilePanel("Attach a file", "", "");
            if (!string.IsNullOrEmpty(path)) AddFiles(new[] { path });
        }

        /// <summary>The server takes at most 16 MiB per request; pictures and PDFs together stay under ~14 MB of base64.</summary>
        const int MaxAttachedChars = 14_000_000;

        int AttachedChars() =>
            _images.Sum(i => i.Data?.Length ?? 0) +
            _files.Sum(f => (f.Document?.Data?.Length ?? 0) + (f.Text?.Length ?? 0));

        bool Fits(int extra)
        {
            if (AttachedChars() + extra <= MaxAttachedChars) return true;
            ShowNotification(new GUIContent("That would make the message too large to send — attach fewer or smaller files."));
            return false;
        }

        /// <summary>Files from disk (the 📎 button, or dropped from Finder / Explorer), sorted into what the agent can take.</summary>
        void AddFiles(IEnumerable<string> paths)
        {
            foreach (var path in paths)
            {
                try
                {
                    var attachment = AgentAttachment.FromPath(path);
                    var size = (attachment.Image?.Data?.Length ?? 0) + (attachment.Document?.Data?.Length ?? 0) + (attachment.Text?.Length ?? 0);
                    if (!Fits(size)) continue;
                    switch (attachment.Kind)
                    {
                        case "image":
                            AddImage(attachment.Image, attachment.Label, attachment.Original, attachment.OriginalName);
                            break;
                        case "asset":
                            AddMention(attachment.Mention);
                            if (attachment.Mention.Note != null) ShowNotification(new GUIContent("Copied into " + attachment.Label));
                            break;
                        default:
                            if (_files.Count(f => f.Kind == "document") >= 4 && attachment.Kind == "document")
                            {
                                ShowNotification(new GUIContent("Up to 4 PDFs per message."));
                                continue;
                            }
                            _files.Add(attachment);
                            break;
                    }
                }
                catch (Exception e)
                {
                    ShowNotification(new GUIContent(e.Message));
                }
            }
            RenderAttachments();
        }

        /// <summary>Tags waiting for the chat box: the window was just opened, or the user is not signed in yet.</summary>
        static readonly List<AgentMention> Pending = new List<AgentMention>();

        /// <summary>
        /// Tags these objects in the chat box ("Add to Zetta Agent"), opening the Agent window when it is not open.
        /// Returns how many could be tagged — scene objects and project assets; anything else is skipped.
        /// </summary>
        public static int AddToChat(IEnumerable<UnityEngine.Object> objects)
        {
            var mentions = (objects ?? Enumerable.Empty<UnityEngine.Object>()).Select(AgentMention.FromObject).Where(m => m != null).ToList();
            if (mentions.Count == 0) return 0;
            Pending.AddRange(mentions);
            Open().TakePending();
            return mentions.Count;
        }

        void TakePending()
        {
            if (_input == null || Pending.Count == 0) return;
            foreach (var mention in Pending) AddMention(mention);
            Pending.Clear();
            _input.Focus();
            _input.schedule.Execute(() => _input.SelectRange((_input.value ?? "").Length, (_input.value ?? "").Length));
        }

        /// <summary>Tags a scene object or asset: a chip above the message, sent as an address the agent works on.</summary>
        void AddMention(AgentMention mention)
        {
            if (mention == null) return;
            if (_mentions.All(m => m.Kind != mention.Kind || m.Path != mention.Path)) _mentions.Add(mention);
            RenderAttachments();
        }

        // ─── @ mentions ─────────────────────────────────────────────────────────────

        int Cursor()
        {
#if UNITY_2023_1_OR_NEWER
            return _input.textSelection.cursorIndex;
#else
            return _input.cursorIndex;
#endif
        }

        /// <summary>Opens, filters or closes the @ picker from the text before the cursor.</summary>
        void UpdateMentionQuery()
        {
            if (_mentionPopup == null) return;
            var value = _input.value ?? "";
            var cursor = Mathf.Clamp(Cursor(), 0, value.Length);
            var at = -1;
            for (var i = cursor - 1; i >= 0; i--)
            {
                var c = value[i];
                if (c == '@')
                {
                    if (i == 0 || char.IsWhiteSpace(value[i - 1])) at = i;
                    break;
                }
                if (char.IsWhiteSpace(c)) break;
            }
            if (at < 0)
            {
                _mentionStart = -1;
                _mentionPopup.Close();
                return;
            }
            var query = value.Substring(at + 1, cursor - at - 1);
            // A finished tag the user is just passing over is not a new one.
            if (_mentions.Any(m => m.Token == "@" + query)) return;
            _mentionStart = at;
            PlaceMentionPopup();
            _mentionPopup.Open(query);
        }

        void PlaceMentionPopup()
        {
            var composerHeight = _composerBox?.parent?.layout.height ?? 0;
            _mentionPopup.style.bottom = float.IsNaN(composerHeight) ? 120 : composerHeight + 4;
        }

        void OnMentionPicked(AgentMention mention)
        {
            var value = _input.value ?? "";
            var cursor = Mathf.Clamp(Cursor(), 0, value.Length);
            var start = _mentionStart >= 0 && _mentionStart <= cursor ? _mentionStart : cursor;
            // The "@query" typed to find it is removed — the chip above the message is the tag.
            var after = value.Substring(cursor);
            if (start > 0 && after.StartsWith(" ") && char.IsWhiteSpace(value[start - 1])) after = after.Substring(1);
            _input.value = value.Substring(0, start) + after;
            _input.SelectRange(start, start);
            _mentionStart = -1;
            _mentionPopup.Close();
            if (_mentions.All(m => m.Kind != mention.Kind || m.Path != mention.Path)) _mentions.Add(mention);
            RenderAttachments();
            _input.Focus();
        }

        // ─── Drag and drop ──────────────────────────────────────────────────────────

        /// <summary>
        /// Drops onto the chat: assets from the Project window and objects from the Hierarchy are @-tagged;
        /// files from Finder / Explorer are attached.
        /// </summary>
        void RegisterDrop(VisualElement target)
        {
            target.RegisterCallback<DragUpdatedEvent>(_ =>
            {
                var objects = DragAndDrop.objectReferences;
                if ((objects == null || objects.Length == 0) && (DragAndDrop.paths == null || DragAndDrop.paths.Length == 0)) return;
                DragAndDrop.visualMode = objects is { Length: > 0 } ? DragAndDropVisualMode.Link : DragAndDropVisualMode.Copy;
                _composerBox?.AddToClassList("z-composer-box--drop");
            });
            target.RegisterCallback<DragLeaveEvent>(_ => _composerBox?.RemoveFromClassList("z-composer-box--drop"));
            target.RegisterCallback<DragExitedEvent>(_ => _composerBox?.RemoveFromClassList("z-composer-box--drop"));
            target.RegisterCallback<DragPerformEvent>(_ =>
            {
                _composerBox?.RemoveFromClassList("z-composer-box--drop");
                DragAndDrop.AcceptDrag();
                var objects = DragAndDrop.objectReferences ?? new UnityEngine.Object[0];
                var tagged = 0;
                foreach (var obj in objects)
                {
                    var mention = AgentMention.FromObject(obj);
                    if (mention == null) continue;
                    AddMention(mention);
                    tagged++;
                }
                if (tagged == 0 && DragAndDrop.paths is { Length: > 0 } paths) AddFiles(paths);
                _input.Focus();
            });
        }

        void AddImage(ToolImage image, string label, byte[] original = null, string name = null)
        {
            if (_images.Count >= 4)
            {
                ShowNotification(new GUIContent("Up to 4 images per message."));
                return;
            }
            if (image.Data.Length > 4_000_000)
            {
                ShowNotification(new GUIContent("That image is too large (max ~3 MB)."));
                return;
            }
            if (!Fits(image.Data.Length)) return;
            _images.Add(image);
            _imageLabels.Add(label);
            string key = null;
            try
            {
                key = AttachedPictures.Keep(original ?? Convert.FromBase64String(image.Data), name ?? label);
            }
            catch (Exception)
            {
                // Still sent to the agent to look at; only editing it by name is lost.
            }
            _imageKeys.Add(key);
            RenderAttachments();
        }

        void RenderAttachments()
        {
            _attachments.Clear();
            for (var i = 0; i < _images.Count; i++)
            {
                var index = i;
                var thumb = new VisualElement().Classes("z-thumb");
                thumb.style.width = 56;
                thumb.style.height = 40;
                thumb.style.backgroundImage = new StyleBackground(ZettaUI.TextureFrom(Convert.FromBase64String(_images[i].Data)));
                thumb.tooltip = _imageLabels[i] + " — click to remove";
                thumb.RegisterCallback<ClickEvent>(_ =>
                {
                    _images.RemoveAt(index);
                    _imageLabels.RemoveAt(index);
                    _imageKeys.RemoveAt(index);
                    RenderAttachments();
                });
                _attachments.Add(thumb);
            }
            foreach (var file in _files.ToList())
            {
                var glyph = file.Kind == "document" ? "📄" : "📝";
                _attachments.Add(AttachmentChip($"{glyph}  {file.Label}", file.Kind == "document" ? "PDF — the agent reads it" : "Text — its contents go with your message", () =>
                {
                    _files.Remove(file);
                    RenderAttachments();
                }));
            }
            foreach (var mention in _mentions.ToList())
            {
                var chip = AttachmentChip(mention.Name, mention.IsScene ? $"Scene object {mention.Path}" : mention.Path, () =>
                {
                    _mentions.Remove(mention);
                    RenderAttachments();
                });
                chip.AddToClassList(mention.IsScene ? "z-attach-chip--scene" : "z-attach-chip--asset");
                var icon = AgentMention.Icon(mention);
                if (icon != null) chip.Insert(0, new Image { image = icon }.Classes("z-attach-icon"));
                _attachments.Add(chip);
            }
            _attachments.Visible(_attachments.childCount > 0);
        }

        static VisualElement AttachmentChip(string text, string tooltip, Action remove)
        {
            var chip = new VisualElement().Classes("z-attach-chip");
            chip.tooltip = tooltip;
            chip.Add(new Label(text).Classes("z-attach-label"));
            var x = new Button(remove) { text = "✕" };
            x.AddToClassList("z-attach-x");
            chip.Add(x);
            return chip;
        }

        // ─── Transcript ─────────────────────────────────────────────────────────────

        // Rendered rows, reused while their content is unchanged — streaming touches only the last one.
        readonly List<(ChatItem item, string signature, VisualElement element)> _rows = new List<(ChatItem, string, VisualElement)>();
        VisualElement _welcome;

        static string Signature(ChatItem i) =>
            $"{i.Kind}|{i.Text?.Length}|{i.Text?.GetHashCode()}|{i.Status}|{i.Detail}|{i.Resolved}|{i.Options.Count}";

        void RenderTranscript()
        {
            if (_list == null) return;
            var state = AgentRuntime.State;

            if (state.Items.Count == 0)
            {
                _rows.Clear();
                _list.Clear();
                _welcome = Welcome();
                _list.Add(_welcome);
            }
            else
            {
                if (_welcome != null)
                {
                    _welcome.RemoveFromHierarchy();
                    _welcome = null;
                }
                // Drop rows that no longer line up with the transcript (a new chat, a reopened session).
                var keep = 0;
                while (keep < _rows.Count && keep < state.Items.Count && ReferenceEquals(_rows[keep].item, state.Items[keep])) keep++;
                for (var i = _rows.Count - 1; i >= keep; i--)
                {
                    _rows[i].element.RemoveFromHierarchy();
                    _rows.RemoveAt(i);
                }
                for (var i = 0; i < state.Items.Count; i++)
                {
                    var item = state.Items[i];
                    var signature = Signature(item);
                    if (i < _rows.Count)
                    {
                        if (_rows[i].signature == signature) continue;
                        var fresh = Render(item);
                        var index = _list.IndexOf(_rows[i].element);
                        _rows[i].element.RemoveFromHierarchy();
                        _list.Insert(Math.Max(0, index), fresh);
                        _rows[i] = (item, signature, fresh);
                    }
                    else
                    {
                        var element = Render(item);
                        _list.Add(element);
                        _rows.Add((item, signature, element));
                    }
                }
            }

            var phase = state.Phase switch
            {
                "streaming" => "Thinking",
                "sending" => "Carrying on",
                "tools" => state.Items.Any(i => i.Kind is "approval" or "ask" && !i.Resolved) ? "Waiting for you" : "Working in the Editor",
                "compiling" => "Compiling scripts",
                _ => "",
            };
            // The live indicator sits at the very end of the conversation while the agent works.
            _working ??= new ThinkingIndicator();
            if (phase != "")
            {
                _working.Text = phase;
                _working.SetRoute(RouteText(state), RouteWhy(state));
                if (_working.parent != _list || _list.IndexOf(_working) != _list.childCount - 1) _list.Add(_working);
            }
            else _working.RemoveFromHierarchy();
            _phase.Visible(false);
            if (_send != null) _send.text = AgentRuntime.Busy ? "Stop" : "Send";
            // A turn keeps its model to the end: the menus wait while it runs or waits on the Editor.
            _pickers?.SetEnabled(!AgentRuntime.Busy);

            var signatureNow = state.Items.Count + "|" + (state.Items.Count > 0 ? Signature(state.Items[state.Items.Count - 1]) : "");
            if (_lastScroll != signatureNow)
            {
                _lastScroll = signatureNow;
                _scroll.schedule.Execute(() => _scroll.scrollOffset = new Vector2(0, float.MaxValue)).StartingIn(30);
            }
        }

        string _lastScroll;

        /// <summary>"Capable · Low thinking", "Fast · No thinking" — what the server said this turn runs on.</summary>
        static string RouteText(AgentState state)
        {
            if (string.IsNullOrEmpty(state.RouteLabel)) return null;
            var index = Thinking.IndexOf(state.RouteThinking ?? "");
            return index <= 0 ? state.RouteLabel : state.RouteLabel + " · " + ThinkingLabels[index];
        }

        /// <summary>The chip's tooltip: who picked the model and the thinking, and whether the plan lowered them.</summary>
        static string RouteWhy(AgentState state)
        {
            var reason = state.RouteReason ?? "";
            var capped = reason.Contains("capped by the plan");
            reason = reason.Replace(" (capped by the plan)", "");
            var autoModel = state.RouteSource != "manual";
            var autoThinking = string.IsNullOrEmpty(state.RouteThinkingAsked) || state.RouteThinkingAsked == "auto";
            var why = autoModel && reason != "" ? " for " + reason : "";
            var text = autoModel && autoThinking ? "Auto picked the model and the thinking" + why + "."
                : autoModel ? "Auto picked the model" + why + "; you picked the thinking."
                : autoThinking ? "You picked the model; Auto picked the thinking."
                : "You picked the model and the thinking.";
            return capped ? text + " Lowered to what your plan includes." : text;
        }

        VisualElement Welcome()
        {
            var card = ZettaUI.Card();
            card.Add(ZettaUI.Title("What are we building?"));
            card.Add(ZettaUI.Text("The agent reads your scene and scripts, writes C#, builds prefabs and materials, looks at the Scene view to check its work, and makes 3D models and images with Zetta. Everything it changes can be undone."));
            var examples = new[]
            {
                "Look at my scene and tell me what's here",
                "Add a third-person player controller with a follow camera",
                "Make a simple coin pickup with a score UI",
                "Generate a low-poly treasure chest and place it by the player",
            };
            foreach (var example in examples)
            {
                var button = ZettaUI.Button(example, () =>
                {
                    _input.value = example;
                    _input.Focus();
                }, "z-button--ghost");
                button.style.unityTextAlign = TextAnchor.MiddleLeft;
                card.Add(button);
            }
            return card;
        }

        VisualElement Render(ChatItem item)
        {
            switch (item.Kind)
            {
                case "user":
                {
                    var box = new VisualElement().Classes("z-msg", "z-msg--user");
                    box.Add(new Label(item.Text).Classes("z-msg-text"));
                    if (!string.IsNullOrEmpty(item.Detail)) box.Add(ZettaUI.Hint(item.Detail));
                    return box;
                }
                case "assistant":
                    return new VisualElement().Classes("z-msg", "z-msg--assistant").Children(AgentMarkdown.Render(item.Text));
                case "route":
                    // Older conversations may still hold route lines; they are no longer shown.
                    return new VisualElement().Visible(false);
                case "step":
                {
                    var row = new VisualElement().Classes("z-step");
                    var dot = new VisualElement().Classes("z-step-dot");
                    if (item.Status == "done") dot.AddToClassList("z-step-dot--done");
                    else if (item.Status is "error" or "declined" or "stopped") dot.AddToClassList("z-step-dot--error");
                    else if (item.Status == "waiting") dot.AddToClassList("z-step-dot--wait");
                    else if (item.Status == "running") dot.AddToClassList("z-step-dot--running");
                    row.Add(dot);
                    var text = item.Text + (item.Status == "running" ? "…" : item.Status == "declined" ? " — declined" : item.Status == "stopped" ? " — stopped" : "");
                    var column = new VisualElement { style = { flexShrink = 1 } };
                    column.Add(new Label(text).Classes("z-step-text"));
                    if (!string.IsNullOrEmpty(item.Detail)) column.Add(new Label(item.Detail).Classes("z-hint"));
                    row.Add(column);
                    return row;
                }
                case "approval":
                    return Approval(item);
                case "changes":
                    return ChangesCard(item.Detail);
                case "ask":
                    return AskCard.Build(item);
                case "question":
                {
                    var card = ZettaUI.Card();
                    card.Add(ZettaUI.Text(item.Text));
                    var options = new VisualElement();
                    foreach (var option in item.Options)
                    {
                        var chosen = option;
                        var button = ZettaUI.Button(option, () => AgentRuntime.Answer(item, chosen), item.Detail == option ? "z-button--primary" : "z-button--ghost");
                        button.style.unityTextAlign = TextAnchor.MiddleLeft;
                        button.SetEnabled(!item.Resolved);
                        options.Add(button);
                    }
                    card.Add(options);
                    return card;
                }
                case "memory":
                {
                    var row = new VisualElement().Classes("z-row");
                    row.Add(ZettaUI.Chip(item.Detail ?? "Remembered", "signal"));
                    row.Add(new Label(item.Text).Classes("z-hint"));
                    return row;
                }
                case "image":
                {
                    var box = new VisualElement();
                    var image = new VisualElement().Classes("z-image-card");
                    ZettaUI.LoadInto(image, item.ImageUrl, "agent-image-" + item.ImageId);
                    box.Add(image);
                    var row = new VisualElement().Classes("z-row");
                    row.Add(ZettaUI.Hint(item.Text));
                    row.Add(ZettaUI.Button("Save to project", () => _ = SaveImage(item), "z-button--ghost", "z-button--small"));
                    box.Add(row);
                    return box;
                }
                case "event":
                    return new VisualElement().Classes("z-msg", "z-msg--event").Children(new Label(item.Text).Classes("z-muted"));
                case "error":
                {
                    var box = new VisualElement().Classes("z-msg", "z-error-card").Children(new Label(item.Text).Classes("z-error"));
                    // A key the provider refused, an empty balance, a model the key can't use: the fix is in settings.
                    if (item.Detail is "provider_key_invalid" or "provider_quota" or "provider_model_unavailable" or "provider_key_missing")
                        box.Add(ZettaUI.Button("Open settings", OpenSettings, "z-button--small"));
                    return box;
                }
                default:
                    return new Label(item.Text).Classes("z-hint");
            }
        }

        /// <summary>Why the last undo on a card could not be done, per turn — shown on the card until it is redrawn by a new try.</summary>
        readonly Dictionary<string, string> _undoProblems = new Dictionary<string, string>();

        /// <summary>The changes-card lines opened to their preview ("turn|path"), kept while the card is redrawn.</summary>
        readonly HashSet<string> _openPreviews = new HashSet<string>();

        /// <summary>
        /// What a turn changed — each file, a folder it made, its steps in the scene — with Undo on each line and for all
        /// of it. Redrawn in place when something is undone, here or on another card.
        /// </summary>
        VisualElement ChangesCard(string turn)
        {
            var card = new VisualElement().Classes("z-changes");

            void Draw()
            {
                card.Clear();
                var rows = AgentChanges.Rows(AgentChanges.Load(turn));
                card.Visible(rows.Count > 0);
                if (rows.Count == 0) return;

                var open = rows.Count(r => !r.Settled);
                var head = new VisualElement().Classes("z-row", "z-changes-head");
                head.Add(new Label(ChangesTitle(rows)).Classes("z-changes-title", "z-grow"));
                if (open > 0)
                {
                    var rest = open < rows.Count;
                    var keepAll = ZettaUI.Button(rest ? "Keep the rest" : "Keep all", () => AgentChanges.KeepAll(turn), "z-button--ghost", "z-button--small");
                    keepAll.tooltip = "Keep what this message changed — what happens anyway if you leave it";
                    head.Add(keepAll);
                    var all = ZettaUI.Button(rest ? "Undo the rest" : "Undo all", () => TakeBack(true, null), "z-button--ghost", "z-button--small");
                    all.tooltip = rest ? "Put back what you haven't kept or undone yet" : "Put everything this message changed back the way it was";
                    head.Add(all);
                }
                else
                {
                    head.Add(new Label(SettledSummary(rows)).Classes("z-hint", "z-changes-summary"));
                    // Kept is not final: what was kept can still all be put back.
                    if (rows.Any(r => r.Kept))
                    {
                        var all = ZettaUI.Button("Undo all", () => TakeBack(true, null, includeKept: true), "z-button--ghost", "z-button--small");
                        all.tooltip = "Put back everything this message changed, kept or not";
                        head.Add(all);
                    }
                }
                card.Add(head);

                foreach (var row in rows)
                {
                    var key = turn + "|" + row.Path;
                    var expanded = _openPreviews.Contains(key);
                    var line = new VisualElement().Classes("z-row", "z-changes-row");
                    if (row.Undone) line.AddToClassList("z-changes-row--undone");
                    if (row.Kept) line.AddToClassList("z-changes-row--kept");
                    line.tooltip = expanded ? "Hide the preview" : row.Kind == "scene" ? "Show the agent's steps in the scene" : "Show what changed";
                    // The whole line opens and closes its preview — except its Undo button.
                    line.RegisterCallback<ClickEvent>(e =>
                    {
                        if (e.target is Button) return;
                        if (!_openPreviews.Remove(key)) _openPreviews.Add(key);
                        Draw();
                    });
                    line.Add(new Label(expanded ? "▾" : "▸").Classes("z-changes-chevron"));
                    line.Add(ZettaUI.Chip(ChangeLabel(row.Kind), ChangeVariant(row.Kind)).Classes("z-changes-kind"));
                    line.Add(new Label(row.Path).Classes("z-changes-path", "z-grow"));
                    if (!string.IsNullOrEmpty(row.Detail)) line.Add(new Label(row.Detail).Classes("z-hint", "z-changes-detail"));
                    if (row.Undone) line.Add(new Label("Undone").Classes("z-hint", "z-changes-detail"));
                    else
                    {
                        if (row.Kept) line.Add(new Label("Kept").Classes("z-hint", "z-changes-detail"));
                        else
                        {
                            var keep = ZettaUI.Button("Keep", () => AgentChanges.KeepRow(turn, row), "z-button--ghost", "z-button--small");
                            keep.tooltip = "Keep this change — what happens anyway if you leave it";
                            line.Add(keep);
                        }
                        var undo = ZettaUI.Button("Undo", () => TakeBack(false, row), "z-button--ghost", "z-button--small");
                        undo.tooltip = row.Kind switch
                        {
                            "created" or "folder" => "Move it to the trash",
                            "deleted" => "Put it back",
                            "moved" => "Move it back",
                            "scene" => "Undo the agent's steps in the scene",
                            _ => "Put back the version from before this message",
                        };
                        line.Add(undo);
                    }
                    card.Add(line);
                    if (expanded) card.Add(ChangePreview(turn, row));
                }

                if (_undoProblems.TryGetValue(turn, out var problem) && problem != null)
                    card.Add(new Label(problem).Classes("z-error", "z-changes-problem"));
            }

            void TakeBack(bool everything, AgentChanges.Row row, bool includeKept = false)
            {
                if (AgentRuntime.Busy)
                {
                    ShowNotification(new GUIContent("Wait for the agent to finish, or stop it first."));
                    return;
                }
                if (everything && !EditorUtility.DisplayDialog("Undo these changes?",
                        "Everything this message changed that you haven't kept goes back to how it was before it: files are restored or moved to the trash, and its steps in the scene are undone.",
                        "Undo all", "Cancel"))
                    return;
                _undoProblems[turn] = everything ? AgentChanges.UndoAll(turn, includeKept) : AgentChanges.UndoRow(turn, row);
                Draw();
            }

            Draw();
            ZettaUI.WhileOnScreen(card, h => AgentChanges.Changed += h, h => AgentChanges.Changed -= h, Draw);
            return card;
        }

        static Font _mono;
        static bool _monoLooked;

        /// <summary>The Editor's own monospace font, so code lines up; null when this Unity does not ship it.</summary>
        static Font Mono
        {
            get
            {
                if (_monoLooked) return _mono;
                _monoLooked = true;
                try
                {
                    _mono = EditorGUIUtility.Load("Fonts/RobotoMono/RobotoMono-Regular.ttf") as Font;
                }
                catch (Exception)
                {
                    _mono = null;
                }
                return _mono;
            }
        }

        /// <summary>
        /// A changes-card line opened: the file's diff (or its contents, when new or deleted), the agent's scene steps,
        /// or what is in a new folder — with Open to see it in full.
        /// </summary>
        static VisualElement ChangePreview(string turn, AgentChanges.Row row)
        {
            var box = new VisualElement().Classes("z-diff");
            var preview = AgentChanges.PreviewOf(turn, row);

            var head = new VisualElement().Classes("z-row", "z-diff-head");
            if (preview.Added + preview.Removed > 0)
            {
                if (preview.Added > 0) head.Add(new Label("+" + preview.Added).Classes("z-diff-added"));
                if (preview.Removed > 0) head.Add(new Label("−" + preview.Removed).Classes("z-diff-removed"));
            }
            head.Add(ZettaUI.Spacer());
            var openPath = row.File?.MovedTo != null && !row.File.Undone ? row.File.MovedTo : row.File?.Path ?? (row.Kind == "folder" ? row.Path.TrimEnd('/') : null);
            var asset = openPath != null ? AssetDatabase.LoadMainAssetAtPath(openPath) : null;
            if (asset != null)
            {
                var show = ZettaUI.Button("Show", () => EditorGUIUtility.PingObject(asset), "z-button--ghost", "z-button--small");
                show.tooltip = "Find it in the Project window";
                head.Add(show);
                if (row.Kind != "folder")
                {
                    var open = ZettaUI.Button("Open", () => AssetDatabase.OpenAsset(asset), "z-button--ghost", "z-button--small");
                    open.tooltip = "Open it in its editor (scripts in your code editor)";
                    head.Add(open);
                }
            }
            if (head.childCount > 1) box.Add(head);
            if (preview.Note != null) box.Add(new Label(preview.Note).Classes("z-hint", "z-diff-note"));

            if (preview.Lines.Count > 0)
            {
                var scroll = new ScrollView(ScrollViewMode.VerticalAndHorizontal).Classes("z-diff-lines");
                if (Mono != null) scroll.style.unityFontDefinition = FontDefinition.FromFont(Mono);
                var numbered = preview.Lines.Any(l => l.Op is '+' or '-' or ' ');
                foreach (var line in preview.Lines)
                {
                    var view = new VisualElement().Classes("z-diff-line");
                    switch (line.Op)
                    {
                        case '+': view.AddToClassList("z-diff-line--add"); break;
                        case '-': view.AddToClassList("z-diff-line--del"); break;
                        case '~': view.AddToClassList("z-diff-line--fold"); break;
                    }
                    if (numbered)
                    {
                        view.Add(new Label(line.Op is '-' or ' ' ? line.Old.ToString() : "").Classes("z-diff-num"));
                        view.Add(new Label(line.Op is '+' or ' ' ? line.New.ToString() : "").Classes("z-diff-num"));
                    }
                    var sign = line.Op switch { '+' => "+", '-' => "−", '~' => "⋯", '•' => "•", _ => " " };
                    view.Add(new Label(sign).Classes("z-diff-sign"));
                    // Plain text: code is full of < and > that rich text would read as tags.
                    var text = new Label((line.Text ?? "").Replace("\t", "    ")) { enableRichText = false };
                    text.AddToClassList("z-diff-text");
                    view.Add(text);
                    scroll.Add(view);
                }
                box.Add(scroll);
            }
            if (preview.More > 0)
                box.Add(new Label($"… and {preview.More} more changed lines — open the file to see them all.").Classes("z-hint", "z-diff-note"));
            return box;
        }

        /// <summary>"All kept", "All undone", or "2 kept · 1 undone" once every line is decided.</summary>
        static string SettledSummary(List<AgentChanges.Row> rows)
        {
            var kept = rows.Count(r => r.Kept);
            var undone = rows.Count(r => r.Undone);
            return undone == 0 ? "All kept" : kept == 0 ? "All undone" : $"{kept} kept · {undone} undone";
        }

        static string ChangesTitle(List<AgentChanges.Row> rows)
        {
            var files = rows.Count(r => r.Kind != "scene");
            var scene = rows.Any(r => r.Kind == "scene");
            var what = files == 0 ? "" : files == 1 ? "1 file" : $"{files} files";
            return files == 0 ? "Changed the scene" : scene ? $"Changed {what} and the scene" : $"Changed {what}";
        }

        static string ChangeLabel(string kind) => kind switch
        {
            "created" => "New",
            "folder" => "New folder",
            "deleted" => "Deleted",
            "moved" => "Moved",
            "scene" => "Scene",
            _ => "Edited",
        };

        static string ChangeVariant(string kind) => kind switch
        {
            "created" or "folder" => "success",
            "deleted" => "error",
            "scene" => "signal",
            _ => "amber",
        };

        VisualElement Approval(ChatItem item)
        {
            var card = new VisualElement().Classes("z-approval");
            var kind = item.ApprovalKind switch
            {
                "generate" => "Spend credits?",
                "destructive" => "Remove this?",
                _ => "Allow this change?",
            };
            card.Add(ZettaUI.Chip(kind, item.ApprovalKind == "destructive" ? "error" : "amber"));
            card.Add(ZettaUI.Title(item.Text));
            if (!string.IsNullOrEmpty(item.Detail))
            {
                var detail = new TextField { multiline = true, value = item.Detail, isReadOnly = true };
                detail.style.maxHeight = 220;
                card.Add(detail);
            }
            if (item.Credits.HasValue)
            {
                var credits = ZettaAuth.User?.Credits.ThreeD.Total;
                card.Add(ZettaUI.Text($"{item.Credits.Value} credits" + (credits.HasValue ? $" · you have {credits.Value:N0}" : "")));
            }
            if (item.Resolved)
            {
                card.Add(ZettaUI.Hint(item.Status == "done" ? "Allowed" : item.Status == "stopped" ? "Stopped" : "Declined"));
                return card;
            }
            var row = new VisualElement().Classes("z-row");
            row.Add(ZettaUI.Button(item.Options.FirstOrDefault() ?? "Allow", () => AgentRuntime.Resolve(item, true),
                item.ApprovalKind == "generate" ? "z-button--signal" : "z-button--primary"));
            row.Add(ZettaUI.Button("Decline", () => AgentRuntime.Resolve(item, false), "z-button--ghost"));
            card.Add(row);
            return card;
        }

        async Task SaveImage(ChatItem item)
        {
            var name = ZettaImporter.Slug(item.Text) + ".png";
            var path = EditorUtility.SaveFilePanelInProject("Save image", name, "png", "Where to save the image", ZettaProjectSettings.Instance.ImagesFolder);
            if (string.IsNullOrEmpty(path)) return;
            try
            {
                var bytes = await ZettaHttp.DownloadAsync(item.ImageUrl);
                var saved = ZettaImporter.SaveImage(bytes, path);
                EditorGUIUtility.PingObject(AssetDatabase.LoadAssetAtPath<Texture2D>(saved));
            }
            catch (Exception e)
            {
                ShowNotification(new GUIContent("Could not save: " + e.Message));
            }
        }

        // ─── History / settings ─────────────────────────────────────────────────────

        // ─── History ────────────────────────────────────────────────────────────────

        VisualElement _historyPanel;
        Button _historyButton;
        Button _memoryButton;
        List<JToken> _sessions = new List<JToken>();
        string _historyFilter = "";
        VisualElement _historyList;

        void ToggleHistory()
        {
            var show = _historyPanel.style.display == DisplayStyle.None;
            ShowSidePanel(show ? _historyPanel : null);
            if (show) _ = LoadHistory();
        }

        /// <summary>One side panel at a time (History or Memory); its toolbar button shows it is open.</summary>
        void ShowSidePanel(VisualElement panel)
        {
            _historyPanel.Visible(panel == _historyPanel);
            _memoryPanel.Visible(panel == _memoryPanel);
            _historyButton.EnableInClassList("z-nav--active", panel == _historyPanel);
            _memoryButton.EnableInClassList("z-nav--active", panel == _memoryPanel);
        }

        async Task LoadHistory()
        {
            _historyPanel.Clear();
            var head = new VisualElement().Classes("z-history-head");
            head.Add(new Label("Conversations in this project").Classes("z-section-title", "z-grow"));
            head.Add(ZettaUI.Button("+ New chat", () =>
            {
                AgentRuntime.NewChat();
                RenderTranscript();
                ShowSidePanel(null);
                _input?.Focus();
            }, "z-button--small"));
            head.Add(ZettaUI.Button("✕", () => ShowSidePanel(null), "z-button--ghost", "z-button--small"));
            _historyPanel.Add(head);

            var search = ZettaUI.TextInput("Search conversations", _historyFilter, 120);
            search.AddToClassList("z-history-search");
            search.RegisterValueChangedCallback(e =>
            {
                _historyFilter = e.newValue ?? "";
                RenderHistory();
            });
            _historyPanel.Add(search);

            var scroll = new ScrollView(ScrollViewMode.Vertical).Classes("z-history-list");
            _historyList = new VisualElement();
            scroll.Add(_historyList);
            _historyPanel.Add(scroll);
            _historyList.Add(ZettaUI.Hint("Loading your conversations…"));

            try
            {
                var json = await ZettaHttp.GetAsync("/unityAgent/sessions?projectId=" + Uri.EscapeDataString(ZettaProjectSettings.Instance.ProjectId));
                _sessions = (json["sessions"] as JArray ?? new JArray()).ToList();
                RenderHistory();
            }
            catch (ZettaApiException e)
            {
                _historyList.Clear();
                _historyList.Add(new Label(e.Message).Classes("z-error"));
            }
        }

        void RenderHistory()
        {
            if (_historyList == null) return;
            _historyList.Clear();
            var filter = _historyFilter.Trim();
            var rows = _sessions
                .Where(t => filter.Length == 0 || TitleOf(t).IndexOf(filter, StringComparison.OrdinalIgnoreCase) >= 0)
                .ToList();
            if (_sessions.Count == 0)
            {
                _historyList.Add(ZettaUI.Hint("No conversations yet — your chats in this project will appear here."));
                return;
            }
            if (rows.Count == 0)
            {
                _historyList.Add(ZettaUI.Hint("No conversation matches that search."));
                return;
            }

            string lastGroup = null;
            foreach (var session in rows)
            {
                var when = session["lastMessageAt"]?.Value<DateTime?>()?.ToLocalTime();
                var group = GroupOf(when);
                if (group != lastGroup)
                {
                    _historyList.Add(new Label(group).Classes("z-history-group"));
                    lastGroup = group;
                }
                _historyList.Add(HistoryRow(session, when));
            }
        }

        static string TitleOf(JToken session)
        {
            var title = session["title"]?.ToString();
            return string.IsNullOrWhiteSpace(title) ? $"Conversation #{session["id"]}" : title.Trim();
        }

        static string GroupOf(DateTime? when)
        {
            if (when == null) return "Earlier";
            var days = (DateTime.Now.Date - when.Value.Date).TotalDays;
            if (days < 1) return "Today";
            if (days < 2) return "Yesterday";
            if (days < 7) return "This week";
            return "Earlier";
        }

        VisualElement HistoryRow(JToken session, DateTime? when)
        {
            var id = session["id"]?.Value<long>() ?? 0;
            var current = id == AgentRuntime.State.SessionId;
            var row = new VisualElement().Classes("z-history-row");
            row.EnableInClassList("z-history-row--active", current);

            var text = new VisualElement().Classes("z-history-text");
            var title = new Label(TitleOf(session)).Classes("z-history-title");
            title.tooltip = TitleOf(session);
            text.Add(title);
            var messages = session["messageCount"]?.Value<int?>() ?? 0;
            var time = when == null ? "" : (GroupOf(when) is "Today" or "Yesterday" ? when.Value.ToString("HH:mm") : when.Value.ToString("MMM d, HH:mm"));
            text.Add(new Label(string.Join(" · ", new[] { time, messages > 0 ? $"{messages} message{(messages == 1 ? "" : "s")}" : null }
                .Where(x => !string.IsNullOrEmpty(x)))).Classes("z-history-meta"));
            row.Add(text);

            if (current) row.Add(ZettaUI.Chip("Open", "signal"));
            if (session["awaiting"]?.Value<bool>() == true)
            {
                var waiting = ZettaUI.Chip("Waiting", "amber");
                waiting.tooltip = "It paused waiting on the Editor — open it to carry on";
                row.Add(waiting);
            }

            var delete = ZettaUI.Button("✕", null, "z-button--ghost", "z-button--small", "z-history-delete");
            delete.tooltip = "Delete this conversation";
            delete.clicked += () => _ = DeleteSession(id, TitleOf(session));
            row.Add(delete);

            row.RegisterCallback<ClickEvent>(e =>
            {
                if (e.target is Button) return;
                ShowSidePanel(null);
                if (!current) _ = AgentRuntime.OpenSession(id);
            });
            return row;
        }

        async Task DeleteSession(long id, string title)
        {
            if (!EditorUtility.DisplayDialog("Delete conversation", $"Delete “{title}”? The memories it saved are kept.", "Delete", "Cancel")) return;
            try
            {
                await ZettaHttp.DeleteAsync($"/unityAgent/sessions/{id}");
                if (id == AgentRuntime.State.SessionId)
                {
                    AgentRuntime.NewChat();
                    RenderTranscript();
                }
                _sessions.RemoveAll(t => (t["id"]?.Value<long>() ?? 0) == id);
                RenderHistory();
            }
            catch (ZettaApiException e)
            {
                ShowNotification(new GUIContent(e.Message));
            }
        }

        void SetAutonomy(string value)
        {
            ZettaPrefs.AgentAutonomy = value;
            _autonomy?.SetValueWithoutNotify(value);
            if (_autonomy != null) SyncAutonomy();
        }

        void ShowSettings()
        {
            var menu = new GenericMenu();
            menu.AddItem(new GUIContent("Mode/Agent — works in the Editor"), ZettaPrefs.AgentMode == "agent", () => ZettaPrefs.AgentMode = "agent");
            menu.AddItem(new GUIContent("Mode/Ask — reads and explains, changes nothing"), ZettaPrefs.AgentMode == "ask", () => ZettaPrefs.AgentMode = "ask");
            menu.AddItem(new GUIContent("Autonomy/Ask first — before deleting or spending credits"), !ZettaPrefs.AgentFullAutonomy, () => SetAutonomy("ask"));
            menu.AddItem(new GUIContent("Autonomy/Full autonomy — never asks, runs to the end"), ZettaPrefs.AgentFullAutonomy, () => SetAutonomy("full"));
            menu.AddItem(new GUIContent("Adaptive thinking"), ZettaPrefs.AgentAdaptive, () => ZettaPrefs.AgentAdaptive = !ZettaPrefs.AgentAdaptive);
            menu.AddSeparator("");
            menu.AddDisabledItem(new GUIContent($"Tokens this chat: {AgentRuntime.State.TokensThisSession:N0}"));
            menu.AddItem(new GUIContent("Open 3D Studio"), false, () => EditorApplication.ExecuteMenuItem("Window/Zetta 3D/3D Studio"));
            menu.ShowAsContext();
        }

        // ─── Memory ─────────────────────────────────────────────────────────────────

        void ToggleMemory()
        {
            var show = _memoryPanel.style.display == DisplayStyle.None;
            ShowSidePanel(show ? _memoryPanel : null);
            if (show) _ = LoadMemory();
        }

        async Task LoadMemory()
        {
            _memoryPanel.Clear();
            var scroll = new ScrollView();
            scroll.style.maxHeight = 320;
            var body = new VisualElement();
            body.style.paddingLeft = 0;
            body.style.paddingRight = 0;
            scroll.Add(body);
            _memoryPanel.Add(scroll);
            body.Add(ZettaUI.Hint("Loading…"));

            JToken json;
            var projectId = ZettaProjectSettings.Instance.ProjectId;
            try
            {
                json = await ZettaHttp.GetAsync("/unityAgent/memories?projectId=" + Uri.EscapeDataString(projectId));
            }
            catch (ZettaApiException e)
            {
                body.Clear();
                body.Add(new Label(e.Message).Classes("z-error"));
                return;
            }
            body.Clear();

            body.Add(ZettaUI.Title("Project brief"));
            body.Add(ZettaUI.Hint("The agent's summary of this game — genre, core loop, systems, decisions. It reads this every turn."));
            var brief = ZettaUI.TextArea("No brief yet — the agent writes one as you build.", json["brief"]?.ToString() ?? "", 4000);
            body.Add(brief);
            body.Add(ZettaUI.Button("Save brief", async () =>
            {
                try
                {
                    await ZettaHttp.PutAsync("/unityAgent/brief", new JObject { ["projectId"] = projectId, ["brief"] = brief.value });
                    ShowNotification(new GUIContent("Brief saved"));
                }
                catch (ZettaApiException e)
                {
                    ShowNotification(new GUIContent(e.Message));
                }
            }, "z-button--small"));

            body.Add(ZettaUI.Title("What the agent remembers"));
            var memories = json["memories"] as JArray ?? new JArray();
            if (memories.Count == 0) body.Add(ZettaUI.Hint("Nothing yet. Tell it your conventions (“we use the new Input System”, “scripts go in Assets/_Game/Scripts”) and it will remember."));
            foreach (var memory in memories)
            {
                var id = memory["id"]?.Value<long>() ?? 0;
                var row = new VisualElement().Classes("z-memory");
                row.Add(ZettaUI.Chip(memory["scope"]?.ToString() == "global" ? "All projects" : "This project"));
                row.Add(ZettaUI.Chip(memory["category"]?.ToString()?.Replace('_', ' ') ?? "fact"));
                var text = new Label(memory["text"]?.ToString()).Classes("z-text");
                text.style.flexShrink = 1;
                text.style.flexGrow = 1;
                row.Add(text);
                row.Add(ZettaUI.Button("✕", async () =>
                {
                    try
                    {
                        await ZettaHttp.DeleteAsync($"/unityAgent/memories/{id}");
                        row.RemoveFromHierarchy();
                    }
                    catch (ZettaApiException e)
                    {
                        ShowNotification(new GUIContent(e.Message));
                    }
                }, "z-button--ghost", "z-button--small"));
                body.Add(row);
            }

            var add = ZettaUI.TextInput("Add something to remember…", "", 300);
            var scope = new PopupField<string>(new List<string> { "project", "global" }, 0, s => s == "project" ? "This project" : "All projects", s => s == "project" ? "This project" : "All projects");
            var addRow = new VisualElement().Classes("z-row");
            add.style.flexGrow = 1;
            addRow.Add(add);
            addRow.Add(scope);
            addRow.Add(ZettaUI.Button("Add", async () =>
            {
                if (string.IsNullOrWhiteSpace(add.value)) return;
                try
                {
                    await ZettaHttp.PostAsync("/unityAgent/memories", new JObject
                    {
                        ["text"] = add.value.Trim(),
                        ["scope"] = scope.value,
                        ["category"] = "preference",
                        ["projectId"] = projectId,
                    });
                    await LoadMemory();
                }
                catch (ZettaApiException e)
                {
                    ShowNotification(new GUIContent(e.Message));
                }
            }, "z-button--small"));
            body.Add(addRow);
        }
    }
}
