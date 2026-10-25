# Zetta 3D for Unity

Zetta 3D inside the Unity Editor. Generate 3D models and images, retopologize, texture, rig and
convert them, import them straight into your project — and build your game with the **Zetta Unity
Agent**, an assistant that sees your scene and works in the Editor.

Requires Unity **2022.3 LTS** or newer (tested on Unity 6).

## Install

Package Manager → **+** → **Add package from git URL…**

```
https://github.com/ig-shadow-walker/zetta-Unity3DPlugin.git
```

The package pulls in glTFast (GLB import), the meshopt decoder (compressed GLBs) and Newtonsoft JSON.

## Sign in

After install the **3D Studio** window opens on its own (or **Window › Zetta 3D › 3D Studio**). Press
**Sign in with your browser**, sign in on zetta3d.ai and press **Connect Unity**. That's it — the
Editor receives a plugin session over `127.0.0.1` and never sees a password. The session is stored
per machine (EditorPrefs), never in the project.

## 3D Studio — Window › Zetta 3D › 3D Studio

| Tab | What it does |
|---|---|
| Generate 3D | Text, image or multi-view → 3D on Zetta N1 / N2.5, with every option the web Studio has |
| Smart 3D | Clean-topology generation on Zetta Np1 / Np1.5 |
| Segment | Split a model into named parts (or Smart Segment) |
| Retopology | Rebuild a mesh at a polygon budget (Zetta R1 or N1), triangles or quads |
| Smart UV | Lay a model's UVs out again as a clean layout, carrying textures, rig and animation across (up to 80,000 triangles) |
| Texture | Re-texture from a description or a reference picture, up to 8K |
| Auto Rig | Rig humanoids, animals and more; Standard or Mixamo bone names; GLB or FBX |
| Convert | Server-side conversion to FBX, GLTF, OBJ, USDZ, STL or 3MF, with pivot, scale, axis and texture options (Unity-only; the web Studio now exports locally) |
| Image Studio | Generate, edit and compose images; save them as textures or sprites |
| My Generations | Everything you've made on Zetta 3D (also **Window › Zetta 3D › My Generations**) — import, place in scene, send to a tool, **Fork** a generation's settings back into Generate, bulk delete; pipeline runs grouped by run |

Finished models import to `Assets/Zetta3D/Models/<id>-<name>/` (configurable) and can be placed in
the open scene automatically, resting on the ground. Every job shows its credit price before you
press the button.

## 3D Pipelines — Window › Zetta 3D › Pipelines

Chain the Studio's tools into a reusable recipe on a node canvas — the same pipelines as zetta3d.ai/pipelines.
Start from a **picture seed** or a **model seed**, then add steps: Remove background, AI edit, Draw views,
Isolate parts (one branch per part), Generate 3D / Smart 3D, Retopology, Segmentation, AI texturing, Smart UV and
Auto rig. The canvas shows each step's price and problems, autosaves, and supports undo/redo.

Run a pipeline on up to 20 pictures or models at once, in **Manual** (waits for you before each step) or **Auto**
mode. Runs continue on the server even if Unity is closed; the Runs view shows every step's result, lets you
review isolated parts, retry failed steps, and import finished models straight into the project.

Gaussian splats and worlds made on zetta3d.ai show in My Generations, but they are not supported in
Unity: those rows only open on zetta3d.ai (or can be deleted).

## The Zetta Unity Agent — Window › Zetta 3D › Agent

A game-building assistant that works in your open project:

- **Sees the scene** — reads the hierarchy, components and scripts, and looks at the Scene or Game
  view (and around objects) to check its own work.
- **Acts in the Editor** — writes and edits C#, waits for compilation and fixes errors, creates
  GameObjects, components, prefabs, materials and scenes, sets serialized properties, runs menu items
  and Play Mode. Every change is on the Undo stack.
- **Makes art** — draws images, generates 3D models, and runs the finishing tools (retopology,
  Smart UV, texturing, rigging, segmentation) with Zetta — you approve every credit spend — then
  imports and places the results. Splats made on the web are listed but can't be used in Unity.
- **Builds pipelines** — drafts a pipeline from a description ("generate, retopologize, texture and rig a set of
  props"), edits its steps, runs it on your pictures or models after you approve the estimate, follows the runs and
  imports the results.
- **Asks when it needs you** — mid-task, the agent can show a card in the chat: drop in the prefab or object it
  should use (or pick it, or use the selection), choose from options or write your own answer, then press **OK**
  and it carries on. **Skip** lets it go on with its best guess.
- **Knows what you point at** — type **@** to tag a scene object or a project asset (pick Scene or Project,
  then search), or drag assets from the Project window / objects from the Hierarchy onto the chat. A tag is a
  chip, sent as the file's address: the agent opens and edits that file itself, nothing is uploaded. Or select
  one or more items and right-click ▸ **Add to Zetta Agent**, or press **⇧⌘M** (Ctrl+Shift+M on Windows; rebind
  it in Edit ▸ Shortcuts under Main Menu ▸ Assets). Attach any
  file with 📎 or by dropping it in: pictures and PDFs go to the agent, code and text files are inlined, and
  anything else (models, audio, fonts…) is copied into `Assets/Zetta3D/Attachments/` and tagged. 📷 Scene and
  🎮 Game attach a picture of either view.
- **Remembers** — per-project memory and a project brief it keeps up to date, plus preferences that
  follow you across projects. Open **Memory** to see, edit or delete any of it.
- **Routes itself** — on **Auto** each message goes to the right model and thinking depth; or pick
  Fast / Capable / Intelligent and a thinking level yourself.
- **Autonomy, your call** — **Ask first** (default) asks before deleting anything or spending credits;
  **Full autonomy** never asks and works through the whole request on its own (every automatic approval,
  and its credits, is noted in the chat).
- **Stops when you say** — Stop (or Escape) ends whatever it is doing, and nothing resumes until you
  type again.

## Settings

**Preferences › Zetta 3D**: account, import folders, preferred mesh format (GLB or FBX), and — for
Zetta developers — the API and site base URLs (defaults: `https://api.zetta3d.ai/api`,
`https://zetta3d.ai`). Project settings live in `ProjectSettings/Zetta3DSettings.json`, including the
project id the agent's memory is keyed on — commit it so your team shares one project memory.

## License

Source-available, not open source — © 2026 Zetta 3D. The source is published so you can install and audit
it; it may not be copied, modified or redistributed. See [LICENSE.md](LICENSE.md).
