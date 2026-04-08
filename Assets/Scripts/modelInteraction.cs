using UnityEngine;

public class ModelInteraction : MonoBehaviour
{
    private Vector2 lastTouchPosition;
    private float rotationSpeed = 0.3f;
    private float zoomSpeed = 0.02f;
    private float panSpeed = 0.005f;
    private float minScale = 0.5f;
    private float maxScale = 2f;
    private bool isRotating = false;
    private float initialScale;

    void Start()
    {
        initialScale = transform.localScale.x; // Store original scale
    }

    void Update()
    {
        HandleTouchInput();
    }

    void HandleTouchInput()
    {
        if (Input.touchCount == 1) // One-finger touch for rotation
        {
            Touch touch = Input.GetTouch(0);
            if (touch.phase == TouchPhase.Began)
            {
                lastTouchPosition = touch.position;
                isRotating = true;
            }
            else if (touch.phase == TouchPhase.Moved && isRotating)
            {
                Vector2 delta = touch.position - lastTouchPosition;

                if (Mathf.Abs(delta.x) > Mathf.Abs(delta.y)) // Horizontal swipe → Rotate Y-axis
                {
                    transform.Rotate(Vector3.up, -delta.x * rotationSpeed, Space.World);
                }
                else // Vertical swipe → Rotate X-axis
                {
                    transform.Rotate(Vector3.right, delta.y * rotationSpeed, Space.World);
                }

                lastTouchPosition = touch.position;
            }
            else if (touch.phase == TouchPhase.Ended)
            {
                isRotating = false;
            }
        }
        else if (Input.touchCount == 2) // Two-finger pinch for zooming
        {
            Touch touch1 = Input.GetTouch(0);
            Touch touch2 = Input.GetTouch(1);

            if (touch1.phase == TouchPhase.Began || touch2.phase == TouchPhase.Began)
            {
                return; // Ignore zooming when fingers first touch
            }

            if (touch1.phase == TouchPhase.Moved || touch2.phase == TouchPhase.Moved)
            {
                float prevDistance = (touch1.position - touch1.deltaPosition - (touch2.position - touch2.deltaPosition)).magnitude;
                float currDistance = (touch1.position - touch2.position).magnitude;
                float zoomFactor = (currDistance - prevDistance) * zoomSpeed;

                transform.localScale += Vector3.one * zoomFactor;

                // Ensure zoom stays within min & max bounds
                float newScale = Mathf.Clamp(transform.localScale.x, minScale * initialScale, maxScale * initialScale);
                transform.localScale = new Vector3(newScale, newScale, newScale);
            }
        }
        else if (Input.touchCount == 3) // Three-finger drag for panning
        {
            Touch touch1 = Input.GetTouch(0);
            Touch touch2 = Input.GetTouch(1);
            Touch touch3 = Input.GetTouch(2);

            if (touch1.phase == TouchPhase.Moved && touch2.phase == TouchPhase.Moved && touch3.phase == TouchPhase.Moved)
            {
                Vector2 delta = (touch1.deltaPosition + touch2.deltaPosition + touch3.deltaPosition) / 3;
                transform.Translate(-delta.x * panSpeed, -delta.y * panSpeed, 0, Space.World);
            }
        }
    }
}
