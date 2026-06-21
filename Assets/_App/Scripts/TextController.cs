using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;

public class TextController : MonoBehaviour
{
    public Text TextBox;
    [SerializeField]
    [Header("Needs to be low default val - .1f")]
    float TextSpeed = .1f;
    [SerializeField]
    [Header("Pause between texts")]
    float TextPause = 2f;
    public string m_Text;
    // Start is called before the first frame update
    void Awake()
    {
        TextBox = GetComponentInChildren<Text>();
        StartCoroutine(TextTyping());
    }
    IEnumerator TextTyping()
    {
        WaitForSeconds TextTypeSpeed = new WaitForSeconds(TextSpeed);
        WaitForSeconds pauseWait = new WaitForSeconds(TextPause);
        char[] letters = m_Text.ToCharArray();

        for (int i = 0; i < letters.Length; i++)
        {
            if (letters[i] == '/')
            {
                TextBox.text = null;
                yield return TextTypeSpeed;
            }
            else
            {
                if (i < letters.Length -1 && letters[i + 1] == '/')
                {
                    yield return pauseWait;
                }
                else
                {
                    TextBox.text += letters[i].ToString();
                    yield return TextTypeSpeed;
                }
            }
        }
        yield return pauseWait;
        TextBox.text = null;
    }
}
