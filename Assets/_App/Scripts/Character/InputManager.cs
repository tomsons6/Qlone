using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Events;
using UnityEngine.UIElements;

public class InputManager : MonoBehaviour
{
    [Header("Q")]
    public KeyCode KeyboardButtonQ;
    public UnityEvent OnQDown = new UnityEvent();
    public UnityEvent OnQUp = new UnityEvent();
    public UnityEvent OnQHold = new UnityEvent();

    [Header("F")]
    public KeyCode KeyboardButtonF;
    public UnityEvent OnFDown = new UnityEvent();
    public UnityEvent OnFUp = new UnityEvent();
    public UnityEvent OnFHold = new UnityEvent();

    [Header("E")]
    public KeyCode KeyboardButtonE;
    public UnityEvent OnEDown = new UnityEvent();
    public UnityEvent OnEUp = new UnityEvent();
    public UnityEvent OnEHold = new UnityEvent();

    [Header("R")]
    public KeyCode KeyboardButtonR;
    public UnityEvent OnRDown = new UnityEvent();
    public UnityEvent OnRUp = new UnityEvent();
    public UnityEvent OnRHold = new UnityEvent();

    void Update()
    {
        //--------Q button------
        if (Input.GetKeyDown(KeyboardButtonQ))
        {
            OnQDown.Invoke();
        }
        if (Input.GetKeyUp(KeyboardButtonQ))
        {
            OnQUp.Invoke();
        }
        if(Input.GetKey(KeyboardButtonQ))
        {
            OnQHold.Invoke();
        }
        //--------F button------
        if (Input.GetKeyDown(KeyboardButtonF))
        {
            OnFDown.Invoke();
        }
        if (Input.GetKeyUp(KeyboardButtonF))
        {
            OnFUp.Invoke();
        }
        if (Input.GetKey(KeyboardButtonF))
        {
            OnFHold.Invoke();
        }
        //--------E button------
        if (Input.GetKeyDown(KeyboardButtonE))
        {
            OnEDown.Invoke();
        }
        if (Input.GetKeyUp(KeyboardButtonE))
        {
            OnEUp.Invoke();
        }
        if (Input.GetKey(KeyboardButtonE))
        {
            OnEHold.Invoke();
        }
        //--------R button------
        if (Input.GetKeyDown(KeyboardButtonR))
        {
            OnRDown.Invoke();
        }
        if (Input.GetKeyUp(KeyboardButtonR))
        {
            OnRUp.Invoke();
        }
        if (Input.GetKey(KeyboardButtonR))
        {
            OnRHold.Invoke();
        }
    }
}
