using UnityEngine;
#if ENABLE_INPUT_SYSTEM
using UnityEngine.InputSystem;
#endif

/// <summary>
/// 自由飞行相机：按住鼠标右键转视角，WASD 前后左右、Q/E 升降、Shift 加速、滚轮调速。
/// 手感对齐 Unity Scene 视图。挂到 Main Camera 上，Play 之后就能用。
///
/// 为什么不用 Input.GetKey：本工程的 Active Input Handling 设成了新输入系统
/// （ProjectSettings 里 activeInputHandler = 1），老的 Input 类一读就会抛
/// InvalidOperationException，所以这里直接走 Keyboard/Mouse.current。
/// </summary>
[DisallowMultipleComponent]
public class FreeFlyCamera : MonoBehaviour
{
    [Header("移动")]
    [Tooltip("基础速度（米/秒）")]
    public float moveSpeed = 15f;

    [Tooltip("按住 Shift 时的速度倍率")]
    public float sprintMultiplier = 4f;

    [Tooltip("滚轮每格的调速倍率（指数缩放，慢速和快速都好调）")]
    public float scrollSpeedFactor = 1.15f;

    [Tooltip("速度上限，防止一路滚下去飞出太阳系")]
    public float maxSpeed = 2000f;

    [Tooltip("起步/刹车的平滑时间（秒），0 = 瞬间启停")]
    public float smoothTime = 0.08f;

    [Header("视角")]
    [Tooltip("鼠标灵敏度（度/像素）")]
    public float lookSensitivity = 0.12f;

    [Tooltip("俯仰角上限，防止翻过 90 度导致画面上下颠倒")]
    public float pitchLimit = 89f;

    [Tooltip("转视角时锁定鼠标，松开右键再放出来")]
    public bool lockCursorWhileLooking = true;

    // 自己维护 yaw/pitch 而不是读 transform.eulerAngles：
    // 后者会被 Clamp 到 [0,360)，连续转头时会出现跳变。
    private float yaw;
    private float pitch;
    private float speed;
    private Vector3 velocity;
    private Vector3 velocityRef;
    private bool looking;

    private void OnEnable()
    {
        Vector3 euler = transform.eulerAngles;
        yaw = euler.y;
        pitch = euler.x > 180f ? euler.x - 360f : euler.x;
        speed = Mathf.Clamp(moveSpeed, 0.1f, maxSpeed);
    }

    private void OnDisable()
    {
        // 退出播放模式时把光标还回去，否则编辑器里鼠标是锁住的
        looking = false;
        SetCursorLocked(false);
    }

    private void Update()
    {
        Mouse mouse = Mouse.current;
        Keyboard keyboard = Keyboard.current;
        if (mouse == null || keyboard == null)
            return; // 没有输入设备时直接跳过，别让批处理渲染抛空引用

        HandleLook(mouse);
        HandleSpeed(mouse);
        HandleMove(keyboard);
    }

    private void HandleLook(Mouse mouse)
    {
        bool wantLook = mouse.rightButton.isPressed;
        if (wantLook != looking)
        {
            looking = wantLook;
            SetCursorLocked(looking && lockCursorWhileLooking);
        }

        if (!looking)
            return;

        Vector2 delta = mouse.delta.ReadValue();
        yaw += delta.x * lookSensitivity;
        pitch -= delta.y * lookSensitivity;
        pitch = Mathf.Clamp(pitch, -pitchLimit, pitchLimit);

        // 只给俯仰和偏航，不带 roll：翻滚视角在飞行相机里只会让人分不清地平线
        transform.rotation = Quaternion.Euler(pitch, yaw, 0f);
    }

    private void HandleSpeed(Mouse mouse)
    {
        float scroll = mouse.scroll.ReadValue().y;
        if (Mathf.Abs(scroll) < 0.01f)
            return;

        // Windows 上一格滚轮是 ±120，先归一化，再按倍率做指数缩放
        float notches = Mathf.Clamp(scroll / 120f, -1f, 1f);
        speed = Mathf.Clamp(speed * Mathf.Pow(scrollSpeedFactor, notches), 0.5f, maxSpeed);
    }

    private void HandleMove(Keyboard keyboard)
    {
        Vector3 input = Vector3.zero;
        if (keyboard.wKey.isPressed) input += Vector3.forward;
        if (keyboard.sKey.isPressed) input += Vector3.back;
        if (keyboard.dKey.isPressed) input += Vector3.right;
        if (keyboard.aKey.isPressed) input += Vector3.left;
        if (keyboard.eKey.isPressed) input += Vector3.up;
        if (keyboard.qKey.isPressed) input += Vector3.down;

        // 斜向不能比直行快，先归一化
        if (input.sqrMagnitude > 1f)
            input.Normalize();

        float currentSpeed = speed *
            (keyboard.leftShiftKey.isPressed || keyboard.rightShiftKey.isPressed ? sprintMultiplier : 1f);
        Vector3 targetVelocity = transform.rotation * input * currentSpeed;

        // 平滑一下，硬启停在看海面这种连续画面上会有顿挫感
        velocity = smoothTime > 0f
            ? Vector3.SmoothDamp(velocity, targetVelocity, ref velocityRef, smoothTime)
            : targetVelocity;

        transform.position += velocity * Time.deltaTime;
    }

    private static void SetCursorLocked(bool locked)
    {
        Cursor.lockState = locked ? CursorLockMode.Locked : CursorLockMode.None;
        Cursor.visible = !locked;
    }
}
