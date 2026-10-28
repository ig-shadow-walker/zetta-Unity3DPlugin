# Changelog

## [0.7.0] - 2026-10-29

### Added
- Agent: **undo what it changed.** When a message is done, a card under the reply lists every script and asset it
  created, edited, moved or deleted, the folders it made, and its changes to the scene — with **Keep** and **Undo**
  on each line, and **Keep all** / **Undo all** for the rest. Changes are kept unless you undo them, and a kept change
  can still be undone. Click a line to preview it: the file's changes line by line, a new file's contents, or the
  agent's steps in the scene. Files are copied just before the agent first changes them (with their .meta, so a restored asset
  keeps its references), into `Library/Zetta3D/AgentChanges`. Scene changes are only undone while they are the
  newest in Unity's undo history, so nothing you did afterwards is taken back with them.
- Pipelines: **multi-view.** A **Multi-view seed** starts a run from pictures of each side (front required; back,
  left and right as you have them), and **Multi-view - Generate 3D** and **Multi-view - Smart 3D** make the model
  from them (or from Draw views). Run on… takes a set of pictures per run. **Multi-view - AI texturing** textures a
  model from pictures of each side: wired into its Views input from a Multi-view seed or Draw views (the same
  pictures the model was made from), or given on each run. A pipeline saved with Draw views wired into Generate 3D
  opens with its multi-view step in place.
- Pipelines: connections can be selected and removed (✕ or Delete), and either end dragged to another socket to move
  it — or onto empty canvas to remove it. A connection that can't be made says why.

### Changed
- **One Zetta 3D window.** 3D Studio, My Generations, Pipelines and the Agent are tabs in its header instead of
  separate windows; each tab keeps its place while you switch. The Window › Zetta 3D items open it on that tab, and
  a layout saved with the old windows opens the new one in their place. While the agent works, dots beside the
  **Agent** tab show it from any other tab — amber and still when it is waiting for you.
- Pipelines: Generate 3D and Smart 3D take one picture; pictures of each side go to their Multi-view steps, and wiring
  them the wrong way names the right step.

### Removed
- The **Convert** tab in 3D Studio, and Send to › Convert in My Generations.

### Fixed
- Signing in updates every open Zetta tab straight away. A window whose tab was hidden behind another stopped
  listening for sign-in changes and showed "signed out" until it was reopened.

## [0.6.0] - 2026-10-27

### Added
- Agent: **run it on your own API key** — Gemini, OpenAI, DeepSeek or Claude. In Preferences › Zetta 3D, choose
  "My own API keys" and paste a key for any provider (Check key tells you it works). Keys are kept in your system's
  key store (macOS Keychain, Windows Credential Manager, or the Linux keyring), never in Unity's preferences. The
  chat box then shows a provider menu (Zetta Account, then only providers with a key), that provider's models and
  the thinking levels each model has. Your pick runs every message; Auto thinking still adapts. Usage is billed by
  the provider, not in Zetta credits; making 3D models and pictures still uses Zetta credits. Protocol v1.7.
- Agent: images and PDFs are blocked before sending when the chosen model can't read them, and Scene / Game are
  off for models that can't see pictures. Key problems (invalid key, no credit, model unavailable) show in the chat
  with **Open settings**.

### Fixed
- The Agent window asks the server for its model list again after you sign in, or when you come back to it, so
  the menus appear without reopening the window.

## [0.5.0] - 2026-10-27

### Added
- Agent: a **thinking level menu** beside the model menu, always shown, each with its own Auto — pick the model and
  let Auto choose the thinking, or the other way round. Protocol v1.6.
- Agent: the working indicator shows the model and thinking level each turn runs on (e.g. *Capable · Low thinking*),
  and the tooltip says whether Auto or you picked them. On Auto it can step up mid-task on long or failing jobs.

### Changed
- The thinking level defaults to Auto.
- The "⇧↵ new line" hint hides in narrow windows so the Send button stays on the same line as the menus.
- Requests tell the server which plugin and Unity version you use.

## [0.4.0] - 2026-10-25

### Added
- Agent: **asks you mid-task** (`ask_user`). When it needs something to carry on, it shows a card in the chat: a
  slot to drop a prefab or object into (or pick it, or use the selection; several at once where it asks for
  several), options to choose from with "Something else…" for your own words, or a text box. Press OK and it
  continues the same turn; Skip lets it go on with its best guess. Protocol v1.5.
- Agent: @-mentions (Scene or Project picker with search), drag-and-drop tagging from the Project window and Hierarchy;
  tags show as chips and are sent as addresses, not contents (the agent opens the file itself when it works on it),
  **Add to Zetta Agent** in the Project and Hierarchy right-click menus and **⇧⌘M / Ctrl+Shift+M** tag everything
  selected at once (opening the Agent window if needed),
  any-file attachments (pictures, PDFs, inlined text/code, other files copied into the project), Game view capture.
- Agent: History panel (search, grouped by day, delete), two-level Autonomy (Ask first / Full autonomy), live
  thinking indicator, Shift+Enter for new lines, redesigned message box.
- My Generations shows "In project" for imported models, with one Import button (Place in scene is a setting) and
  download progress.

### Changed
- "Studio" is now "3D Studio"; header links to 3D Studio, My Generations, Pipelines and Agent.
- Model downloads come in 8 MB ranges with retries; toolbars no longer squeeze on short windows; pill shapes fixed.

## [0.3.0] - 2026-10-19

### Added
- 3D Pipelines window: node-canvas editor (all Studio steps, Isolate parts branching, prices, problems, undo/redo,
  autosave), batch runs on up to 20 seeds (Manual/Auto), runs view with step results, isolate-parts review, retry,
  and import of finished models.
- Library: pipeline runs grouped by run with a "Pipeline" tag; Fork a generation back into Generate / Smart 3D.
- Agent: pipeline tools (build, edit, run, status, manage, import results) and `pipeline_run_finished` events.

### Changed
- Uploads go through the API's upload relay (large files in 8 MB pieces, automatic retries, clear errors) with progress.
- Polygon counts keep what you typed when switching triangles/quads, with an inline range message.

## [0.2.0] - 2026-10-14

### Added
- Smart UV tab.
- AI edit on input pictures; library bulk delete.
- Agent: `process_model` tool; Stop in every state (and Escape).

### Changed
- Texture fit defaults to "Fit texture to mesh"; Output defaults to Geometry on unpaid plans.
- Vendor names are removed from downloaded model files.
- Gaussian splats are not supported in Unity; splat rows in the library link to zetta3d.ai.
- Splat files and UV layout pictures are no longer mistaken for meshes.

## [0.1.0] - 2026-09-28

### Added
- Browser sign-in (plugin session over 127.0.0.1) with a first-run sign-in panel.
- Studio window: Generate 3D, Smart 3D, Retopology, Segment, Texture, Auto Rig, Convert, Image Studio, Library.
- Import of finished generations (GLB via glTFast, FBX) with optional placement in the open scene.
- Zetta Unity Agent: streaming chat, Editor tools (scene, components, scripts, prefabs, materials, capture),
  compile-and-fix loop across domain reloads, approvals, per-project memory and project brief, auto routing.
