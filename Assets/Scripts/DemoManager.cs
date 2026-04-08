// File: DemoManager.cs
using UnityEngine;
using UnityEngine.UI;
using TMPro;

public class DemoManager : MonoBehaviour
{
    public GameObject staticBlock;
    public GameObject ghostOverlay;
    public TextMeshProUGUI captionText;
    public AudioSource audioSource;
    public AudioClip stepAudio;
    public Button gotItButton;

    private bool userConfirmed = false;

    void Start()
    {
        gotItButton.onClick.AddListener(OnGotItClicked);
        StartCoroutine(DemoRoutine());
    }

    void OnGotItClicked()
    {
        userConfirmed = true;
    }

    System.Collections.IEnumerator DemoRoutine()
    {
        while (!userConfirmed)
        {
            // Step 1: Static block only
            staticBlock.SetActive(true);
            ghostOverlay.SetActive(false);
            captionText.text = "";
            yield return new WaitForSeconds(1f);

            // Step 2: Show ghost, caption, and audio
            ghostOverlay.SetActive(true);
            captionText.text = "Step 1: Insert crankshaft";
            audioSource.clip = stepAudio;
            audioSource.Play();
            yield return new WaitForSeconds(5f);

            // Step 3: Show instruction while waiting for user input
            captionText.text = "Tap or press trigger when ready";
            float waitTime = 0f;

            // Wait up to N seconds, then repeat
            while (!userConfirmed && waitTime < 5f) // 10s between repeats
            {
                waitTime += Time.deltaTime;
                yield return null;
            }
        }

        // Once confirmed, finish
        captionText.text = "Demo complete!";
        gotItButton.gameObject.SetActive(false);
        audioSource.Stop();
    }

}
