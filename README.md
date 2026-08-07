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
4. The window is tabbed: **Materials** (per-material bindings), **Textures** (a flat list of every texture the copy materials reference), **Animators**, and **Not Isolated** (everything still shared, with the pending count in the tab label).
5. On the *Materials* tab, expand a binding. For any texture slot you want to make per-avatar, press the row's **Isolate** button — that texture is copied to `Assets/Xestrel/<AvatarName>/Textures/` and the property is repointed at the copy. **Isolate All Textures** does every slot at once. Isolated slots are marked with ● and can be reverted individually with **Restore**.
6. On the *Textures* tab, each texture shows its thumbnail, isolation status, and every material slot that uses it; **Isolate** / **Restore** there rewires all of those slots at once.
7. The *Not Isolated* tab lists shared materials (with renderer slot counts and a one-click per-material **Isolate**), shared textures, and shared descriptor playable layers.
8. Re-running Isolate is a no-op for materials; texture isolation never happens automatically.
9. Press **Restore** on the inspector or window (with confirmation) to revert texture properties to their originals (textures first) and then point every renderer back at the shared materials. A single material can be reverted with its **Restore Material** button; the copy asset always stays on disk.
10. If you delete copy assets from the Project window, the window/inspector show a **Prune** button that drops the now-dead bindings.

## Workspaces and renaming

- The folder name under `Assets/Xestrel/` is fixed the first time an avatar is isolated. Renaming the GameObject afterwards is safe: existing and new copies keep going to the original folder, and the status line tells you which one.
- If the folder name is already taken (a same-named avatar in another scene, or leftovers from a component you removed), a ` (n)` suffix is chosen — so placing the same prefab into several scenes, or twice into one scene, gives each instance its own independent workspace.
- **Deriving a variant that inherits your edits**: duplicate the isolated avatar in the Hierarchy (Ctrl+D). The duplicate initially shares the same copy assets, so the window shows a warning with a **Fork** button — press it on the duplicate. Fork re-copies every bound material / texture / animator / clip into a fresh workspace folder (all edits made so far carry over), rewires the duplicate to the forks, and keeps each binding pointing at the true shared original, so Restore still works per avatar. The source avatar is never touched. For a from-scratch variant, place a fresh prefab instance instead and isolate it.

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
- `Editor.Tests/` — Editor Test Runner suite
