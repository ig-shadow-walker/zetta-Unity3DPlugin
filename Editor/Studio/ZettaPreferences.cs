using System.Collections.Generic;
using UnityEditor;
using UnityEngine;
using UnityEngine.UIElements;

namespace Zetta3D.Editor.Studio
{
    /// <summary>Preferences › Zetta 3D: the account, where the plugin connects, and this project's import settings.</summary>
    public static class ZettaPreferences
    {
        [SettingsProvider]
        public static SettingsProvider Create() =>
            new SettingsProvider("Preferences/Zetta 3D", SettingsScope.User)
            {
                label = "Zetta 3D",
                keywords = new HashSet<string> { "Zetta", "3D", "account", "sign in", "API", "models folder", "images folder", "glb", "fbx" },
                activateHandler = (_, root) => Build(root),
            };

        static void Build(VisualElement host)
        {
            var root = ZettaUI.Root(host);
            var scroll = new ScrollView(ScrollViewMode.Vertical).Classes("z-scroll");
            root.Add(scroll);
            scroll.Add(new Label("Zetta 3D").Classes("z-login-title", "z-left"));

            // ─── Account ───
            var account = ZettaUI.Card(ZettaUI.Title("Account"));
            var accountBody = new VisualElement();
            account.Add(accountBody);
            scroll.Add(account);

            void RenderAccount()
            {
                accountBody.Clear();
                if (ZettaAuth.IsSignedIn)
                {
                    var user = ZettaAuth.User;
                    accountBody.Add(ZettaUI.Text($"Signed in as {user.Name}" + (user.Name != user.Email ? $" ({user.Email})" : "")));
                    accountBody.Add(ZettaUI.Hint($"{user.PlanLabel} plan · {user.Credits.ThreeD.Total:N0} credits"));
                    accountBody.Add(ZettaUI.Row(
                        ZettaUI.Button("Refresh", () => _ = ZettaAuth.RefreshAsync(), "z-button--small"),
                        ZettaUI.Button("Buy credits / upgrade", () => Application.OpenURL(ZettaConfig.SiteBase + "/pricing"), "z-button--small"),
                        ZettaUI.Button("Sign out", () => ZettaAuth.SignOut(), "z-button--small", "z-button--danger")).Classes("z-row--wrap"));
                }
                else if (ZettaAuth.Current == ZettaAuth.State.WaitingForBrowser)
                {
                    accountBody.Add(ZettaUI.Text("Finish signing in in your browser, then press Connect Unity."));
                    accountBody.Add(ZettaUI.Button("Cancel", () => ZettaAuth.CancelLogin(), "z-button--small"));
                }
                else if (ZettaAuth.Current == ZettaAuth.State.Checking)
                {
                    accountBody.Add(ZettaUI.Text("Checking your account…"));
                }
                else
                {
                    accountBody.Add(ZettaUI.Text("Not signed in."));
                    accountBody.Add(ZettaUI.Row(
                        ZettaUI.Button("Sign in with your browser", ZettaAuth.BeginLogin, "z-button--small", "z-button--signal"),
                        ZettaUI.Button("Create a free account", () => Application.OpenURL(ZettaConfig.SiteBase + "/signup"), "z-button--small")).Classes("z-row--wrap"));
                }
                if (!string.IsNullOrEmpty(ZettaAuth.LastError)) accountBody.Add(new Label(ZettaAuth.LastError).Classes("z-error"));
            }

            RenderAccount();
            ZettaAuth.Changed += RenderAccount;
            host.RegisterCallback<DetachFromPanelEvent>(_ => ZettaAuth.Changed -= RenderAccount);

            // ─── Project ───
            var settings = ZettaProjectSettings.Instance;
            var project = ZettaUI.Card(ZettaUI.Title("This project"));
            project.Add(ZettaUI.Hint("Saved in ProjectSettings/Zetta3DSettings.json, so your team shares them."));
            project.Add(FolderField("Models folder", settings.ModelsFolder, v =>
            {
                settings.ModelsFolder = v;
                settings.Save();
            }));
            project.Add(FolderField("Images folder", settings.ImagesFolder, v =>
            {
                settings.ImagesFolder = v;
                settings.Save();
            }));
            project.Add(ZettaUI.Toggle("Place imported models in the open scene", settings.PlaceImportedInScene, v =>
            {
                settings.PlaceImportedInScene = v;
                settings.Save();
            }));
            project.Add(ZettaUI.Dropdown("Preferred format", new List<string> { "glb", "fbx" }, new List<string> { "GLB (glTFast)", "FBX (native)" }, settings.PreferredFormat, v =>
            {
                settings.PreferredFormat = v;
                settings.Save();
            }));
            project.Add(ZettaUI.Hint("When a result has both, this format is imported. GLB needs the glTFast package; FBX imports natively and is best for Mecanim Humanoid rigs."));
            scroll.Add(project);

            // ─── Connection ───
            var connection = ZettaUI.Card(ZettaUI.Title("Connection"));
            connection.Add(ZettaUI.Hint("Only change these if you are developing against your own Zetta 3D server."));
            var api = ZettaUI.TextInput("API base", ZettaConfig.ApiBase, 300);
            var site = ZettaUI.TextInput("Site base", ZettaConfig.SiteBase, 300);
            var apiField = Labelled("API base", api);
            var siteField = Labelled("Site base", site);
            var status = ZettaUI.Hint("");

            void RenderStatus() => status.text = ZettaConfig.IsProduction ? "Connected to production." : $"Using {ZettaConfig.ApiBase}";

            api.RegisterCallback<FocusOutEvent>(_ => Apply());
            site.RegisterCallback<FocusOutEvent>(_ => Apply());

            void Apply()
            {
                var changed = api.value.Trim().TrimEnd('/') != ZettaConfig.ApiBase || site.value.Trim().TrimEnd('/') != ZettaConfig.SiteBase;
                if (!changed) return;
                ZettaConfig.ApiBase = api.value;
                ZettaConfig.SiteBase = site.value;
                api.SetValueWithoutNotify(ZettaConfig.ApiBase);
                site.SetValueWithoutNotify(ZettaConfig.SiteBase);
                StudioContext.Shared.Reset();
                RenderStatus();
                // A token is only good on the server that issued it.
                if (ZettaAuth.IsSignedIn) ZettaAuth.SignOut("The server changed. Sign in again.");
            }

            connection.Add(apiField);
            connection.Add(siteField);
            connection.Add(status);
            connection.Add(ZettaUI.Button("Reset to production", () =>
            {
                var wasProduction = ZettaConfig.IsProduction && ZettaConfig.SiteBase == ZettaConfig.ProductionSite;
                ZettaConfig.ResetToProduction();
                api.SetValueWithoutNotify(ZettaConfig.ApiBase);
                site.SetValueWithoutNotify(ZettaConfig.SiteBase);
                RenderStatus();
                if (!wasProduction)
                {
                    StudioContext.Shared.Reset();
                    if (ZettaAuth.IsSignedIn) ZettaAuth.SignOut("The server changed. Sign in again.");
                }
            }, "z-button--small"));
            RenderStatus();
            scroll.Add(connection);

            scroll.Add(ZettaUI.Hint($"Zetta 3D for Unity {ZettaConfig.Version}").Classes("z-pad"));
        }

        static VisualElement Labelled(string label, VisualElement field)
        {
            var box = new VisualElement();
            box.Add(ZettaUI.FieldLabel(label));
            box.Add(field);
            return box;
        }

        static VisualElement FolderField(string label, string value, System.Action<string> changed)
        {
            var box = new VisualElement();
            box.Add(ZettaUI.FieldLabel(label));
            var row = new VisualElement().Classes("z-row");
            var field = ZettaUI.TextInput(label, value, 260);
            field.AddToClassList("z-grow");
            var error = new Label().Classes("z-error").Visible(false);

            void Commit(string text)
            {
                text = (text ?? "").Replace('\\', '/').Trim().TrimEnd('/');
                if (text != "Assets" && !text.StartsWith("Assets/"))
                {
                    error.text = "Choose a folder inside Assets/.";
                    error.Visible(true);
                    return;
                }
                error.Visible(false);
                field.SetValueWithoutNotify(text);
                changed(text);
            }

            field.RegisterCallback<FocusOutEvent>(_ => Commit(field.value));
            row.Add(field);
            row.Add(ZettaUI.Button("Choose…", () =>
            {
                var absolute = EditorUtility.OpenFolderPanel(label, Application.dataPath, "");
                if (string.IsNullOrEmpty(absolute)) return;
                absolute = absolute.Replace('\\', '/');
                var assets = Application.dataPath.Replace('\\', '/');
                if (!absolute.StartsWith(assets))
                {
                    error.text = "Choose a folder inside this project's Assets folder.";
                    error.Visible(true);
                    return;
                }
                Commit("Assets" + absolute.Substring(assets.Length));
            }, "z-button--small"));
            box.Add(row);
            box.Add(error);
            return box;
        }
    }
}
