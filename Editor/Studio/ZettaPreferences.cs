using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json.Linq;
using UnityEditor;
using UnityEngine;
using UnityEngine.UIElements;
using Zetta3D.Editor.Agent;

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
                keywords = new HashSet<string> { "Zetta", "3D", "account", "sign in", "API", "API key", "OpenAI", "Claude", "Gemini", "DeepSeek", "GLM", "Z.ai", "models folder", "images folder", "glb", "fbx" },
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

            scroll.Add(AgentAccountCard(host));

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

            scroll.Add(VersionCard(host));
        }

        /// <summary>The version installed, whether a newer one is out, and Check for updates / Update.</summary>
        static VisualElement VersionCard(VisualElement host)
        {
            var card = ZettaUI.Card(ZettaUI.Title("Version"));
            var status = ZettaUI.Hint("");
            var check = ZettaUI.Button("Check for updates", null, "z-button--small");
            var update = ZettaUI.Button("Update", ZettaUpdates.Install, "z-button--small", "z-button--signal");
            var notes = ZettaUI.Button("What's new", () => Application.OpenURL(ZettaUpdates.Known?.NotesUrl ?? ZettaUpdates.ReleasesUrl), "z-button--small", "z-button--ghost");
            var checking = false;

            void Render()
            {
                var known = ZettaUpdates.Known;
                status.text = $"Zetta 3D for Unity {ZettaConfig.Version} — " + (
                    checking ? "checking…" :
                    ZettaUpdates.Installing ? $"updating to {known?.Latest}…" :
                    ZettaUpdates.Problem ?? (known == null ? "not checked yet." :
                    known.Required ? $"{known.Latest} is out, and this version must be updated." :
                    known.Available ? $"{known.Latest} is out." : "the latest version."));
                status.EnableInClassList("z-error", known?.Required == true || ZettaUpdates.Problem != null);
                check.SetEnabled(!checking && !ZettaUpdates.Installing);
                update.Visible(known?.Available == true && !ZettaUpdates.Installing);
                notes.Visible(known?.Available == true);
            }

            check.clicked += async () =>
            {
                checking = true;
                Render();
                var found = await ZettaUpdates.CheckAsync();
                checking = false;
                Render();
                if (found == null) status.text = $"Zetta 3D for Unity {ZettaConfig.Version} — couldn't check: Zetta 3D and GitHub didn't answer.";
            };
            card.Add(status);
            card.Add(ZettaUI.Row(check, update, notes).Classes("z-row--wrap"));
            ZettaUI.WhileOnScreen(host, h => ZettaUpdates.Changed += h, h => ZettaUpdates.Changed -= h, Render);
            Render();
            return card;
        }

        // ─── Zetta Agent: Zetta account or the user's own API keys ───

        /// <summary>
        /// What the agent runs on. On "own keys", one row per provider: paste a key, check it, replace or remove
        /// it. The chat box then lists the providers that have a key. Models are picked in the chat box, not here.
        /// </summary>
        static VisualElement AgentAccountCard(VisualElement host)
        {
            var card = ZettaUI.Card(ZettaUI.Title("Zetta Agent — what it runs on"));
            var body = new VisualElement();
            card.Add(body);
            VisualElement savedKeys = null;

            void Render()
            {
                body.Clear();
                savedKeys = null;
                var providers = AgentCatalog.Providers;
                if (providers == null)
                {
                    body.Add(ZettaUI.Hint("The agent runs on your Zetta account. Using your own API keys isn't available on this server yet."));
                    return;
                }

                var own = ZettaPrefs.AgentAccount == "own";
                var names = providers.Select(p => p.Label).ToList();
                var list = names.Count > 1 ? string.Join(", ", names.Take(names.Count - 1)) + " or " + names.Last() : names.FirstOrDefault();

                var choices = new VisualElement().Classes("z-choices");
                choices.Add(AccountChoice("zetta", "Zetta account", "Included with your plan. Auto picks the model and how much it thinks for each message.", !own, Choose));
                var ownChoice = AccountChoice("own", "Your own API key",
                    $"Use your key from {list}. Billed by the provider, not in Zetta credits.", own, Choose);
                savedKeys = new VisualElement().Classes("z-choice-providers");
                ownChoice.Add(savedKeys);
                choices.Add(ownChoice);
                body.Add(choices);
                ShowSavedKeys();
                if (!own) return;

                if (!providers.Any(p => ZettaKeychain.Has(p.Key)))
                    body.Add(new Label("Paste a key for any provider below to start.").Classes("z-hint", "z-hint--warn"));
                foreach (var provider in providers) body.Add(KeyRow(provider));
                body.Add(ZettaUI.Hint(
                    $"Keys are kept in {ZettaKeychain.StoreName}. They are sent to Zetta 3D only to run your messages, and are never saved " +
                    "on our servers. What you use is billed by the provider, not in Zetta credits; making 3D models and pictures still uses " +
                    "Zetta credits. Pick the provider and model in the Agent window, under the message box."));
            }

            void Choose(string account)
            {
                if (ZettaPrefs.AgentAccount == account) return;
                ZettaPrefs.AgentAccount = account;
                AgentCatalog.NotifyChanged();
                Render();
            }

            // Each provider on the own-key choice, ✓ once its key is saved — redrawn alone, so a key row being typed in stays.
            void ShowSavedKeys()
            {
                if (savedKeys == null) return;
                savedKeys.Clear();
                foreach (var provider in AgentCatalog.Providers ?? new List<AgentProviderInfo>())
                {
                    var saved = ZettaKeychain.Has(provider.Key);
                    var chip = new Label(saved ? "✓ " + provider.Label : provider.Label).Classes("z-chip", saved ? "z-chip--success" : null);
                    chip.tooltip = saved ? $"Your {provider.Label} key is saved" : $"No {provider.Label} key saved";
                    savedKeys.Add(chip);
                }
            }

            Render();
            AgentCatalog.Changed += Render;
            ZettaKeychain.Changed += ShowSavedKeys;
            host.RegisterCallback<DetachFromPanelEvent>(_ =>
            {
                AgentCatalog.Changed -= Render;
                ZettaKeychain.Changed -= ShowSavedKeys;
            });
            _ = AgentCatalog.RefreshAsync();
            return card;
        }

        /// <summary>One of the two things the agent can run on, as a card to click — the chosen one is filled.</summary>
        static VisualElement AccountChoice(string account, string title, string blurb, bool chosen, System.Action<string> choose)
        {
            var choice = new VisualElement { name = "choice-" + account, focusable = true, tooltip = chosen ? "In use" : "Use this" };
            choice.AddToClassList("z-choice");
            choice.EnableInClassList("z-choice--chosen", chosen);
            var head = new VisualElement().Classes("z-row");
            var radio = new VisualElement().Classes("z-choice-radio");
            radio.Add(new VisualElement().Classes("z-choice-dot"));
            head.Add(radio);
            head.Add(new Label(title).Classes("z-choice-title"));
            choice.Add(head);
            choice.Add(new Label(blurb).Classes("z-hint", "z-choice-blurb"));
            choice.AddManipulator(new Clickable(() => choose(account)));
            choice.RegisterCallback<KeyDownEvent>(e =>
            {
                if (e.keyCode is not (KeyCode.Return or KeyCode.KeypadEnter or KeyCode.Space)) return;
                choose(account);
                e.StopPropagation();
            });
            return choice;
        }

        /// <summary>One provider: its key saved (✓, Check, Replace, Remove) or a field to paste one.</summary>
        static VisualElement KeyRow(AgentProviderInfo provider)
        {
            var row = new VisualElement().Classes("z-key-row");
            var status = new Label().Classes("z-hint");

            void Render(bool editing)
            {
                row.Clear();
                var saved = ZettaKeychain.Has(provider.Key);
                var title = new VisualElement().Classes("z-row");
                title.Add(new Label(provider.Label).Classes("z-label"));
                if (saved) title.Add(new Label("✓ Key saved").Classes("z-chip", "z-chip--success"));
                row.Add(title);

                if (saved && !editing)
                {
                    row.Add(ZettaUI.Row(
                        ZettaUI.Button("Check key", () => _ = Check(), "z-button--small"),
                        ZettaUI.Button("Replace", () => Render(true), "z-button--small"),
                        ZettaUI.Button("Remove", () =>
                        {
                            ZettaKeychain.Delete(provider.Key);
                            status.text = "";
                            Render(false);
                        }, "z-button--small", "z-button--danger")).Classes("z-row--wrap"));
                    row.Add(status);
                    return;
                }

                var field = ZettaUI.TextInput($"Paste your {provider.Label} API key", "", 512);
                field.isPasswordField = true;
                field.AddToClassList("z-grow");
                var line = new VisualElement().Classes("z-row");
                line.Add(field);
                line.Add(ZettaUI.Button("Save", () =>
                {
                    var error = ZettaKeychain.Set(provider.Key, field.value);
                    field.value = "";
                    if (error != null)
                    {
                        status.text = error;
                        status.EnableInClassList("z-error", true);
                        return;
                    }
                    Render(false);
                    _ = Check();
                }, "z-button--small", "z-button--signal"));
                if (saved) line.Add(ZettaUI.Button("Cancel", () => Render(false), "z-button--small"));
                row.Add(line);
                if (!string.IsNullOrEmpty(provider.KeyUrl))
                    row.Add(ZettaUI.Button($"Get a {provider.Label} key ↗", () => Application.OpenURL(provider.KeyUrl), "z-button--ghost", "z-button--small"));
                row.Add(status);
            }

            async System.Threading.Tasks.Task Check()
            {
                status.EnableInClassList("z-error", false);
                status.EnableInClassList("z-success", false);
                status.text = "Checking…";
                var key = ZettaKeychain.Get(provider.Key);
                try
                {
                    var answer = await ZettaHttp.PostAsync("/unityAgent/provider-key/check", new JObject { ["provider"] = provider.Key },
                        new Dictionary<string, string> { ["X-Zetta-Provider-Key"] = key ?? "" }, 30) as JObject;
                    var ok = answer?["ok"]?.Value<bool>() == true;
                    status.text = ok ? "The key works." : answer?["message"]?.ToString() ?? "The key didn't work.";
                    status.EnableInClassList(ok ? "z-success" : "z-error", true);
                }
                catch (ZettaApiException e)
                {
                    status.text = e.Message;
                    status.EnableInClassList("z-error", true);
                }
            }

            Render(false);
            return row;
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
