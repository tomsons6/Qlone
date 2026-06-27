# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## Project

Qlone is an early-stage first-person puzzle game built in **Unity 6 (6000.0.72f1)** with the **Universal Render Pipeline (URP)**. The central mechanic: the player spawns a stationary *clone* of themselves and switches control back and forth between the two bodies (split-screen), using both to solve environment puzzles (doors, keycards, pickable objects). When a clone is dismissed it collapses into a physics **ragdoll** corpse that stays in the world and can be picked up.

All first-party code lives in `Assets/_App/`. Everything under `Library/`, `Packages/`, and the various `com.unity.*` caches is Unity/third-party and should not be edited. The C# is nine files in `Assets/_App/Scripts/`, compiled into the default `Assembly-CSharp` (no asmdefs).

## Working in this repo

This is an **Editor-driven project** — there is no CLI build/test pipeline and no scenes are registered in `EditorBuildSettings` (build via Unity's *Build Profiles* window). Develop and run by opening the project in Unity 6000.0.72f1 and entering Play mode in `Assets/_App/Scenes/DemoLevel.unity` (the main scene; `TestScene.unity` is a scratch scene).

The **Unity MCP server** (`com.coplaydev.unity-mcp`) is installed and connected, so you can drive the live Editor directly instead of asking the user to. Practical loop:
- After editing scripts, call `refresh_unity` (with `wait_for_ready`), then `read_console` (filter to `error`) to confirm a clean compile **before** assuming a type/component is usable. The readiness/compile state lives in the `editor_state` resource (`mcpforunity://editor/state`) under `compilation.is_compiling` and `advice.ready_for_tools`.
- **Gotcha:** creating a *brand-new* `.cs` file needs a full asset refresh (`refresh_unity` with `scope: all`) so Unity imports it — `scope: scripts` recompiles existing files but won't pick up the new asset, and references to the new type will fail to compile.
- Input simulation isn't available over MCP — you can enter Play mode and read the console, but you can't press keys or screenshot the Game view. Treat gameplay changes as *verified-compiles*, not *verified-in-play*; ask the user to confirm feel/visuals.
- `manage_editor` controls Play mode; `manage_prefabs` edits prefab assets headlessly (`modify_contents` with `target` = the GameObject the change/child applies to). Paths passed to MCP tools are relative to `Assets/` with forward slashes.

There are **no unit tests** in the project, though the Unity Test Framework package is present. If you add tests, create an asmdef + test assembly and run them via `run_tests` (MCP) or the Editor's Test Runner.

## Architecture

The scripts are small but coupled through Unity Editor wiring, prefab inheritance, and runtime tag/layer lookups rather than through code references. The following conventions are **load-bearing** and not visible from reading any single `.cs` file.

### Character prefabs: one base, one variant
- `Prefabs/Character/MainCharecter.prefab` is the player (root tagged `Main`). It carries `FPS_Controller`, `Cloning`, `GrabScript`, `InputManager`, `CharacterAnimator`, a `CharacterController`, a kinematic `Rigidbody`, and a child `Camera`.
- The runtime clone spawns from **`CloneNested.prefab`, a prefab *variant* of MainCharecter** (referenced by `Cloning.CloneGO`). The variant renames the root, tags it `Clone`, puts the model on the `CloneBody` layer, disables `FPS_Controller`, and removes `Cloning`. **`Clone.prefab` is an older, unused version — editing it has no runtime effect.**
- **Consequence:** components/objects shared by both bodies should be added to **MainCharecter** (the base) — they propagate to the player, the scene instance, *and* the clone variant. This is how `CharacterAnimator` and the `Camera/HoldPoint` anchor reach both bodies from a single edit.
- The visible body is the **`FirstCharacter`** humanoid model (a nested FBX with an `Animator`, controller `MainCharacterController` assigned, root motion off). The `Camera` also has a static `CameraViewArms` first-person arm mesh and a `HoldPoint` empty (the grab anchor).

### Input is wired in the scene, not in code
`Character/InputManager.cs` is the only input entry point: it exposes `KeyCode` fields plus `UnityEvent`s (`OnQDown/Up/Hold`, same for `F`, `E`, `R`) and invokes them each frame. **Which method a key calls is configured in the Inspector**, and every binding targets the **player's** component instances (so they run even while the clone is in control, acting on the active body via `Camera.main`). Current wiring on the MainCharecter `InputManager`:
- **Q** → `Cloning.Clone`
- **F** → `Cloning.SwitchClone`
- **E** → `GrabScript.ReleaseObject` then `GrabScript.PickUpObject` (drop + grab/swap)
- **R** → `GrabScript.ReleaseObject` then `Cloning.DestroyClone` (drop + ragdoll the clone)

To change a binding, edit the event wiring in the prefab/scene — grepping code won't reveal it.

### Cloning and control-switching (`Character/Cloning.cs`)
Lives on the player root. Key behaviours:
- `Clone()` raycasts around the player for a clear spot, instantiates the clone, and **animates the split-screen open** (coroutine `LerpRect` eases each `Camera.rect`; the player view shrinks left while the clone view slides in from the right). Split-screen uses oversized viewport rects (`width/height = 5`); the rest/target rects are serialized fields.
- `SwitchClone()` hands control over by **retagging the active camera as `MainCamera`** and toggling `FPS_Controller` + `CharacterController` between the bodies. It deliberately **does not** toggle `GrabScript` — that stays enabled on the player so a held object keeps being dragged across a switch (it follows `Camera.main`). Anything reading `Camera.main` follows the active body automatically.
- `DestroyClone()` hands control back, animates the split closed, then calls `ReleaseAsRagdoll()` — it does **not** destroy the clone.
- `CloneActive` (public bool) tracks which body has control.

### Locomotion animation (`Character/CharacterAnimator.cs`)
On each body root; finds the child `Animator`. Drives a single float **`Speed`** parameter from the body's *actual* horizontal movement (position delta), not from input. Because it measures real movement, the controlled body plays Walking while the idle body stays in Idle with no coupling to `Cloning`. The controller (`Models/AnimationsAndControllers/MainCharacterController.controller`) is just Idle ⇄ Walking with `Speed` threshold transitions.

### Ragdoll on clone dismissal (`Character/HumanoidRagdoll.cs`, `Character/Ragdoll.cs`)
`HumanoidRagdoll.Create(animator, velocity)` builds a ragdoll **at runtime** from the Humanoid avatar: it looks up the 11 standard bones via `Animator.GetBoneTransform`, adds Rigidbodies + Colliders + CharacterJoints (collider sizes computed in bone-local space so they're scale-correct), disables the Animator, and goes dynamic. `Cloning.ReleaseAsRagdoll()` calls it, retags the corpse off `Clone` (so a new clone can spawn), disables its controller/grab/camera, and adds a **`Ragdoll`** marker component. The marker is how `GrabScript` recognises a corpse's body parts as grabbable. Ragdoll tuning (masses, joint limits, limb thickness) is constants in `HumanoidRagdoll.cs`.

### Object grabbing (`Character/GrabScript.cs`)
**Physics-based** (the old `ParentConstraint` approach is gone; constraints may still sit unused on pickup objects). Raycasts from `Camera.main`; a hit is grabbable if its collider is on the `Pickable` layer **or** under a `Ragdoll` marker, and has a non-kinematic Rigidbody. While held, each `FixedUpdate` drags the body toward the `HoldPoint` child of the active camera:
- **Rigid pickups** are pulled by velocity and rotated to the anchor (so they sit gripped, not tumbling).
- **The ragdoll** bone you aimed at is made **kinematic** and `MovePosition`-ed to the hand, so it pins firmly (no spin) while the rest of the body dangles from it through the joints.

`Physics.IgnoreLayerCollision(heldLayer, Player)` stops a held object from shoving the carrier while held; it's restored on release. Move/rotate the `HoldPoint` anchor (or later parent it to a hand bone) to tune where/how things are held.

### Tags and layers are a hard contract
Code hardcodes layer **numbers** and looks objects up by **tag string** at runtime. Reordering `ProjectSettings/TagManager.asset` will silently break things.
- Layers (by index): `9 Pickable`, `10 Player`, `11 PlayerArms`, `12 CloneArms`, `13 CloneBody`, `8 PostProccesing`. `GrabScript` treats `Pickable` as grabbable and ignores collisions between the held object's layer and `Player`. The clone's body/ragdoll parts are on `CloneBody`.
- Tags used in code: `Main` (player root), `Clone` (live clone, found via `FindGameObjectWithTag`; a corpse is retagged `Untagged` so it's no longer found), plus the built-in `MainCamera`/`Untagged` swapped during control switching. Other defined tags: `KeyCard`, `PickUp`, `CloneArms`.

### Narrative text (`TextController.cs` + `Editor/TextControllerEditor.cs`)
A coroutine typewriter effect that prints `m_Text` one character at a time into a uGUI `Text`. Control characters: `/` clears the textbox; a `/` *following* a character inserts a pause (`TextPause`). The custom inspector adds a **"Get Text File"** button that loads a `.txt` (e.g. `Assets/_App/NarrativeText/NarrativeText.txt`) into `m_Text` via a file dialog.
