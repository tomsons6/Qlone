# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## Project

Qlone is an early-stage first-person puzzle game built in **Unity 6 (6000.0.72f1)** with the **Universal Render Pipeline (URP)**. The central mechanic: the player spawns a stationary *clone* of themselves and switches control back and forth between the two bodies (split-screen), using both to solve environment puzzles (doors, keycards, pickable objects). When a clone is dismissed it collapses into a physics **ragdoll** corpse that stays in the world and can be picked up.

All first-party code lives in `Assets/_App/`. Everything under `Library/`, `Packages/`, and the various `com.unity.*` caches is Unity/third-party and should not be edited. The C# is eleven small files in `Assets/_App/Scripts/`, compiled into the default `Assembly-CSharp` (no asmdefs).

## Working in this repo

This is an **Editor-driven project** — there is no CLI build/test pipeline and no scenes are registered in `EditorBuildSettings` (build via Unity's *Build Profiles* window). Develop and run by opening the project in Unity 6000.0.72f1 and entering Play mode in `Assets/_App/Scenes/DemoLevel.unity` (the main scene; `TestScene.unity` is a scratch scene).

The **Unity MCP server** (`com.coplaydev.unity-mcp`) is installed and connected, so you can drive the live Editor directly instead of asking the user to. Practical loop:
- After editing scripts, call `refresh_unity` (with `wait_for_ready`), then `read_console` (filter to `error`) to confirm a clean compile **before** assuming a type/component is usable. The readiness/compile state lives in the `editor_state` resource (`mcpforunity://editor/state`) under `compilation.is_compiling` and `advice.ready_for_tools`.
- **Gotcha:** creating a *brand-new* `.cs` file needs a full asset refresh (`refresh_unity` with `scope: all`) so Unity imports it — `scope: scripts` recompiles existing files but won't pick up the new asset, and references to the new type will fail to compile.
- Input simulation isn't available over MCP — you can enter Play mode and read the console, but you can't press keys or screenshot the Game view. Treat gameplay changes as *verified-compiles*, not *verified-in-play*; ask the user to confirm feel/visuals.
- `manage_editor` controls Play mode; `manage_prefabs` edits prefab assets headlessly (`modify_contents` with `target` = the GameObject the change/child applies to). Paths passed to MCP tools are relative to `Assets/` with forward slashes.
- Prefer reading live state over the on-disk YAML: the `mcpforunity://project/layers` resource, camera culling masks, etc. can be ahead of `ProjectSettings/*.asset` until the user does *File → Save Project*. `manage_prefabs modify_contents` sometimes reports "no changes" on private serialized fields — verify with a read, and fall back to editing the prefab YAML directly for a stubborn value.

There are **no unit tests** in the project, though the Unity Test Framework package is present. If you add tests, create an asmdef + test assembly and run them via `run_tests` (MCP) or the Editor's Test Runner.

## Architecture

The scripts are small but coupled through Unity Editor wiring, prefab inheritance, and runtime tag/layer lookups rather than through code references. The following conventions are **load-bearing** and not visible from reading any single `.cs` file.

### Character prefabs: one base, one variant
- `Prefabs/Character/MainCharecter.prefab` is the player (root tagged `Main`, layer `Player`). It carries `FPS_Controller`, `Cloning`, `GrabScript`, `GrabIK`, `ViewmodelArms`, `InputManager`, `CharacterAnimator`, a `CharacterController`, a kinematic `Rigidbody`, and a child `Camera`.
- The runtime clone spawns from **`CloneNested.prefab`, a prefab *variant* of MainCharecter** (referenced by `Cloning.CloneGO`). The variant renames the root, tags it `Clone`, puts the model on the `CloneBody` layer / arms on `CloneArms`, disables `FPS_Controller`, removes `Cloning`, and overrides the camera's culling mask. **`Clone.prefab` is an older, unused version — editing it has no runtime effect.**
- **Consequence:** components/objects shared by both bodies should be added to **MainCharecter** (the base) — they propagate to the player, the scene instance, *and* the clone variant. This is how `CharacterAnimator`, `GrabIK`, `ViewmodelArms`, and the `Camera/HoldPoint` anchor reach both bodies from a single edit.
- The visible body is the **`FirstCharacter`** humanoid model (a nested FBX with an `Animator`, controller `MainCharacterController` assigned, root motion off). The `Camera` carries the first-person arms under **`Camera/Arms`** — a static `CameraViewArms` mesh plus a rigged `metarig` skeleton driven by `GrabIK`/`ViewmodelArms` — and a `HoldPoint` empty (the grab/hold anchor).

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
- **Exactly one camera is tagged `MainCamera` at a time** — a load-bearing invariant. `Camera.main` and every "is my view active?" check (ViewmodelArms look-sway's `CompareTag("MainCamera")`, GrabScript raycasts) depend on it. The clone's camera *inherits* the `MainCamera` tag from the base prefab, so `SpawnClone` retags the fresh clone's camera `Untagged` and disables its duplicate `AudioListener` (the player keeps control on spawn: `CloneActive = false`). Skip this and both bodies behave as the active view.
- `SwitchClone()` hands control over by **retagging the active camera as `MainCamera`** (the other `Untagged`) and toggling `FPS_Controller` + `CharacterController` between the bodies. It deliberately **does not** toggle `GrabScript` — that stays enabled on the player as the sole grabber (see grabbing section).
- `DestroyClone()` hands control back, animates the split closed, then calls `ReleaseAsRagdoll()` — it does **not** `Destroy` the clone GameObject.
- `CloneActive` (public bool) tracks which body has control.
- **Split-screen culling masks:** each body's camera renders the *other* body plus its *own* first-person arms — never its own body or the other's arms — plus the shared `DeadClone` layer. Player cam = `PlayerArms(11)` + `CloneBody(13)` + `DeadClone(14)`; clone cam = `CloneArms(12)` + `Player(10)` + `DeadClone(14)`. The clone's mask is a prefab-variant override in `CloneNested.prefab` (`m_CullingMask.m_Bits`); when you add a layer that both views must see, update **both** masks.

### First-person arms: procedural IK + retargeted sway (`Character/GrabIK.cs`, `Character/ViewmodelArms.cs`)
There are no hand-authored arm/finger clips. Both systems live on the body root (so they propagate base → variant) and each drives its *own* `Camera/Arms/metarig`. That viewmodel skeleton is a separate **Rigify**-named rig (`upper_arm.R`, `forearm.R`, `hand.R`, `palm.01.R`, `f_index.01.R`, `thumb.01.R`, …) with different bone names **and** axes than the body's Humanoid rig, so they can't be name-matched or Humanoid-retargeted.
- **`GrabIK.cs`** (LateUpdate) — a two-bone IK solver bends the right arm to reach the held object, and each finger phalanx curls until it contacts the held collider (`Collider.ClosestPoint`) so the hand conforms. **Rig quirks that are load-bearing:** the IK "hand" tip resolves to **`palm.01.R`**, *not* the wrist `hand.R` — `palm.01.R` uniquely parents *both* the index and the thumb, while `hand.R` parents four metacarpals (`palm.01..04.R`). Fingers are collected from `hand.R` so all five are driven. `m_OrientHandToAnchor` applies `m_HandGripEuler` **relative to the rest pose** (zero = natural); it must not snap a bone to an absolute world rotation, because the rig's bones carry a large baked roll. Curl axes/angles are serialized (rig-dependent); `m_DebugOverrideWeight` scrubs the grip pose without playing.
- **`ViewmodelArms.cs`** (`[DefaultExecutionOrder(100)]`, so it runs *after* GrabIK) — (1) *retarget* copies the body's animated arm swing onto the viewmodel bones via a basis change captured at startup (LateUpdate), fading the right-arm swing out under `GrabIK.GripWeight` while gripping; (2) *sway* is procedural walk-bob / breathing / look-sway on the whole `Arms` container (Update).
- **Gripping/sway is gated, not toggled.** Both bodies' components run every frame and each decides whether to act. Look-sway runs only for the active `MainCamera` body. `GrabIK` grips only for the body that is *holding* something: it finds the canonical grabber by tag `Main`, then grips iff `GrabScript.HoldCam == this body's camera` — so the grip pose stays with the grabber even after control switches away from it.

### Locomotion animation (`Character/CharacterAnimator.cs`)
On each body root; finds the child body `Animator` (the one with a controller assigned, not the controller-less arms Animator). Drives a single float **`Speed`** parameter from the body's *actual* horizontal movement (position delta), not from input. Because it measures real movement, the controlled body plays Walking while the idle body stays in Idle with no coupling to `Cloning`. The controller (`Models/AnimationsAndControllers/MainCharacterController.controller`) is just Idle ⇄ Walking with `Speed` threshold transitions.

### Ragdoll on clone dismissal (`Character/HumanoidRagdoll.cs`, `Character/Ragdoll.cs`)
`HumanoidRagdoll.Create(animator, velocity)` builds a ragdoll **at runtime** from the Humanoid avatar: it looks up the 11 standard bones via `Animator.GetBoneTransform`, adds Rigidbodies + Colliders + CharacterJoints (collider sizes computed in bone-local space so they're scale-correct), disables the Animator, and goes dynamic. `Cloning.ReleaseAsRagdoll()` calls it, then: retags the corpse off `Clone` (so a new clone can spawn), disables its controller/grab/camera components, adds a **`Ragdoll`** marker, **moves the corpse's model subtree to the `DeadClone` layer** (so every split-screen view renders corpses — live bodies on `CloneBody` are culled by the other view), and **destroys the corpse's `Camera` GameObject** once the split finishes (the first-person `Arms`/`CameraViewArms` are children of that camera and would otherwise hang in mid-air). The `Ragdoll` marker is how `GrabScript` recognises a corpse's body parts as grabbable. Ragdoll tuning (masses, joint limits, limb thickness) is constants in `HumanoidRagdoll.cs`.

### Object grabbing (`Character/GrabScript.cs`)
**Physics-based**, with a **single grabber**: the player's `GrabScript` (tag `Main`) — the target of all grab input — grabs on behalf of whichever body is active (raycasts from `Camera.main`). A hit is grabbable if its collider is on the `Pickable` layer **or** under a `Ragdoll` marker, and has a non-kinematic Rigidbody. At pickup it **captures the grabbing body's camera** (`HoldCam`) and each `FixedUpdate` drags the held body toward *that* camera's `HoldPoint` — so a held item **stays with the body that grabbed it across control switches** instead of following `Camera.main` to the newly-controlled body.
- **Rigid pickups** are pulled by velocity and rotated to the anchor (so they sit gripped, not tumbling).
- **The ragdoll** bone you aimed at is made **kinematic** and `MovePosition`/`MoveRotation`-ed to the hand each step (position + a rotation offset captured at grab), so it pins firmly and turns with the holder while the rest of the body dangles from it through the joints.

Because there is one hold slot, only one object is held at a time across both bodies. `Physics.IgnoreLayerCollision(heldLayer, Player)` stops a held object from shoving the carrier while held; it's restored on release. Move/rotate the `HoldPoint` anchor (or later parent it to a hand bone) to tune where/how things are held.

### Tags and layers are a hard contract
Code hardcodes layer **numbers** and looks objects up by **tag string** at runtime. Reordering `ProjectSettings/TagManager.asset` will silently break things.
- Layers (by index): `8 PostProccesing`, `9 Pickable`, `10 Player`, `11 PlayerArms`, `12 CloneArms`, `13 CloneBody`, `14 DeadClone`. `GrabScript` treats `Pickable` as grabbable and ignores collisions between the held object's layer and `Player`. Live clone body/arms sit on `CloneBody`/`CloneArms`; a **dismissed corpse is moved to `DeadClone`** so both cameras render it (resolved at runtime via `LayerMask.NameToLayer("DeadClone")`). A newly-added layer must be saved to disk (*File → Save Project*) and given a sensible Physics collision-matrix row, or it collides with everything by default.
- Tags used in code: `Main` (player root; also how `GrabIK`/`ViewmodelArms` find the canonical grabber `GrabScript`), `Clone` (live clone, found via `FindGameObjectWithTag`; a corpse is retagged `Untagged` so it's no longer found), plus the built-in `MainCamera`/`Untagged` swapped during control switching. Other defined tags: `KeyCard`, `PickUp`, `CloneArms`.

### Narrative text (`TextController.cs` + `Editor/TextControllerEditor.cs`)
A coroutine typewriter effect that prints `m_Text` one character at a time into a uGUI `Text`. Control characters: `/` clears the textbox; a `/` *following* a character inserts a pause (`TextPause`). The custom inspector adds a **"Get Text File"** button that loads a `.txt` (e.g. `Assets/_App/NarrativeText/NarrativeText.txt`) into `m_Text` via a file dialog.
