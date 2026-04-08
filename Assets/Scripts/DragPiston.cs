using System.Collections;
using System.Collections.Generic;
using UnityEngine;

public class DragPiston : MonoBehaviour
{
    private Vector3 screenPoint;
    private Vector3 offset;
    private bool dragging = false;

    void Update()
    {
        // Check for mouse input
        if (Input.GetMouseButtonDown(0))
        {
            RaycastHit hit;
            Ray ray = Camera.main.ScreenPointToRay(Input.mousePosition);

            if (Physics.Raycast(ray, out hit) && hit.collider.gameObject == gameObject)
            {
                StartDrag();
            }
        }

        if (dragging)
        {
            if (Input.GetMouseButton(0))
            {
                DragObject(Input.mousePosition);
            }
            else if (Input.GetMouseButtonUp(0))
            {
                StopDrag();
            }
        }

        // Check for touch input if available
        if (Input.touchCount > 0)
        {
            Touch touch = Input.GetTouch(0);

            if (touch.phase == TouchPhase.Began)
            {
                RaycastHit hit;
                Ray ray = Camera.main.ScreenPointToRay(touch.position);

                if (Physics.Raycast(ray, out hit) && hit.collider.gameObject == gameObject)
                {
                    StartDrag();
                }
            }
            else if (dragging && touch.phase == TouchPhase.Moved)
            {
                DragObject(touch.position);
            }
            else if (dragging && (touch.phase == TouchPhase.Ended || touch.phase == TouchPhase.Canceled))
            {
                StopDrag();
            }
        }
    }

    private void StartDrag()
    {
        screenPoint = Camera.main.WorldToScreenPoint(gameObject.transform.position);
        offset = gameObject.transform.position - Camera.main.ScreenToWorldPoint(new Vector3(Input.mousePosition.x, Input.mousePosition.y, screenPoint.z));
        dragging = true;
    }

    private void DragObject(Vector3 newPosition)
    {
        Vector3 cursorPoint = new Vector3(newPosition.x, newPosition.y, screenPoint.z);
        Vector3 cursorPosition = Camera.main.ScreenToWorldPoint(cursorPoint) + offset;
        transform.position = cursorPosition;
    }

    private void StopDrag()
    {
        dragging = false;
    }
}

