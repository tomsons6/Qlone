using System.Collections;
using System.Collections.Generic;
using UnityEngine;

public class Cloning : MonoBehaviour
{
    [SerializeField]
    GameObject CloneGO;
    [SerializeField]
    float spawnDistance = 1;

    Vector3 playerPos;
    Vector3 playerDirection;
    Quaternion playerRotation;
    bool CloneActive;


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
                gameObject.GetComponentInChildren<Camera>().rect = new Rect(-4.5f, 0f, 5f, 5f);
                Instantiate(CloneGO, spawnPos, playerRotation);
                CloneActive = false;
                CloneGO.GetComponentInChildren<Camera>().rect = new Rect(0.5f, 0f, 5f, 5f);
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
                            gameObject.GetComponentInChildren<Camera>().rect = new Rect(-4.5f, 0f, 5f, 5f);
                            Instantiate(CloneGO, spawnPos, playerRotation);
                            CloneActive = false;
                            CloneGO.GetComponentInChildren<Camera>().rect = new Rect(0.5f, 0f, 5f, 5f);
                            CloneGO.GetComponent<FPS_Controller>().enabled = false;
                            return;
                        }
                        //spawn backside
                        playerDirection = gameObject.transform.forward * -1f;
                        spawnPos = playerPos + playerDirection * spawnDistance;
                        gameObject.GetComponentInChildren<Camera>().rect = new Rect(-4.5f, 0f, 5f, 5f);
                        Instantiate(CloneGO, spawnPos, playerRotation);
                        CloneActive = false;
                        CloneGO.GetComponentInChildren<Camera>().rect = new Rect(0.5f, 0f, 5f, 5f);
                        return;
                    }
                    //spawn left
                    playerDirection = gameObject.transform.right * -1f;
                    spawnPos = playerPos + playerDirection * spawnDistance;
                    gameObject.GetComponentInChildren<Camera>().rect = new Rect(-4.5f, 0f, 5f, 5f);
                    Instantiate(CloneGO, spawnPos, playerRotation);
                    CloneActive = false;
                    CloneGO.GetComponentInChildren<Camera>().rect = new Rect(0.5f, 0f, 5f, 5f);
                    return;
                }
            }
        }
    }
    public void DestroyClone()
    {
        if (GameObject.FindGameObjectWithTag("Clone") != null)
        {
            Destroy(GameObject.FindGameObjectWithTag("Clone"));
            gameObject.GetComponentInChildren<Camera>().rect = new Rect(0f, 0f, 5f, 5f);
            gameObject.GetComponentInChildren<Camera>().tag = "MainCamera";
            gameObject.GetComponent<FPS_Controller>().enabled = true;
            gameObject.GetComponent<CharacterController>().enabled = true;
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
}
