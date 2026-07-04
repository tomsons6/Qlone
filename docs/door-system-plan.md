# Door System + Two-Hold-Slot Grab — Implementation Plan

## Context

Qlone needs doors. The start room is a **2-scanner keycard puzzle** that must require **both keycards held at the same time** — one carried by the player, one by the clone — which is the game's core clone mechanic. Beyond that, the user wants a **reusable** system that also covers switch/button doors and "two buttons pressed at once" doors.

Confirmed decisions (from the user):
- Doors move by **procedural code tween** (mirror `Cloning.LerpRect`/`AnimateCameraRect` SmoothStep easing), not Animator clips.
- The keycard puzzle requires both cards **held simultaneously** → the grab system, which currently holds only **one** object across both bodies, must be extended to **two hold slots (one per body)**.
- Support **both** pressure plates (presence) and look-and-press interact buttons/switches → adds an interact action.

Current reality (verified): the door art (`StartDoor.prefab`, two leaves), two card-reader models, and a keycard placeholder are already placed in the start room, but there is **zero** door/trigger/interaction code — this is greenfield. The two door leaves share one transform (left/right offset is baked into the meshes), so a slide moves each leaf along opposite local/world **X**.

### Resolved technical defaults (recommended answers baked in; all are tunable later)
- **Held-vs-carrier collision:** pairwise `Physics.IgnoreCollision(card, carrier)` per hold (not the old global `IgnoreLayerCollision(9,10)`, which can't refcount two cards and also over-disables collisions). This also fixes a pre-existing caveat.
- **Door slide:** ±X in the leaves' parent space, magnitude ≈ leaf width (start ~1.5, tune visually); duration ~0.6 s.
- **Door leaves:** add a **kinematic Rigidbody** to each so the moving convex MeshCollider is physics-correct.
- **Keycard tag:** retag `PickUp` → `KeyCard` (safe — grabbing keys off layer 9; grep shows nothing depends on `PickUp`).
- **Interact key:** default `KeyCode.G` (rebindable in the Inspector; Q/F/E/R are taken).
- **Button/switch:** one `InteractButton` component with a `m_Latching` flag (latching = switch, momentary = button).

## Conventions to follow
`m_PascalCase` `[SerializeField]` privates with `[Tooltip]`/`[Header]`; XML `///` docs; `const` for hardcoded layer numbers; expression-bodied read-only properties; auto-resolve helpers (resolve grabber by tag `Main`, like `GrabIK`); stored-`Coroutine` + `StopCoroutine` + `Mathf.SmoothStep` easing (`Cloning.cs`); empty marker components (`Ragdoll.cs`); UnityEvent-in-Inspector aggregator idiom (`InputManager.cs`). New door files go in `Assets/_App/Scripts/Door/`; the player input component goes in `Assets/_App/Scripts/Character/`.

---

## Phase 1 — `GrabScript.cs`: two hold slots (one per body)

Replace the single `m_Held*` fields with a small private `Hold` class and a list (max one per body):

```
class Hold {
    Rigidbody Body; Collider Collider; int Layer;
    bool UsedGravity, WasKinematic, IsRagdoll;
    Vector3 GrabLocalPoint; Quaternion GrabRotOffset;
    Transform HoldCam;   // body/camera that owns this hold
    Collider  Carrier;   // this body's CharacterController collider (for pairwise IgnoreCollision)
}
readonly List<Hold> m_Holds = new List<Hold>(2);
```

**New per-body public API** (replaces `HandOccupied`/`HoldCam`/`GrabWorldPosition`/`HeldCollider`):
- `bool IsHolding(Transform bodyCam)`
- `Vector3 GetGrabWorldPosition(Transform bodyCam)`
- `Collider GetHeldCollider(Transform bodyCam)`
- `bool IsHeldRigidbody(Rigidbody rb)` and `bool IsHeld(Collider c)` — for the scanner
- `void ReleaseForCamera(Transform bodyCam)` — deterministic external release
- Keep param-less `PickUpObject()` / `ReleaseObject()` (delegate to `Camera.main`'s slot) so existing Inspector wiring (E, R) still works.
- Private helpers `FindByCam` / `FindByBody`.

**`ToggleGrab()`** (preserves the three recently-fixed behaviors):
```
cam = Camera.main; if (cam == null) return;
mine = FindByCam(cam.transform);
if (mine != null) { Release(mine); return; }          // holder press = drop, return (no drop-then-regrab)
if (!TryFindGrabTarget(cam, out hit, out body, out isRagdoll)) return;
PickUp(cam, hit, body, isRagdoll);                     // transfers if the OTHER body holds it
```

**`PickUp(...)`:** one object per body (`FindByCam` guard); if another body holds this `body`, `Release` it first (transfer); capture state; ragdoll → kinematic + `GrabRotOffset`, else gravity off; `Physics.IgnoreCollision(collider, carrier, true)` where `carrier = cam.GetComponentInParent<CharacterController>()`; add to `m_Holds`.

**`Release(Hold)`:** `IgnoreCollision(..., false)`, restore kinematic/gravity, remove from list.

**`FixedUpdate()`:** iterate `m_Holds` backwards; if `Body == null || HoldCam == null` → `Release` (this auto-drops the clone's card once its camera is destroyed); else run the existing ragdoll-pin vs velocity-pull math against `h.Body`/`h.HoldCam`.

Existing serialized tuning fields and `TryFindGrabTarget` are unchanged.

## Phase 2 — Update grab consumers

- **`GrabIK.cs`** (3 call sites, keep grabber-by-tag-`Main`): `ShouldGrip()` → `m_HoldSource.IsHolding(m_Cam.transform)`; `ComputeTargetPosition()` → `IsHolding` guard + `GetGrabWorldPosition(m_Cam.transform)`; `ContactCollider()` → `GetHeldCollider(m_Cam.transform)`. Result: each body grips its own card independently — both can grip at once.
- **`BodyGrabIK.cs`** (2 call sites): `IsHolder()` → `IsHolding(m_Cam.transform)`; `OnAnimatorIK` reach target → `GetGrabWorldPosition(m_Cam.transform)`.
- **`Cloning.cs`**: in `DestroyClone()` (before the clone camera is torn down) call `GetComponent<GrabScript>()?.ReleaseForCamera(cloneCam.transform)` so the clone's card drops immediately when it ragdolls. Keep existing `grab.enabled = false`.
- **`ViewmodelArms.cs`**: no change (reads only `GrabIK.GripWeight`).

Compile-check here — the new API must land in the same change that removes the old getters.

---

## Phase 3 — Door core (`Scripts/Door/`)

- **`Door.cs`** — reusable actuator. Serialized `DoorPart[] m_Parts` (nested `[System.Serializable]`: `Transform Target`, `Vector3 LocalPositionOffset`, `Vector3 LocalRotationEulerOffset`, hidden captured closed pose), `float m_OpenDuration = 0.6f`, `bool m_StartOpen`, `UnityEvent m_OnOpened/m_OnClosed`. API: `bool IsOpen`, `Open()`, `Close()`, `SetOpen(bool)`, `Toggle()`. `Awake` captures each part's closed `localPosition`/`localRotation`. `SetOpen` stops the stored coroutine and starts `Animate(open)`, which mirrors `LerpRect`: `t = SmoothStep(0,1,elapsed/duration)`, `Lerp` position between closed and `closed+offset`, `Slerp` rotation between closed and `closed*Euler(offset)`, snap at end, invoke the UnityEvent. Sliding door = two parts, opposite X offsets; hinged door = one part with a rotation offset.
- **`DoorCondition.cs`** — abstract base: `bool IsMet => m_Met`, `event Action<DoorCondition> OnChanged`, protected `SetMet(bool)` that only fires on change.
- **`DoorLock.cs`** — aggregator: `Door m_Door`, `DoorCondition[] m_Conditions`, `enum Mode {All, Any, AtLeast}` + `int m_AtLeast`, `bool m_Latch`, `bool m_AutoClose` + `float m_CloseDelay`. `OnEnable` subscribes + `Evaluate`; `OnDisable` unsubscribes. `Evaluate` counts met, applies mode → `m_Door.Open()` (and latches) or (if not latched) `Close()`/delayed close. Auto-resolve `m_Door` via `GetComponentInParent<Door>()` and `m_Conditions` via `GetComponentsInChildren<DoorCondition>()` when empty.

## Phase 4 — Conditions (`Scripts/Door/Conditions/`)

- **`KeyCardScanner.cs : DoorCondition`** — needs a **trigger** collider. `[SerializeField] string m_AcceptedTag = "KeyCard"`; resolve grabber by tag `Main`. `OnTriggerEnter/Exit` track occupant **`attachedRigidbody`s** in a `HashSet` (dedupes the card's twin colliders). `Update()`: `SetMet(any occupant rb where m_Grabber.IsHeldRigidbody(rb))` and prune nulls. (Evaluate in `Update`, not `OnTriggerStay` — a held card is moved each FixedUpdate so it stays inside, and this is the reliable "inside AND held" test.)
- **`PressurePlate.cs : DoorCondition`** — trigger collider; `[SerializeField] LayerMask m_Mask` (set to `Player`), optional required tag. `OnTriggerEnter/Exit` filter → occupant set → `SetMet(count > 0)`. Two plates under one `DoorLock(All)` = "two buttons at once" (player + clone).
- **`InteractButton.cs : DoorCondition`** — `[SerializeField] bool m_Latching = true`, `float m_MomentaryPulse = 0.1f`, `UnityEvent m_OnPressed`. Public `Interact()`: latching → `SetMet(!IsMet)`; momentary → `SetMet(true)` + timed `SetMet(false)`. Combine momentary with `DoorLock.m_Latch` to latch a door open on a single press.

## Phase 5 — Interact input

- **`InputManager.cs`** — add a 5th key block mirroring Q/F/E/R: `KeyboardButtonInteract` (default `G`) + `OnInteractDown/Up/Hold` UnityEvents + the matching `GetKeyDown/Up/Key` lines in `Update()`.
- **`Interactor.cs`** (`Scripts/Character/`, on the player root, tag `Main`) — `public void Interact()`: raycast from `Camera.main` for `m_Distance`; if the hit has an `InteractButton` in its parents, call `Interact()` on it. Single player-owned actor, acts for whichever body is active (like `GrabScript`). The clone inherits it but its `InputManager` is disabled on spawn, so no double-fire — **no extra disabling needed**.

## Phase 6 — `KeyCard.prefab`

Normalize the scene placeholder into `Assets/_App/Prefabs/.../KeyCard.prefab`: `tag = KeyCard`, **one solid** BoxCollider on layer `Pickable(9)` (remove the stray trigger collider and the inert `ParentConstraint`), non-kinematic Rigidbody. Place **two** instances in `DemoLevel` (one reachable by the player, one by the clone).

## Phase 7 — Scene / prefab wiring (`DemoLevel`, `Level/StartRoomModels/…`)

- **StartDoor:** add `Door` to the root; `m_Parts` = `StartRoomDoorLeft` (offset `+X`) and `StartRoomDoorRight` (offset `−X`), duration ~0.6. Add a **kinematic Rigidbody** to each leaf (keep the convex MeshCollider). Add `DoorLock` (Mode `All`, `m_Latch = true`) referencing the `Door` and the two scanners.
- **Card readers** (`CardReaderBottom`, `CardReaderTop`): add a child with a **trigger** BoxCollider (sized to the slot) + `KeyCardScanner`.
- **Player** (`MainCharecter.prefab`): add `Interactor` to the root; set `InputManager.KeyboardButtonInteract = G` and wire `OnInteractDown → Interactor.Interact` (persistent Inspector call, like E→`ToggleGrab`).
- **Optional demo of the other door types:** two `PressurePlate`s under a `DoorLock(All)`, and a mesh + collider + `InteractButton` under a `DoorLock`, on a second door.

## Phase 8 — Layers / tags
No new layers or tags. Reuse `KeyCard` tag + `Pickable(9)`/`Player(10)`. Only change: the two keycards use tag `KeyCard`.

---

## New / changed files
**New:** `Scripts/Door/Door.cs`, `Scripts/Door/DoorCondition.cs`, `Scripts/Door/DoorLock.cs`, `Scripts/Door/Conditions/KeyCardScanner.cs`, `Scripts/Door/Conditions/PressurePlate.cs`, `Scripts/Door/Conditions/InteractButton.cs`, `Scripts/Character/Interactor.cs`, `Prefabs/.../KeyCard.prefab`.
**Edited:** `Scripts/Character/GrabScript.cs` (major), `GrabIK.cs`, `BodyGrabIK.cs`, `Cloning.cs`, `InputManager.cs`; scene `DemoLevel.unity`; `StartDoor.prefab` (leaf Rigidbodies).

## Risks
- **Grab refactor ripple (highest):** GrabIK/BodyGrabIK must move to the per-cam API in the same change that removes the old getters. Re-verify the three fixed behaviors: holder-press-drops-and-returns, holder gating survives control switches, take-from-other-body.
- **Clone card cleanup:** the clone's card is owned by the player's grabber in the clone-camera slot; `DestroyClone` must `ReleaseForCamera`; the `FixedUpdate` `HoldCam==null` branch is the safety net.
- **Moving MeshCollider:** add the kinematic Rigidbody per leaf; if the player can be flush against a closing door, prefer `MovePosition` in `FixedUpdate`.
- **Reliable "held":** track occupants by `attachedRigidbody` and test `IsHeldRigidbody` in `Update`; prune nulls (card may be destroyed).

## Verification
1. Compile gate after Phase 2 and Phase 5: `refresh_unity` (scope `all` for new files) → `read_console` (errors) → confirm `editor_state` ready. Fix before wiring.
2. Build scene structure via `manage_prefabs`/`manage_components`/`manage_gameobject`/`manage_asset`; re-check console after each structural change.
3. **Input can't be simulated over MCP** — user runs these in Play mode:
   - E grabs a card → viewmodel + body arm reach; switch (F) → grip stays with the grabbing body.
   - Player holds card A; spawn clone (Q); switch; clone grabs card B → **both bodies hold a card at once** (the two-slot goal).
   - Hold each card in a reader → door slides both leaves apart and latches open.
   - Dismiss the clone (R) while it holds a card → the card drops immediately.
   - (If demo built) two plates: player + clone simultaneously → opens; step off → auto-closes. Interact key on a switch → toggles.
