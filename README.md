<p align="center">
  <picture>
    <source media="(prefers-color-scheme: dark)" srcset="Documentation~/images/banner-dark.svg">
    <img alt="Zetta 3D for Unity — 3D assets and an AI agent, inside the Unity Editor. Generate, refine and import 3D models without leaving Unity, and build your game with an agent that sees your scene." src="Documentation~/images/banner-light.svg" width="100%">
  </picture>
</p>

<p align="center">
  <a href="#install"><img alt="Unity 2022.3 LTS and Unity 6" src="https://img.shields.io/badge/Unity-2022.3%20LTS%20%C2%B7%20Unity%206-15130F?style=flat-square&logo=unity&logoColor=FAF8F4"></a>
  <a href="#install"><img alt="Install from a Git URL" src="https://img.shields.io/badge/install-Package%20Manager%20%C2%B7%20Git%20URL-15130F?style=flat-square"></a>
  <a href="#the-zetta-agent"><img alt="AI agent built in" src="https://img.shields.io/badge/AI%20agent-built%20in-1677FF?style=flat-square"></a>
  <a href="#license"><img alt="Source-available" src="https://img.shields.io/badge/license-source--available-55514A?style=flat-square"></a>
</p>

<p align="center">
  <a href="#install"><b>Install</b></a> ·
  <a href="#sign-in"><b>Sign in</b></a> ·
  <a href="#3d-studio"><b>3D Studio</b></a> ·
  <a href="#3d-pipelines"><b>Pipelines</b></a> ·
  <a href="#the-zetta-agent"><b>Agent</b></a> ·
  <a href="#settings"><b>Settings</b></a> ·
  <a href="https://zetta3d.ai"><b>zetta3d.ai</b></a>
</p>

<br>

**Zetta 3D for Unity** puts your [Zetta 3D](https://zetta3d.ai) account inside the Unity Editor. Use it to:

- **make 3D models** from text, a picture or several views, and finish them: retopology, segmentation, UVs,
  texturing, rigging and format conversion;
- **bring them into your project** in one click — imported, set up and, if you like, placed in the open scene;
- **chain the tools into pipelines** that run over many pictures or models at once;
- **build your game with the Zetta Agent**, an assistant that reads your scene and scripts, writes C#, builds
  prefabs and materials, looks at the Scene view to check its work, and makes the art it needs.

Everything you make is also in your library on zetta3d.ai, and everything you made there shows up in Unity.

<!-- Screenshot: the whole Editor with the Agent docked and a generated model in the scene.
<p align="center">
  <img alt="The Unity Editor with the Zetta Agent docked beside the Scene view" src="Documentation~/images/editor.png" width="100%">
</p>
-->

## Install

**You need:** Unity **2022.3 LTS** or newer (Unity 6 recommended), [Git](https://git-scm.com/downloads)
installed on your computer, and a [Zetta 3D](https://zetta3d.ai) account.

1. In Unity, open **Window › Package Manager**.
2. Click **+** (top left) › **Add package from git URL…**
3. Paste the URL and click **Add**:

   ```
   https://github.com/ig-shadow-walker/zetta-Unity3DPlugin.git#v0.4.0
   ```

Unity downloads the package with everything it needs: glTFast (GLB import), the meshopt decoder (compressed
GLBs) and Newtonsoft JSON. When it finishes, the **3D Studio** window opens on its own.

<details>
<summary><b>Other ways to install, update and remove</b></summary>

<br>

**Edit the manifest instead.** Add this line to `dependencies` in your project's `Packages/manifest.json`:

```json
"com.zetta3d.unity": "https://github.com/ig-shadow-walker/zetta-Unity3DPlugin.git#v0.4.0"
```

**Latest version, not a pinned one.** Leave out `#v0.4.0` to follow the newest code. Pinning a version tag is
better for a team, so everyone gets the same build.

**Update.** Change the tag (for example `#v0.4.0` → a newer release) in `Packages/manifest.json`, or add the
package again from its Git URL with the new tag. The [changelog](CHANGELOG.md) lists what each version adds.

**Remove.** Package Manager › **Zetta 3D** › **Remove**. Models you imported stay in your project.

</details>

<details>
<summary><b>Install problems</b></summary>

<br>

- **"No 'git' executable was found"** — Git isn't installed or Unity can't see it. Install Git, then restart
  Unity *and* Unity Hub.
- **A warning that a package is experimental** — the meshopt decoder is marked experimental by Unity. It's
  expected, and safe to keep.
- **The 3D Studio window didn't open** — open it from **Window › Zetta 3D › 3D Studio**.

</details>

## Sign in

In the **3D Studio** window, click **Sign in with your browser**, sign in on zetta3d.ai and click **Connect
Unity**. That's it — the Editor receives its own session and never sees your password. The session is kept on
your computer, not in the project, so it never ends up in version control.

Every window lives under **Window › Zetta 3D**, and the header of each one links to the others:
**3D Studio · My Generations · Pipelines · Agent**.

## 3D Studio

**Window › Zetta 3D › 3D Studio** — every tool from the web Studio, in a docked window.

<!-- Screenshot: 3D Studio on the Generate 3D tab, with a prompt or a picture filled in.
<p align="center">
  <img alt="3D Studio: generating a 3D model from a picture" src="Documentation~/images/studio.png" width="860">
</p>
-->

| Tool | What it does |
|---|---|
| **Generate 3D** | Text, a picture or several views → a 3D model, on Zetta N1 or N2.5, with every option the web Studio has |
| **Smart 3D** | Clean, game-ready topology from the start, on Zetta Np1 or Np1.5 |
| **Segment** | Split a model into named parts (or let Smart Segment decide them) |
| **Retopology** | Rebuild a mesh at a polygon budget, as triangles or quads |
| **Smart UV** | Lay a model's UVs out again as a clean layout, keeping its textures, rig and animation |
| **Texture** | Re-texture a model from a description or a reference picture, up to 8K |
| **Auto Rig** | Rig humanoids, animals and other creatures — Standard, Mixamo, Unity Humanoid, UE5, VRM or ActorCore bone names; GLB or FBX |
| **Convert** | Convert to FBX, glTF, OBJ, USDZ, STL or 3MF, with pivot, scale, axis and texture options |
| **Image Studio** | Generate, edit and compose images, and save them as textures or sprites |

Finished models import into `Assets/Zetta3D/Models/<id>-<name>/` (you can change the folder) and, with
**Place in scene** on, land in the open scene resting on the ground.

### My Generations

**Window › Zetta 3D › My Generations** — everything you've made on Zetta 3D, on the web or in Unity.

<!-- Screenshot: My Generations with a grid of models, one of them marked "In project".
<p align="center">
  <img alt="My Generations: a grid of models, with the ones already in the project marked" src="Documentation~/images/library.png" width="860">
</p>
-->

- **Import** a model with a progress bar, or **drag it** straight into the Scene view or the Hierarchy.
- Models already in your project are marked **In project**, with **Show** and **Place** buttons.
- **Send to a tool** (texture it, rig it…), or **Fork** a generation's settings back into Generate.
- Search, filter, bulk delete, and see pipeline runs grouped by run.

Gaussian splats and worlds made on zetta3d.ai are listed too, but Unity can't use them; they open on the web.

## 3D Pipelines

**Window › Zetta 3D › Pipelines** — chain the Studio's tools into a reusable recipe on a node canvas: the same
pipelines as on [zetta3d.ai/pipelines](https://zetta3d.ai/pipelines).

<!-- Screenshot: the Pipelines canvas with a recipe of five or six steps.
<p align="center">
  <img alt="The Pipelines node canvas: a picture seed, generate, retopology, texture and rig steps" src="Documentation~/images/pipelines.png" width="860">
</p>
-->

- Start from a **picture** or a **model**, then add steps: Remove background, AI edit, Draw views, Isolate parts
  (one branch per part), Generate 3D or Smart 3D, Retopology, Segmentation, AI texturing, Smart UV and Auto rig.
- The canvas checks every step as you build, saves on its own, and has undo and redo.
- **Run** a pipeline on up to 20 pictures or models at once — **Manual** (it waits for you before each step) or
  **Auto**.
- Runs keep going on the server even if Unity is closed. The **Runs** view shows every step's result, lets you
  review isolated parts and retry a failed step, and imports the finished models into your project.

## The Zetta Agent

**Window › Zetta 3D › Agent** — a game-building assistant that works in your open project. Tell it what you want
in plain words: *"add a third-person controller with a follow camera"*, *"make the coins spin and add a score
UI"*, *"generate a low-poly treasure chest and put it next to the player"*.

<!-- Screenshot: the Agent in the middle of a task — a few steps done, and a card asking you to pick a prefab.
<p align="center">
  <img alt="The Zetta Agent working on a request and asking the user to pick a prefab" src="Documentation~/images/agent.png" width="560">
</p>
-->

### What it can do

- **See your scene.** It reads the hierarchy, components and scripts, and looks at the Scene or Game view — from
  around an object or from above — to check its own work.
- **Work in the Editor.** It writes and edits C#, waits for Unity to compile and fixes any errors, creates
  GameObjects, components, prefabs, materials and scenes, sets properties, runs menu items and enters Play Mode.
  **Every change goes on the Undo stack.**
- **Make the art.** It draws images, generates 3D models, runs the finishing tools (retopology, Smart UV,
  texturing, rigging, segmentation), then imports and places the results.
- **Build pipelines.** Describe a recipe — *"generate, retopologize, texture and rig a set of props"* — and it
  builds the pipeline, runs it on your pictures or models, follows the runs and imports what they make.
- **Remember.** It keeps notes and a project brief per project, plus your preferences across projects. Open
  **Memory** to see, edit or delete any of it. Your conversations are in **History**.

### Modes

Choose how the agent works in the **⋯** menu at the top of its window.

| Mode | What it does |
|---|---|
| **Agent** *(default)* | Works in the Editor: reads, then writes scripts, builds objects and makes art. |
| **Ask** | Reads your project and answers questions — explains code, finds things, suggests fixes — and **changes nothing**. |

### Autonomy

Choose how much it checks with you, under the message box.

| Autonomy | What it does |
|---|---|
| **Ask first** *(default)* | Shows a card and waits for your OK before it deletes anything, generates or processes a 3D model, or runs a pipeline. |
| **Full autonomy** | Never stops to ask. It carries the whole request through to the end on its own, and notes every step it approved for itself in the chat. |

### Model and thinking

Also under the message box. On **Auto**, every message goes to the right model with the right amount of
thinking — quick answers stay quick, and harder work gets more care. You can also pick **Fast**, **Capable** or
**Intelligent** yourself, and then a thinking level from **No thinking** to **Max**.

### Point it at things

<!-- Screenshot: the message box with the @ picker open on Project, and a couple of tagged chips.
<p align="center">
  <img alt="Tagging a script and a scene object for the agent with @" src="Documentation~/images/agent-mention.png" width="560">
</p>
-->

- **Type `@`** to tag a scene object or a project asset: pick **Scene** or **Project**, then search.
- **Drag** assets from the Project window, or objects from the Hierarchy, onto the chat.
- **Right-click › Add to Zetta Agent** in the Project window or the Hierarchy, or select things and press
  **⇧⌘M** (**Ctrl+Shift+M** on Windows). Several selected items are all tagged at once.

A tag shows as a chip and is sent as the item's address — the agent opens the file or object itself and works on
it. Nothing in your project is uploaded.

**Attach files** with 📎 or by dropping them in: pictures and PDFs go to the agent, code and text files are read
in, and anything else (models, audio, fonts…) is copied into `Assets/Zetta3D/Attachments/` and tagged.
**📷 Scene** and **🎮 Game** attach a picture of either view.

### It asks when it needs you

When the agent needs something from you to carry on, it shows a card in the chat and waits:

- **a slot** to drop a prefab, asset or scene object into — or pick it, or click **Use selection**;
- **options** to choose from, with **Something else…** for your own words;
- **a text box** to describe it yourself.

Click **OK** and it continues where it left off. **Skip** lets it go on with its best guess.

### Stop

**Stop** (or **Esc**) ends whatever it's doing, straight away. Nothing starts again until you send a new message.

## Settings

**Unity › Settings › Zetta 3D** (Windows: **Edit › Preferences › Zetta 3D**): your account, the import folders
and the preferred mesh format (GLB or FBX).

Project settings live in `ProjectSettings/Zetta3DSettings.json`, including the project id the agent's memory is
tied to. **Commit that file**, so everyone on your team shares one project memory.

## License

Source-available, not open source — © 2026 Zetta 3D. The source is published so you can install and audit it; it
may not be copied, modified or redistributed. See [LICENSE.md](LICENSE.md).
