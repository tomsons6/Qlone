/// <summary>
/// Anything the player can look at and press with the interact key. <see cref="Interactor"/>
/// raycasts from the active camera and calls <see cref="Interact"/> on the first implementor
/// found on the hit collider or its parents.
///
/// Implementors decide for themselves which body acted: <c>Camera.main</c> is the body in
/// control at the moment the key was pressed (exactly one camera is tagged MainCamera at a
/// time -- see <see cref="Cloning"/>), so it doubles as the acting body's identity token, the
/// same one <see cref="GrabScript.IsHolding"/> keys on.
/// </summary>
public interface IInteractable
{
    /// <summary>Called when the player looks at this and presses the interact key.</summary>
    void Interact();
}
