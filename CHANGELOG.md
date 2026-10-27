# Changelog

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
