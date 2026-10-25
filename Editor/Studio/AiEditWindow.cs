using System;
using System.Linq;
using System.Threading.Tasks;
using UnityEditor;
using UnityEngine;
using UnityEngine.UIElements;

namespace Zetta3D.Editor.Studio
{
    /// <summary>
    /// AI edit — change a picture in a 3D input with a sentence before it becomes 3D (the web Studio's
    /// ai-edit-dialog.tsx). Edits build on each other; nothing reaches the panel until the accept button.
    /// A paid edit (never a pipeline step). On a plan without image editing the picture is shown large,
    /// with the way to unlock it in place of the field.
    /// </summary>
    public class AiEditWindow : EditorWindow
    {
        const int MaxInstruction = 2000;
        const string LockedText = "Change this picture with a sentence on a paid plan.";

        byte[] _original;
        string _originalType;
        byte[] _result;
        string _resultType;
        string _acceptLabel;
        Action<byte[], string> _onAccept;
        bool _busy;

        VisualElement _image;
        Label _edited;
        Label _status;
        TextField _instruction;
        Button _submit;
        Button _accept;
        Label _error;

        /// <summary>Opens the popup on a picture. <paramref name="onAccept"/> gets the edited bytes and type.</summary>
        public static AiEditWindow Open(string title, byte[] bytes, string contentType, Action<byte[], string> onAccept,
            string acceptLabel = "Use this to generate 3D")
        {
            if (bytes == null || bytes.Length == 0) return null;
            var window = CreateInstance<AiEditWindow>();
            window.titleContent = new GUIContent(string.IsNullOrEmpty(title) ? "AI edit" : title);
            window._original = bytes;
            window._originalType = string.IsNullOrEmpty(contentType) ? "image/png" : contentType;
            window._onAccept = onAccept;
            window._acceptLabel = acceptLabel;
            window.minSize = new Vector2(380, 480);
            window.position = new Rect(window.position.x, window.position.y, 560, 680);
            window.ShowUtility();
            window.Build();
            return window;
        }

        static bool Locked => StudioContext.Shared.Gates2D?.AllowsImageEditing == false;

        void Build()
        {
            var root = ZettaUI.Root(rootVisualElement);
            root.Clear();
            _shownBytes = null;
            var body = new VisualElement().Classes("z-ai-edit");
            body.Add(ZettaUI.Title(titleContent.text));
            body.Add(ZettaUI.Hint(Locked ? "A closer look at the picture." : "Describe a change to the picture. Each edit builds on the last."));

            _image = new VisualElement().Classes("z-ai-edit-image");
            _edited = ZettaUI.Chip("Edited", "signal").Classes("z-ai-edit-badge").Visible(false);
            _image.Add(_edited);
            _status = new Label("Editing…").Classes("z-ai-edit-busy").Visible(false);
            _image.Add(_status);
            body.Add(_image);

            if (Locked)
            {
                var lockRow = new VisualElement().Classes("z-lock", "z-row");
                lockRow.Add(new Label(LockedText).Classes("z-grow", "z-text"));
                lockRow.Add(ZettaUI.Button("See plans", () => Application.OpenURL(ZettaConfig.SiteBase + "/pricing"), "z-button--small", "z-button--primary"));
                body.Add(lockRow);
            }
            else
            {
                var row = new VisualElement().Classes("z-row", "z-ai-edit-field");
                _instruction = ZettaUI.TextArea("Make the stripes blue, add a flag on top…", "", MaxInstruction);
                _instruction.AddToClassList("z-grow");
                _instruction.RegisterValueChangedCallback(_ => Sync());
                _instruction.RegisterCallback<KeyDownEvent>(e =>
                {
                    if ((e.keyCode is KeyCode.Return or KeyCode.KeypadEnter) && !e.shiftKey)
                    {
                        e.StopPropagation();
                        _ = Submit();
                    }
                }, TrickleDown.TrickleDown);
                row.Add(_instruction);
                _submit = ZettaUI.Button("Submit", () => _ = Submit(), "z-button--small");
                row.Add(_submit);
                body.Add(row);
            }

            _error = new Label().Classes("z-error").Visible(false);
            body.Add(_error);

            var foot = new VisualElement().Classes("z-row", "z-ai-edit-foot");
            if (!Locked)
            {
                _accept = ZettaUI.Button(_acceptLabel, Accept, "z-button--primary", "z-grow");
                foot.Add(_accept);
            }
            var cancel = ZettaUI.Button(Locked ? "Close" : "Cancel", Close, "z-button--ghost");
            cancel.tooltip = "Keep the original picture";
            foot.Add(cancel);
            body.Add(foot);
            root.Add(body);
            Sync();
        }

        byte[] _shownBytes;
        Texture2D _shownTexture;

        void CreateGUI()
        {
            // Reopened after a script reload with nothing to edit: there is no picture to come back to.
            if (_original == null) EditorApplication.delayCall += () => { if (this != null) Close(); };
            else if (rootVisualElement.childCount == 0) Build();
        }

        void Sync()
        {
            if (_image == null) return;
            var shown = _result ?? _original;
            if (!ReferenceEquals(shown, _shownBytes))
            {
                if (_shownTexture != null) DestroyImmediate(_shownTexture);
                _shownBytes = shown;
                _shownTexture = ZettaUI.TextureFrom(shown);
                _image.style.backgroundImage = new StyleBackground(_shownTexture);
            }
            _edited.Visible(_result != null && !_busy);
            _status.Visible(_busy);
            if (_instruction != null)
            {
                _instruction.SetEnabled(!_busy);
                ZettaUI.SetPlaceholder(_instruction, _result != null ? "Another change?" : "Make the stripes blue, add a flag on top…");
            }
            if (_submit != null)
            {
                _submit.text = _busy ? "Submitting…" : "Submit";
                _submit.SetEnabled(!_busy && Instruction != "");
            }
            _accept?.SetEnabled(_result != null && !_busy);
        }

        string Instruction => (_instruction?.value ?? "").Trim();

        /// <summary>The edit call for a 3D input picture: an object on the converter's square (web use-ai-edit-image.ts).</summary>
        public static Task<ImageResult> EditAsync(string instruction, byte[] bytes, string contentType) =>
            ZettaApi.EditImageAsync(instruction, InlineImage.From(bytes, contentType), for3D: true);

        async Task Submit()
        {
            var instruction = Instruction;
            if (_busy || instruction == "" || Locked) return;
            _busy = true;
            _error.Visible(false);
            Sync();
            try
            {
                // Each edit starts from the last result, the way a picture is refined.
                var result = await EditAsync(instruction, _result ?? _original, _result != null ? _resultType : _originalType);
                var picture = result?.Images?.FirstOrDefault(i => i.Data.Length > 0);
                if (picture == null)
                    throw new StudioProblem(string.IsNullOrWhiteSpace(result?.Text) ? "The image service returned no image for that edit." : result.Text.Trim());
                _result = picture.Data;
                _resultType = picture.ContentType ?? "image/png";
                if (_instruction != null) _instruction.value = "";
                StudioContext.RefreshCredits();
            }
            catch (ZettaApiException e)
            {
                ShowError(ZettaCatalog.Explain(e));
            }
            catch (Exception e)
            {
                ShowError(e.Message);
            }
            finally
            {
                _busy = false;
                if (this != null) Sync();
            }
        }

        void ShowError(string message)
        {
            if (_error == null) return;
            _error.text = message ?? "";
            _error.Visible(!string.IsNullOrEmpty(message));
        }

        void Accept()
        {
            if (_result == null || _busy) return;
            var bytes = _result;
            var type = _resultType;
            var accept = _onAccept;
            _onAccept = null;
            Close();
            accept?.Invoke(bytes, type);
        }

        void OnDestroy()
        {
            _onAccept = null;
            if (_shownTexture != null) DestroyImmediate(_shownTexture);
        }

        /// <summary>The picture's file name kept, the extension following the new type.</summary>
        public static string Renamed(string original, string contentType)
        {
            var name = string.IsNullOrEmpty(original) ? "picture" : System.IO.Path.GetFileNameWithoutExtension(original);
            if (string.IsNullOrEmpty(name)) name = "picture";
            return name + StudioAssets.ExtensionFor(contentType);
        }
    }
}
