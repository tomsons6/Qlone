using System.Collections;
using System.Collections.Generic;
using UnityEngine;

public class Cloning : MonoBehaviour
{
    [SerializeField]
    GameObject CloneGO;
    [SerializeField]
    float spawnDistance = 1;

    [Header("Camera transition")]
    [Tooltip("Seconds the split-screen open/close animation takes.")]
    [SerializeField]
    float transitionDuration = 0.35f;
    [Tooltip("Viewport rect for a single, full-screen view (the camera's default).")]
    [SerializeField]
    Rect fullScreenRect = new Rect(0f, 0f, 1f, 1f);
    [Tooltip("Viewport rect for the left (player) view while split.")]
    [SerializeField]
    Rect leftSplitRect = new Rect(-4.5f, 0f, 5f, 5f);
    [Tooltip("Viewport rect for the right (clone) view while split.")]
    [SerializeField]
    Rect rightSplitRect = new Rect(0.5f, 0f, 5f, 5f);

    Vector3 playerPos;
    Vector3 playerDirection;
    Quaternion playerRotation;
    public bool CloneActive;

    Coroutine playerCamRoutine;
    Coroutine cloneCamRoutine;


    // Start is called before the first frame update
    void Start()
    {

    }

    // Update is called once per frame
    void Update()
    {

    }
    public void Clone()
    {
        playerPos = gameObject.transform.localPosition;
        playerDirection = gameObject.transform.right;
        playerRotation = gameObject.transform.localRotation;

        Vector3 spawnPos = playerPos + playerDirection * spawnDistance;
        Ray CheckDown = new Ray(spawnPos, Vector3.down);
        Ray CheckRight = new Ray(gameObject.transform.position, playerDirection);
        Ray CheckLeft = new Ray(gameObject.transform.position, -playerDirection);
        Ray CheckBack = new Ray(gameObject.transform.position, -gameObject.transform.forward);
        RaycastHit hitDown, hitRight, hitLeft, hitBack;

        //check for clones
        if (GameObject.FindGameObjectsWithTag("Clone").Length == 0)
        {
            Debug.DrawRay(gameObject.transform.position, playerDirection * spawnDistance, Color.red, 2);
            //check if right is clear if not hit then spawn
            if (!Physics.Raycast(CheckRight, out hitRight, spawnDistance))
            {
                Debug.DrawRay(spawnPos, Vector3.down * spawnDistance, Color.blue, 2f);
                SpawnClone(spawnPos, playerRotation);
            }
            else
            {
                //check spawn point down orw right for collider
                if (Physics.Raycast(CheckDown, out hitDown, spawnDistance) || Physics.Raycast(CheckRight, out hitRight, spawnDistance))
                {
                    //check left side
                    if (Physics.Raycast(CheckLeft, out hitLeft, spawnDistance))
                    {
                        //right side
                        if (Physics.Raycast(CheckBack, out hitBack, spawnDistance))
                        {
                            //spawn infornt
                            playerDirection = gameObject.transform.forward;
                            spawnPos = playerPos + playerDirection * spawnDistance;
                            SpawnClone(spawnPos, playerRotation);
                            return;
                        }
                        //spawn backside
                        playerDirection = gameObject.transform.forward * -1f;
                        spawnPos = playerPos + playerDirection * spawnDistance;
                        SpawnClone(spawnPos, playerRotation);
                        return;
                    }
                    //spawn left
                    playerDirection = gameObject.transform.right * -1f;
                    spawnPos = playerPos + playerDirection * spawnDistance;
                    SpawnClone(spawnPos, playerRotation);
                    return;
                }
            }
        }
    }

    // Spawns the clone instance and animates the screen splitting open:
    // the player view shrinks to the left while the clone view slides in from
    // the right edge, so the two halves tile the screen exactly throughout.
    void SpawnClone(Vector3 position, Quaternion rotation)
    {
        GameObject clone = Instantiate(CloneGO, position, rotation);
        CloneActive = false;

        Camera playerCam = gameObject.GetComponentInChildren<Camera>();
        Camera cloneCam = clone.GetComponentInChildren<Camera>();

        AnimateCameraRect(ref playerCamRoutine, playerCam, fullScreenRect, leftSplitRect);

        // Start the clone's view just off the right edge, then slide it in.
        Rect cloneStart = new Rect(1f, rightSplitRect.y, rightSplitRect.width, rightSplitRect.height);
        AnimateCameraRect(ref cloneCamRoutine, cloneCam, cloneStart, rightSplitRect);
    }

    public void DestroyClone()
    {
        GameObject clone = GameObject.FindGameObjectWithTag("Clone");
        if (clone == null)
        {
            return;
        }

        Camera playerCam = gameObject.GetComponentInChildren<Camera>();
        Camera cloneCam = clone.GetComponentInChildren<Camera>();

        // Hand control straight back to the player so input stays responsive
        // while the cameras animate.
        playerCam.tag = "MainCamera";
        gameObject.GetComponent<FPS_Controller>().enabled = true;
        gameObject.GetComponent<CharacterController>().enabled = true;
        gameObject.GetComponent<GrabScript>().enabled = true;
        CloneActive = false;
        if (cloneCam != null)
        {
            cloneCam.tag = "Untagged";
        }

        // Animate the split closing, then leave the clone behind as a ragdoll.
        AnimateCameraRect(ref playerCamRoutine, playerCam, playerCam.rect, fullScreenRect);
        Rect cloneEnd = new Rect(1f, rightSplitRect.y, rightSplitRect.width, rightSplitRect.height);
        if (cloneCam != null)
        {
            AnimateCameraRect(ref cloneCamRoutine, cloneCam, cloneCam.rect, cloneEnd);
        }
        ReleaseAsRagdoll(clone, cloneCam);
    }

    // Converts the spawned clone into a physics ragdoll that stays in the world
    // instead of destroying it. Frees the "Clone" tag so the player can spawn
    // another clone, and shuts down the corpse's control and camera components.
    void ReleaseAsRagdoll(GameObject clone, Camera cloneCam)
    {
        // Free the tag first so a new clone can be spawned while this one lingers.
        clone.tag = "Untagged";

        // Capture momentum (if it was moving) before disabling its controller.
        Vector3 velocity = Vector3.zero;
        CharacterController cc = clone.GetComponent<CharacterController>();
        if (cc != null)
        {
            velocity = cc.velocity;
            cc.enabled = false;
        }

        FPS_Controller fps = clone.GetComponent<FPS_Controller>();
        if (fps != null)
        {
            fps.enabled = false;
        }
        GrabScript grab = clone.GetComponent<GrabScript>();
        if (grab != null)
        {
            grab.enabled = false;
        }
        CharacterAnimator charAnim = clone.GetComponent<CharacterAnimator>();
        if (charAnim != null)
        {
            charAnim.enabled = false;
        }
        Rigidbody rootRb = clone.GetComponent<Rigidbody>();
        if (rootRb != null)
        {
            rootRb.isKinematic = true;
        }

        // Keep the deforming mesh visible even when its bounds leave the frustum.
        foreach (SkinnedMeshRenderer smr in clone.GetComponentsInChildren<SkinnedMeshRenderer>())
        {
            smr.updateWhenOffscreen = true;
        }

        Animator animator = clone.GetComponentInChildren<Animator>();
        bool ragdolled = HumanoidRagdoll.Create(animator, velocity);
        if (!ragdolled)
        {
            // Not a humanoid rig - fall back to simply removing the clone.
            StartCoroutine(DestroyAfter(clone, transitionDuration));
            return;
        }

        // Mark the corpse so the grab system treats its body parts as grabbable.
        clone.AddComponent<Ragdoll>();

        // Stop the corpse rendering its (now off-screen) view once the split finishes.
        StartCoroutine(DisableCloneViewAfter(cloneCam, transitionDuration));
    }

    IEnumerator DisableCloneViewAfter(Camera cam, float delay)
    {
        yield return new WaitForSeconds(delay);
        if (cam != null)
        {
            cam.enabled = false;
            AudioListener listener = cam.GetComponent<AudioListener>();
            if (listener != null)
            {
                listener.enabled = false;
            }
        }
    }
    public void SwitchClone()
    {
        if (GameObject.FindGameObjectWithTag("Clone") != null)
        {
            if(CloneActive == false)
            {
                CloneActive = true;
            }
            else
            {
                CloneActive = false;
            }
            GameObject TempCloneGO = GameObject.FindGameObjectWithTag("Clone");
            if (TempCloneGO != null)
            {
                if (CloneActive == false)
                {
                    gameObject.GetComponentInChildren<Camera>().tag = "MainCamera";
                    gameObject.GetComponent<FPS_Controller>().enabled = true;
                    gameObject.GetComponent<CharacterController>().enabled = true;

                    TempCloneGO.GetComponentInChildren<Camera>().tag = "Untagged";
                    TempCloneGO.GetComponent<FPS_Controller>().enabled = false;
                    TempCloneGO.GetComponent<CharacterController>().enabled = false;

                }
                else
                {
                    TempCloneGO.GetComponentInChildren<Camera>().tag = "MainCamera";
                    TempCloneGO.GetComponent<FPS_Controller>().enabled = true;
                    TempCloneGO.GetComponent<CharacterController>().enabled = true;

                    gameObject.GetComponentInChildren<Camera>().tag = "Untagged";
                    gameObject.GetComponent<FPS_Controller>().enabled = false;
                    gameObject.GetComponent<CharacterController>().enabled = false;
                }
            }

        }
    }

    // Restarts a camera's rect tween, cancelling any tween already running on it.
    void AnimateCameraRect(ref Coroutine routine, Camera cam, Rect from, Rect to)
    {
        if (cam == null)
        {
            return;
        }
        if (routine != null)
        {
            StopCoroutine(routine);
        }
        cam.rect = from;
        routine = StartCoroutine(LerpRect(cam, from, to, transitionDuration));
    }

    IEnumerator LerpRect(Camera cam, Rect from, Rect to, float duration)
    {
        float elapsed = 0f;
        while (elapsed < duration && cam != null)
        {
            elapsed += Time.deltaTime;
            float t = duration > 0f ? Mathf.SmoothStep(0f, 1f, elapsed / duration) : 1f;
            cam.rect = new Rect(
                Mathf.Lerp(from.x, to.x, t),
                Mathf.Lerp(from.y, to.y, t),
                Mathf.Lerp(from.width, to.width, t),
                Mathf.Lerp(from.height, to.height, t));
            yield return null;
        }
        if (cam != null)
        {
            cam.rect = to;
        }
    }

    IEnumerator DestroyAfter(GameObject target, float delay)
    {
        yield return new WaitForSeconds(delay);
        if (target != null)
        {
            Destroy(target);
        }
    }
}
