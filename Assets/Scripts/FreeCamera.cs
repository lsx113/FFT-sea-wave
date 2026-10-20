using UnityEngine;
#if ENABLE_INPUT_SYSTEM
using UnityEngine.InputSystem;
#endif

/// <summary>
/// 自由飞行相机：像 Scene 视图那样在场景里到处飞，方便近距离看浪和泡沫。
///
/// 操作：
///   按住鼠标右键拖动  转视角（按住时鼠标会被锁在画面中央，松开就放出来）
///   W / A / S / D      前进 / 左移 / 后退 / 右移（沿视线方向，抬头就一直往上飞）
///   E / Q              上升 / 下降
///   Space / Ctrl       也是上升 / 下降
///   Shift              加速（按住）
///   滚轮               调基础速度
///
/// 为什么用「按住右键才转视角」：Game 视图里鼠标不锁定的话光标会跑出画面，
/// 不按住就能转视角会非常难用；按住才锁、松开就放，和 Scene 视图的手感一致。
///
/// 注意：海面的 clipmap 网格是跟着 <see cref="OceanGeometry"/> 的 viewer（默认主相机）
/// 重新摆位的，所以相机飞到哪里，近处那层细网格就跟到哪里，不会出现「飞出去就没细节」。
/// </summary>
[DisallowMultipleComponent]
public class FreeCamera : MonoBehaviour
{
    [Header("速度")]
    [Tooltip("基础飞行速度（米/秒）。海面跨度几公里，太小会飞得很难受")]
    [SerializeField] float baseSpeed = 12f;

    [Tooltip("按住 Shift 时的倍数")]
    [SerializeField] float boostMultiplier = 5f;

    [Tooltip("滚轮每格的调速比例")]
    [SerializeField] float scrollSpeedStep = 0.15f;

    [Tooltip("速度上下限（米/秒）")]
    [SerializeField] Vector2 speedLimits = new Vector2(1f, 400f);

    [Tooltip("加减速的平滑程度，越大越跟手")]
    [SerializeField] float responsiveness = 8f;

    [Header("视角")]
    [Tooltip("鼠标灵敏度（度/像素）")]
    [SerializeField] float lookSensitivity = 0.12f;

    [Tooltip("抬头/低头的角度上限")]
    [SerializeField] float pitchLimit = 88f;

    [Header("限制")]
    [Tooltip("不要钻到水面以下")]
    [SerializeField] bool keepAboveWater = true;

    [Tooltip("相机离水面最低高度（米）")]
    [SerializeField] float minHeightAboveWater = 0.8f;

    [Header("调试")]
    [Tooltip("在左上角显示速度 / 坐标 / 操作提示")]
    [SerializeField] bool showHud = true;

    Vector3 velocity;
    float pitch;

    void Start()
    {
        // 初始俯仰以场景里摆好的角度为准，避免一进 Play 视角就跳平
        pitch = NormalizePitch(transform.eulerAngles.x);
    }

    void Update()
    {
#if ENABLE_INPUT_SYSTEM
        if (Keyboard.current == null || Mouse.current == null)
            return;

        HandleSpeed();
        HandleLook();
        HandleMove();
#endif
    }

    void HandleSpeed()
    {
        // 滚轮一格在不同设备上是 120 或 1，统一折算成「格数」再乘比例
        float notches = Mouse.current.scroll.ReadValue().y / 120f;
        if (Mathf.Abs(notches) > 0.001f)
        {
            baseSpeed = Mathf.Clamp(baseSpeed * (1f + notches * scrollSpeedStep),
                                    speedLimits.x, speedLimits.y);
        }
    }

    void HandleLook()
    {
        bool looking = Mouse.current.rightButton.isPressed;

        // 按住右键时把光标锁在中央，能无限转；松开还回去
        Cursor.lockState = looking ? CursorLockMode.Locked : CursorLockMode.None;
        Cursor.visible = !looking;

        if (!looking)
            return;

        Vector2 delta = Mouse.current.delta.ReadValue() * lookSensitivity;

        // 左右转直接绕世界 Y 轴；上下转只改变自己的俯仰，不滚动
        transform.Rotate(Vector3.up, delta.x, Space.World);
        pitch = Mathf.Clamp(pitch - delta.y, -pitchLimit, pitchLimit);
        transform.rotation = Quaternion.Euler(pitch, transform.eulerAngles.y, 0f);
    }

    void HandleMove()
    {
        Vector2 move = Vector2.zero;
        if (Keyboard.current.wKey.isPressed || Keyboard.current.upArrowKey.isPressed) move.y += 1f;
        if (Keyboard.current.sKey.isPressed || Keyboard.current.downArrowKey.isPressed) move.y -= 1f;
        if (Keyboard.current.dKey.isPressed || Keyboard.current.rightArrowKey.isPressed) move.x += 1f;
        if (Keyboard.current.aKey.isPressed || Keyboard.current.leftArrowKey.isPressed) move.x -= 1f;

        float vertical = 0f;
        if (Keyboard.current.eKey.isPressed || Keyboard.current.spaceKey.isPressed) vertical += 1f;
        if (Keyboard.current.qKey.isPressed || Keyboard.current.leftCtrlKey.isPressed) vertical -= 1f;

        bool boost = Keyboard.current.leftShiftKey.isPressed || Keyboard.current.rightShiftKey.isPressed;
        float speed = baseSpeed * (boost ? boostMultiplier : 1f);

        // 水平移动跟着视线方向（抬头前进就是往上飞，和大多数飞行相机一致）
        Vector3 direction = transform.forward * move.y + transform.right * move.x + Vector3.up * vertical;
        if (direction.sqrMagnitude > 1f)
            direction.Normalize();

        Vector3 targetVelocity = direction * speed;
        velocity = Vector3.Lerp(velocity, targetVelocity, 1f - Mathf.Exp(-responsiveness * Time.deltaTime));

        Vector3 position = transform.position + velocity * Time.deltaTime;

        if (keepAboveWater && position.y < minHeightAboveWater)
        {
            position.y = minHeightAboveWater;
            velocity.y = Mathf.Max(velocity.y, 0f);
        }

        transform.position = position;
    }

    /// <summary>把 0~360 的欧拉角折成 -180~180，免得从 359° 转到 1° 时视角翻跟头。</summary>
    static float NormalizePitch(float angle)
    {
        return angle > 180f ? angle - 360f : angle;
    }

    void OnGUI()
    {
        if (!showHud)
            return;

        Vector3 p = transform.position;
        GUI.color = new Color(1f, 1f, 1f, 0.85f);
        GUI.Label(new Rect(12f, 10f, 520f, 20f),
            string.Format("速度 {0:F1} m/s    位置 ({1:F1}, {2:F1}, {3:F1})", baseSpeed, p.x, p.y, p.z));
        GUI.Label(new Rect(12f, 30f, 620f, 20f),
            "右键拖动转视角 · WASD 移动 · E/Q 升降 · Shift 加速 · 滚轮调速");
    }

    void OnDisable()
    {
        // 脚本被关掉时别把光标一直锁着
        Cursor.lockState = CursorLockMode.None;
        Cursor.visible = true;
    }
}
