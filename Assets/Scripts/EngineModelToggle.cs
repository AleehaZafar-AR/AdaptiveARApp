using System.Collections;
using System.Collections.Generic;
using UnityEngine;

public class EngineModelToggle : MonoBehaviour
{
    public GameObject v8EngineAssembled;
    public GameObject v8EngineDisassembled;
    public GameObject DisassembleButton;
    public GameObject instructionPanel;
    // Start is called before the first frame update
    void Start()
    {
        
    }

    // Update is called once per frame
    void Update()
    {
        
    }

    public void displayEngineModel()
    {
        v8EngineAssembled.SetActive(true);
        DisassembleButton.SetActive(true);
    }
    public void hideEngineModel()
    {
        v8EngineAssembled.SetActive(false);
        DisassembleButton.SetActive(false);
        v8EngineDisassembled.SetActive(false);
        instructionPanel.SetActive(false);
    }

    public void displayAssemblyInstructions()
    {
        hideEngineModel();
        v8EngineDisassembled.SetActive(true);
        instructionPanel.SetActive(true);
        DisassembleButton.SetActive(false);

    }
}
