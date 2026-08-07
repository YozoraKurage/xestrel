# xestrel

Per-avatar Material isolation for VRChat avatar projects. 日本語: [README.ja.md](README.ja.md)

When you keep multiple avatars in one Unity project, shared `Material` / `Texture` references mean a tweak made for avatar A silently shows up on avatar B. xestrel walks a selected avatar's `Renderer` hierarchy and:

1. **Materials**: replaces each referenced `Material` with a per-avatar copy under `Assets/Xestrel/<AvatarName>/Materials/` and rewires the `Renderer.sharedMaterials` arrays. This runs in bulk when you press **Isolate** (the button shows how many shared materials it would copy).
2. **Textures**: per texture property, on demand. The window shows each copy material's texture slots; press the per-row **Isolate** button to copy that single `Texture` into `Assets/Xestrel/<AvatarName>/Textures/` and point the property at the copy, or **Isolate All Textures** to do every slot of a material at once. Unisolated textures keep referring to the shared original; isolated slots get a per-row **Restore** button.
3. **Animators**: per `AnimatorController`, on demand. The window's *Animators* section lists every `VRCAvatarDescriptor` playable layer that still points at a shared controller with a one-click **Isolate** button (you can also drop any controller into the object field). The controller is copied to `Assets/Xestrel/<AvatarName>/Animators/`, every referenced `AnimationClip` (states, sub-state machines, blend trees) is copied to `Assets/Xestrel/<AvatarName>/Animations/`, the controller copy is rewired to use the clip copies, and if the original controller is referenced from the avatar's `VRCAvatarDescriptor` playable layers, those references are swapped to the copy.

The original assets are left untouched. A `XestrelMaterialIsolation` component is added to the avatar root to remember every `original → copy` mapping. It implements `VRC.SDKBase.IEditorOnly`, so it is stripped on VRChat upload.

## Status

Unity 2022.3 LTS, VRChat Avatars SDK 3.5+. No NDMF / Modular Avatar / Avatar Optimizer integration — xestrel only touches `Renderer.sharedMaterials`.

## Install

Copy or symlink this directory into your Unity project at `Packages/net.yozolab.xestrel/`. Open the Editor; the package compiles into `Xestrel.Runtime.dll` and `Xestrel.Editor.dll`.

## Use

1. Drop an avatar prefab into a scene.
2. Right-click the avatar root in the Hierarchy → **Xestrel → Isolate Materials**, or open **Window → Xestrel → Asset Isolation** and press **Isolate**. The window follows your Hierarchy selection onto avatars; use the lock toggle next to the Avatar field to pin it.
3. Material copies appear under `Assets/Xestrel/<AvatarName>/Materials/` and the avatar's renderers are rewired to them. The **Folder** button pings that folder in the Project window.
4. The window is tabbed: **Materials** (per-material bindings), **Textures** (a flat list of every texture the copy materials reference), **Animators**, **Isolated** (everything xestrel changed on this avatar in one list — materials / textures / animators / clips, plus *unused copies* left in the workspace folder that nothing references any more), **Additions** (what was added to the avatar relative to its base prefab: added prefab instances and scene objects with per-addition Isolate, added / removed components; degrades to listing child prefab instances when the avatar is unpacked), and **Not Isolated** (everything still shared, with the pending count in the tab label).
5. On the *Materials* tab, expand a binding. For any texture slot you want to make per-avatar, press the row's **Isolate** button — that texture is copied to `Assets/Xestrel/<AvatarName>/Textures/` and the property is repointed at the copy. **Isolate All Textures** does every slot at once. Isolated slots are marked with ● and can be reverted individually with **Restore**.
6. On the *Textures* tab, each texture shows its thumbnail, isolation status, and every material slot that uses it; **Isolate** / **Restore** there rewires all of those slots at once.
7. The *Not Isolated* tab lists shared materials (with renderer slot counts and a one-click per-material **Isolate**), shared textures, and shared descriptor playable layers.
8. Re-running Isolate is a no-op for materials; texture isolation never happens automatically.
9. Press **Restore** on the inspector or window (with confirmation) to revert texture properties to their originals (textures first) and then point every renderer back at the shared materials. A single material can be reverted with its **Restore Material** button; the copy asset always stays on disk.
10. If you delete copy assets from the Project window, the window/inspector show a **Prune** button that drops the now-dead bindings.

## Dependency browser

**Window → Xestrel → Dependencies** (or the **Deps** button in the isolation window) opens an indented dependency tree of the avatar. The first ring is discovered generically — every serialized object-reference of every component in the hierarchy — so Modular Avatar / VRCFury / audio / mesh / menu references all appear without per-type support. Each row's **▸ n** button expands that asset's own direct dependencies (via the import pipeline, so it is cheap); kind toggles (Mat / Tex / Mesh / Anim / Clip / Menu / Shader / Prefab / Other), a **No Pkg** toggle hiding everything outside `Assets/`, and a search highlight keep the list readable. Isolation state is a color dot per row: orange = still shared and isolatable, green = isolated copy, red = a copy that another workspace's manifest also tracks (the badge names the workspace; detected even in unloaded scenes), gray = kinds xestrel does not manage. Rows carry inline **Isolate** / **Restore** buttons acting on the primary avatar; click a name to ping it. **Add Selected** adds more avatars as roots — assets referenced by several of them get an `×n avatars` badge.

## Workspaces and renaming

- The folder name under `Assets/Xestrel/` is fixed the first time an avatar is isolated. Renaming the GameObject afterwards is safe: existing and new copies keep going to the original folder, and the status line tells you which one.
- If the folder name is already taken (a same-named avatar in another scene, or leftovers from a component you removed), a ` (n)` suffix is chosen — so placing the same prefab into several scenes, or twice into one scene, gives each instance its own independent workspace.
- **Deriving a variant that inherits your edits**: press the **Fork** button in the window — it duplicates the avatar in the scene (keeping the prefab connection) and immediately gives the duplicate its own independent copies. Duplicating by hand (Ctrl+D) works too: the duplicate initially shares the same copy assets, so the window shows a warning with a **Fork** button — press it on the duplicate. Fork re-copies every bound material / texture / animator / clip into a fresh workspace folder (all edits made so far carry over), rewires the duplicate to the forks, and keeps each binding pointing at the true shared original, so Restore still works per avatar. The source avatar is never touched. For a from-scratch variant, place a fresh prefab instance instead and isolate it.

## Recovery and safety

- Every workspace also stores a manifest asset, `Assets/Xestrel/<AvatarName>/XestrelWorkspace.asset`, mirroring the component's bindings after every change. The component on the avatar stays authoritative; the manifest exists so the original → copy mapping survives losing the component.
- If the component is lost (a prefab **Revert All**, a scene mishap) while the renderers still point at copies, the window detects it and shows a **Recover** button that rebuilds the component from the manifest.
- The manifest additionally keeps a permanent GUID history of every copy → original pair ever recorded, so even bindings pruned while an asset was missing stay reconstructible.
- Renaming a workspace folder under `Assets/Xestrel/` is followed automatically: the workspace adopts the folder's new name and new copies keep landing next to the old ones.
- The window warns when the avatar's prefab *asset* itself references Xestrel copies (renderer / descriptor overrides were applied to the prefab) — that makes every instance of the prefab, in every scene, use those copies.

## Copies

- Material copies are plain `.mat` assets created via `AssetDatabase.CopyAsset` (not Material Variants). They are fully independent of the source.
- Texture copies use `AssetDatabase.CopyAsset` so importer settings (compression, sRGB, mipmaps, etc.) carry over. Textures that are sub-assets of a model (e.g. embedded inside an FBX) or that have no asset on disk (e.g. `RenderTexture`) are skipped with a warning — their references on the copy material are left pointing at the original.
- Animator copies use `AssetDatabase.CopyAsset` so all sub-assets (states, blend trees, transitions) come along; xestrel then walks the copy and rewrites every `Motion` reference to a clip copy. Embedded clips inside FBX / model assets and `AnimatorOverrideController` inputs are skipped.

## Layout

- `Runtime/` — `XestrelMaterialIsolation` MonoBehavior (IEditorOnly) with material / texture / animator / clip bindings
- `Editor/Core/` — Logging, path helpers
- `Editor/Detection/` — Avatar root resolution (VRCAvatarDescriptor)
- `Editor/Isolation/` — Material / Texture / Animator copy factories and isolator services
- `Editor/UI/` — EditorWindow + Hierarchy context menu
- `Editor/Inspector/` — Custom inspector for the MonoBehavior
- `Tests/` — Editor Test Runner suite
