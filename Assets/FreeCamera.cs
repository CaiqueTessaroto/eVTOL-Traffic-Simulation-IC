using UnityEngine;

/// <summary>
/// Simple free-fly camera. Attach directly to the Camera.
/// - W / S: move forward / backward
/// - A / D: strafe left / right
/// - Q / E: move down / up
/// - Arrow keys: look (Up/Down = pitch, Left/Right = yaw)
/// No mouse required.
/// </summary>
public class FreeCamera : MonoBehaviour
{
    [Header("Movement")]
    public float moveSpeed = 20f;
    [Tooltip("Hold to move faster")]
    public KeyCode fastKey = KeyCode.LeftShift;
    public float fastMultiplier = 3f;

    [Header("Rotation")]
    public float rotationSpeed = 60f; // degrees per second

    private float _yaw;
    private float _pitch;

    private void Start()
    {
        Vector3 angles = transform.eulerAngles;
        _yaw = angles.y;
        _pitch = angles.x;
    }

    private void Update()
    {
        HandleRotation();
        HandleMovement();
    }

    private void HandleRotation()
    {
        float rotAmount = rotationSpeed * Time.deltaTime;

        if (Input.GetKey(KeyCode.UpArrow)) _pitch -= rotAmount;
        if (Input.GetKey(KeyCode.DownArrow)) _pitch += rotAmount;
        if (Input.GetKey(KeyCode.LeftArrow)) _yaw -= rotAmount;
        if (Input.GetKey(KeyCode.RightArrow)) _yaw += rotAmount;

        _pitch = Mathf.Clamp(_pitch, -89f, 89f);
        transform.eulerAngles = new Vector3(_pitch, _yaw, 0f);
    }

    private void HandleMovement()
    {
        float speed = moveSpeed * (Input.GetKey(fastKey) ? fastMultiplier : 1f);

        Vector3 move = Vector3.zero;
        if (Input.GetKey(KeyCode.W)) move += transform.forward;
        if (Input.GetKey(KeyCode.S)) move -= transform.forward;
        if (Input.GetKey(KeyCode.D)) move += transform.right;
        if (Input.GetKey(KeyCode.A)) move -= transform.right;
        if (Input.GetKey(KeyCode.E)) move += transform.up;
        if (Input.GetKey(KeyCode.Q)) move -= transform.up;

        transform.position += move * speed * Time.deltaTime;
    }
}