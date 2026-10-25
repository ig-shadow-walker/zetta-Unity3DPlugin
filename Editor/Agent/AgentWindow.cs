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
    /// <summary>The Zetta Unity Agent — a game-building assistant that sees the scene and works in the Editor.</summary>
    public class AgentWindow : EditorWindow
    {
        [MenuItem("Window/Zetta 3D/Agent", priority = 4)]
        public static void Open()
        {
            var window = GetWindow<AgentWindow>();
            window.titleContent = new GUIContent("Zetta Agent", EditorGUIUtility.IconContent("d_UnityEditor.ConsoleWindow").image);
            window.minSize = new Vector2(320, 360);
            window.Show();
        }

        /// <summary>Brings the window forward when the agent needs the user (an approval card).</summary>
        public static void Reveal()
        {
            if (HasOpenInstances<AgentWindow>()) FocusWindowIfItsOpen<AgentWindow>();
            else Open();
        }

        ScrollView _scroll;
        VisualElement _list;
        TextField _input;
        Button _send;
        Label _phase;
        VisualElement _attachments;
        VisualElement _memoryPanel;
        PopupField<string> _tier;
        PopupField<string> _thinking;
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
        bool _dirty;

        static readonly List<string> Tiers = new List<string> { "auto", "fast", "capable", "intelligent" };
        static readonly List<string> TierLabels = new List<string> { "Auto", "Fast", "Capable", "Intelligent" };
        static readonly List<string> Thinking = new List<string> { "off", "low", "medium", "high", "xhigh", "max" };
        static readonly List<string> ThinkingLabels = new List<string> { "No thinking", "Low", "Medium", "High", "Extra high", "Max" };

        void OnEnable()
        {
            AgentRuntime.Changed += MarkDirty;
            EditorApplication.update += Tick;
        }

        void OnDisable()
        {
            AgentRuntime.Changed -= MarkDirty;
            EditorApplication.update -= Tick;
        }

        void MarkDirty() => _dirty = true;

        void Tick()
        {
            if (!_dirty || _list == null) return;
            _dirty = false;
            RenderTranscript();
        }

        public void CreateGUI()
        {
            var root = ZettaUI.Root(rootVisualElement);
            root.Add(ZettaUI.Header("Zetta Agent"));
            root.Add(ZettaUI.Gate(BuildChat));
            // Escape stops the agent, whatever it is doing — the same as the Stop button.
            root.RegisterCallback<KeyDownEvent>(e =>
            {
                if (e.keyCode != KeyCode.Escape || !AgentRuntime.Busy) return;
                AgentRuntime.Stop();
                e.StopPropagation();
            }, TrickleDown.TrickleDown);
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
            var scene = ZettaUI.Button("📷  Scene", AttachScene, "z-composer-chip");
            scene.tooltip = "Attach a picture of the Scene view";
            attach.Add(scene);
            var game = ZettaUI.Button("🎮  Game", AttachGame, "z-composer-chip");
            game.tooltip = "Attach what the game camera sees (the Game view)";
            attach.Add(game);
            var file = ZettaUI.Button("📎  File", AttachFile, "z-composer-chip");
            file.tooltip = "Attach any file — pictures, PDFs, code and text, models, audio… (or drag files onto the chat). Type @, or select items and press ⇧⌘M (Ctrl+Shift+M), to tag scene objects and project assets.";
            attach.Add(file);
            bar.Add(attach);

            var options = new VisualElement().Classes("z-composer-group");
            _tier = new PopupField<string>(Tiers, Math.Max(0, Tiers.IndexOf(ZettaPrefs.AgentTier)),
                v => TierLabels[Tiers.IndexOf(v)], v => TierLabels[Tiers.IndexOf(v)]);
            _tier.tooltip = "Model — Auto picks the model and how hard it thinks for each message.";
            _tier.AddToClassList("z-composer-popup");
            _tier.RegisterValueChangedCallback(e =>
            {
                ZettaPrefs.AgentTier = e.newValue;
                _thinking.Visible(e.newValue != "auto");
            });
            options.Add(_tier);

            _thinking = new PopupField<string>(Thinking, Math.Max(0, Thinking.IndexOf(ZettaPrefs.AgentThinking)),
                v => ThinkingLabels[Thinking.IndexOf(v)], v => ThinkingLabels[Thinking.IndexOf(v)]);
            _thinking.tooltip = "How hard the model may think. Adaptive (in settings) lets it think less when a turn is simple.";
            _thinking.AddToClassList("z-composer-popup");
            _thinking.RegisterValueChangedCallback(e => ZettaPrefs.AgentThinking = e.newValue);
            _thinking.Visible(ZettaPrefs.AgentTier != "auto");
            options.Add(_thinking);

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
            bar.Add(new Label("⇧↵ new line").Classes("z-composer-hint"));
            _send = ZettaUI.Button("Send", Submit, "z-composer-send");
            bar.Add(_send);
            box.Add(bar);
            return composer;
        }

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
            if (string.IsNullOrWhiteSpace(text)) text = _mentions.Count > 0 && _images.Count == 0 && _files.Count == 0
                ? "Take a look at what I tagged."
                : "Take a look at what I attached.";
            _mentionPopup?.Close();
            _input.value = "";
            // Say what each picture is, so the agent knows a Game view from a Scene view.
            if (_imageLabels.Count > 0) text += "\n\n(Attached: " + string.Join("; ", _imageLabels) + ")";
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
            AddImage(ToolImage.Jpeg(shot.Jpeg), "Scene view");
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
            AddImage(ToolImage.Jpeg(shot.Jpeg), shot.Label);
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
                            AddImage(attachment.Image, attachment.Label);
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
            Open();
            GetWindow<AgentWindow>().TakePending();
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

        void AddImage(ToolImage image, string label)
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
                if (_working.parent != _list || _list.IndexOf(_working) != _list.childCount - 1) _list.Add(_working);
            }
            else _working.RemoveFromHierarchy();
            _phase.Visible(false);
            if (_send != null) _send.text = AgentRuntime.Busy ? "Stop" : "Send";

            var signatureNow = state.Items.Count + "|" + (state.Items.Count > 0 ? Signature(state.Items[state.Items.Count - 1]) : "");
            if (_lastScroll != signatureNow)
            {
                _lastScroll = signatureNow;
                _scroll.schedule.Execute(() => _scroll.scrollOffset = new Vector2(0, float.MaxValue)).StartingIn(30);
            }
        }

        string _lastScroll;

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
                    return new VisualElement().Classes("z-msg").Children(new Label(item.Text).Classes("z-error"));
                default:
                    return new Label(item.Text).Classes("z-hint");
            }
        }

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
