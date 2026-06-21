using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Animations;

public class GrabScript : MonoBehaviour
{
    [SerializeField]
    float m_pickUpDistance;
    GameObject m_ObjInHand;
    bool m_HandOccupied;
    Cloning CloningScript;
    private void Start()
    {
        
    }
    public void PickUpObject()
    {
        if (CloningScript == null)
        {
            CloningScript = GameObject.FindGameObjectWithTag("Main").GetComponent<Cloning>();
        }
        Ray checkfront = new Ray(Camera.main.transform.position, Camera.main.transform.forward);
        RaycastHit hitObj;
        if (Physics.Raycast(checkfront, out hitObj, m_pickUpDistance))
        {
            Debug.DrawRay(Camera.main.transform.position, Camera.main.transform.forward * m_pickUpDistance, Color.blue, 2f);
            if (hitObj.transform.gameObject.layer == 9)
            {
                m_ObjInHand = hitObj.transform.gameObject;
            }
            if (m_ObjInHand != null)
            {
                Physics.IgnoreLayerCollision(9, 10, true);
                List<ConstraintSource> TempSources = new List<ConstraintSource>();
                List<ConstraintSource> UpdatedSources = new List<ConstraintSource>();
                m_ObjInHand.GetComponent<ParentConstraint>().GetSources(TempSources);
                if(GameObject.FindGameObjectWithTag("Clone") == true)
                {
                    if (!TempSources.Equals(UpdatedSources))
                    {
                        foreach (ConstraintSource source in TempSources)
                        {
                            ConstraintSource TempSource = source;
                            if (source.sourceTransform == null)
                            {
                                TempSource.sourceTransform = GameObject.FindGameObjectWithTag("CloneArms").transform;
                                UpdatedSources.Add(TempSource);
                                m_ObjInHand.GetComponent<ParentConstraint>().SetSources(UpdatedSources);
                                return;
                            }
                            if (source.sourceTransform.parent.transform.parent.tag == "Main" && !CloningScript.CloneActive)
                            {
                                if (!UpdatedSources.Contains(TempSource))
                                {
                                    Debug.Log("asda");
                                    TempSource.weight = 1f;
                                    UpdatedSources.Add(TempSource);

                                }
                            }
                            if (source.sourceTransform.parent.transform.parent.tag == "Clone" && CloningScript.CloneActive)
                            {
                                if (!UpdatedSources.Contains(TempSource))
                                {
                                    Debug.Log("asda");
                                    TempSource.weight = 1f;
                                    UpdatedSources.Add(TempSource);

                                }
                            }
                            else
                            {
                                if (!UpdatedSources.Contains(TempSource))
                                {
                                    TempSource.weight = 0f;
                                    UpdatedSources.Add(TempSource);
                                }
                            }

                        }
                        m_ObjInHand.GetComponent<ParentConstraint>().SetSources(UpdatedSources);
                    }          
                }

                else
                {
                    foreach (ConstraintSource source in TempSources)
                    {
                        ConstraintSource TempSource = source;
                        if (TempSource.sourceTransform != null)
                        {
                            if (!UpdatedSources.Contains(TempSource))
                            {
                                TempSource.weight = 1f;
                                UpdatedSources.Add(TempSource);
                            }

                        }
                        else
                        {

                            if (!UpdatedSources.Contains(TempSource))
                            {
                                TempSource.weight = 0f;
                                UpdatedSources.Add(TempSource);
                            }

                        }
                    }
                    m_ObjInHand.GetComponent<ParentConstraint>().SetSources(UpdatedSources);
                }
                m_ObjInHand.GetComponent<ParentConstraint>().constraintActive = true;
                m_HandOccupied = true;
            }

        }
    }
    public void ReleaseObject()
    {
        if (m_ObjInHand != null && m_HandOccupied)
        {
            Physics.IgnoreLayerCollision(9, 10, false);
            m_ObjInHand.GetComponent<ParentConstraint>().constraintActive = false;
            m_HandOccupied = false;
        }
        m_ObjInHand = null;
    }
}
