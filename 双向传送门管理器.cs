using UdonSharp;
using UnityEngine;
using VRC.SDKBase;
using VRC.SDK3.Rendering;

[UdonBehaviourSyncMode(BehaviourSyncMode.None)]
public class 双向传送门管理器 : UdonSharpBehaviour
{
    // ============================================================
    // 基础配置
    // ============================================================

    [Header("════════════ 基础设置 ════════════")]
    public float cameraNearClip = 0.01f;

    [Header("════════════ 传送门 A ════════════")]
    public Transform portalParentA;
    public Transform portalPlaneA;
    public Camera cameraA;
    [Header("★ 请手动把传送门A的材质球拖到这里!")]
    public Material portalMatA;

    [Header("════════════ 传送门 B ════════════")]
    public Transform portalParentB;
    public Transform portalPlaneB;
    public Camera cameraB;
    [Header("★ 请手动把传送门B的材质球拖到这里!")]
    public Material portalMatB;

    [Header("════════════ 传送门厚度 ════════════")]
    public float noClipDepth = 0.3f;
    public float portalTriggerWidth = 2f;
    public float portalTriggerHeight = 2f;
    public float clipPlaneOffset = 0.01f;

    [Header("════════════ 传送门形状 ════════════")]
    [Tooltip("开启后用圆形/椭圆判定，关闭则用矩形。圆形门强烈建议开启")]
    public bool useCircularPortalCheck = true;

    [Header("════════════ 碰撞控制 ════════════")]
    public 传送枪 portalGun;
    public float colliderDisableBuffer = 0.15f;

    [Header("════════════ 性能优化 ════════════")]
    public bool enableVisibilityOptimization = true;
    public float maxRenderDistance = 50f;
    public float maxViewAngle = 100f;
    public int checkInterval = 2;

    [Header("════════════ 调试 ════════════")]
    public bool showDebugGizmos = true;
    public Color gizmoColorA = new Color(1f, 0.4f, 0.1f, 0.5f);
    public Color gizmoColorB = new Color(0.1f, 0.6f, 1f, 0.5f);

    [Header("════════════ 自动适配配置 ════════════")]
    [Tooltip("VR模式下强制使用的FOV (建议110)")]
    public float vrTargetFOV = 110f;

    // ============================================================
    // 新增：过渡系统（极简）
    // ============================================================

    [Header("════════════ 过渡系统（新版） ════════════")]
    [Tooltip("过渡 Cube。子集包含过渡相机。传送时显示并控制旋转，过渡完成后关闭。")]
    public GameObject portalViewTransitionCube;
    [Tooltip("过渡时长（秒）。")]
    public float transitionDuration = 0.5f;

    [Header("════════════ 同Collider专修/调试 ════════════")]
    [Tooltip("开启基础传送日志")]
    public bool debugTeleportLog = true;
    [Tooltip("开启详细状态日志，会按间隔输出")]
    public bool debugTeleportVerbose = false;
    [Tooltip("详细日志间隔帧数")]
    public int debugLogIntervalFrames = 15;

    [Tooltip("VRC 玩家默认胶囊半径，默认 0.2")]
    [HideInInspector]
    public float playerCapsuleRadius = 0.2f;

    [Tooltip("VRC 玩家默认胶囊高度，仅用于调试参考，默认 1.6")]
    [HideInInspector]
    public float playerCapsuleHeight = 1.6f;

    [Tooltip("判断玩家在传送门哪一侧时的死区，防止接近 0 时抖动")]
    [HideInInspector]
    public float portalSideEpsilon = 0.02f;

    [Tooltip("传送后屏蔽几帧传送检测，防止同帧/连续帧误触发")]
    [HideInInspector]
    public int teleportBlockFrames = 0;

    [Tooltip("传送发生后直接结束本帧 LateUpdate，防止另一个门用旧坐标污染状态")]
    public bool stopAfterTeleportSameFrame = true;

    [Tooltip("如果 A/B 开在同一个 Collider 上，防止两个门互相抢开关")]
    public bool protectSharedMarkedCollider = true;

    // ============================================================
    // 固定内部值（隐藏）
    // ============================================================

    [Header("════════════ Seb传送原理 / 玩家保持站立 ════════════")]
    [Tooltip("使用 Sebastian Lague 风格：追踪上一帧/当前帧是否跨过传送门平面")]
    [HideInInspector]
    public bool useSebastianCrossing = true;

    [Tooltip("玩家头部进入传送门前后多深范围内才开始追踪，建议 0.6~1.2")]
    public float travellerTrackDepth = 0.8f;

    [Tooltip("跨越平面判断死区，防止 z 接近 0 抖动")]
    public float crossingEpsilon = 0.005f;

    [Tooltip("传送触发只看头部穿越，脚只用于碰撞体开关。地板/天花板更稳定")]
    [HideInInspector]
    public bool useHeadAsTraveller = true;

    [Tooltip("用 Head 算出新 Head，再用 AvatarRoot/Origin 偏移算 TeleportTo 位置，适合 VRChat")]
    [HideInInspector]
    public bool useVRCTrackingRootTeleport = true;

    [Tooltip("传送空间变换忽略 Transform 缩放，避免门/父物体 scale 影响传送位置")]
    [HideInInspector]
    public bool useScaleFreePortalMatrix = true;

    [Tooltip("经典 Portal 半转。默认关闭，因为你的传送门父物体朝向可能已经手动处理过")]
    [HideInInspector]
    public bool useClassicHalfTurn = false;

    [Tooltip("传送后玩家始终保持站立，只改变水平转身 yaw，不尝试控制抬头/歪头")]
    [HideInInspector]
    public bool keepPlayerUpright = true;

    [Tooltip("地板/天花板门动量吸附，让无限下落更稳定")]
    [HideInInspector]
    public bool enableFlatPortalMomentumSnapping = true;

    [HideInInspector]
    public float flatPortalDotThreshold = 0.9925f;

    [HideInInspector]
    public float verticalVelocitySnapThreshold = 0.9925f;

    // ============================================================
    // 状态显示（Inspector 调试用）
    // ============================================================

    [Header("════════════ 状态显示 ════════════")]
    public bool isVRPlayer = false;
    public float currentFOV = 60f;
    public bool isCameraARendering = true;
    public bool isCameraBRendering = true;
    public bool isClippingActiveA = true;
    public bool isClippingActiveB = true;
    public string playerNearestPortal = "";
    public int portalStateA = 0;
    public int portalStateB = 0;
    public bool colliderADisabled = false;
    public bool colliderBDisabled = false;

    // ============================================================
    // 私有变量
    // ============================================================

    private VRCPlayerApi localPlayer;
    private int frameCounter = 0;
    private Renderer rendererA;
    private Renderer rendererB;

    private int lastBodySideA = 0;
    private int lastBodySideB = 0;

    private int teleportBlockedUntilFrame = -1;
    private int teleportSeq = 0;
    private bool warnedSharedCollider = false;

    // Sebastian Lague 风格 traveller tracking
    private bool trackingHeadA = false;
    private bool trackingHeadB = false;
    private Vector3 previousHeadLocalA = Vector3.zero;
    private Vector3 previousHeadLocalB = Vector3.zero;

    // ============================================================
    // 过渡系统变量
    // ============================================================

    private float transitionStartTime = 0f;
    private Quaternion fromPortalRotAtTeleport;
    private Quaternion toPortalRotAtTeleport;
    private bool isTeleporting = false;

    // PATCH: 速度延迟重发，防止 VRChat 接地吃速度
    private Vector3 pendingVelocity = Vector3.zero;
    private int pendingVelocityFrames = 0;

    // ============================================================
    // Start
    // ============================================================

    void Start()
    {
        localPlayer = Networking.LocalPlayer;

        if (cameraA != null) cameraA.nearClipPlane = cameraNearClip;
        if (cameraB != null) cameraB.nearClipPlane = cameraNearClip;

        if (portalPlaneA != null)
        {
            rendererA = portalPlaneA.GetComponent<Renderer>();
            if (rendererA != null) portalMatA = rendererA.material;
        }

        if (portalPlaneB != null)
        {
            rendererB = portalPlaneB.GetComponent<Renderer>();
            if (rendererB != null) portalMatB = rendererB.material;
        }

        if (localPlayer != null)
        {
            isVRPlayer = localPlayer.IsUserInVR();
        }

        // 默认关闭过渡 Cube（只在传送时开启）
        if (portalViewTransitionCube != null)
        {
            portalViewTransitionCube.SetActive(false);

            // 关闭子集相机，直到传送时才开启
            Camera[] childCameras = portalViewTransitionCube.GetComponentsInChildren<Camera>(true);
            foreach (var cam in childCameras)
            {
                if (cam.gameObject != portalViewTransitionCube)
                {
                    cam.enabled = false;
                }
            }
        }

        TPLog("Start. isVRPlayer=" + isVRPlayer + " capsuleRadius=" + playerCapsuleRadius + " capsuleHeight=" + playerCapsuleHeight);
    }

    // ============================================================
    // 主循环
    // ============================================================

    private void LateUpdate()
    {
        if (localPlayer == null || !localPlayer.IsValid()) return;
        if (portalParentA == null || portalParentB == null) return;
        if (portalPlaneA == null || portalPlaneB == null) return;

        // PATCH: 延迟速度重发，防止 VRChat 接地吃速度
        if (pendingVelocityFrames > 0 && localPlayer != null && localPlayer.IsValid())
        {
            localPlayer.SetVelocity(pendingVelocity);
            pendingVelocityFrames--;
        }

        float syncFOV;
        if (isVRPlayer)
        {
            syncFOV = vrTargetFOV;
        }
        else
        {
            // Desktop 模式：使用当前 FOV 值（无法在 Udon 中访问 Camera.main）
            // 如果需要精确值，可以在 Inspector 中手动设置 currentFOV
            syncFOV = currentFOV > 0 ? currentFOV : 60f;
        }

        VRCPlayerApi.TrackingData headData = localPlayer.GetTrackingData(VRCPlayerApi.TrackingDataType.Head);
        Vector3 playerHead = headData.position;
        Quaternion playerWorldRot = headData.rotation;
        Vector3 playerForward = playerWorldRot * Vector3.forward;
        Vector3 playerFeet = localPlayer.GetPosition();
        Vector3 playerCenter = (playerHead + playerFeet) * 0.5f;

        // ============================================================
        // 更新过渡 Cube 旋转
        // ============================================================

        UpdateTransition(headData, syncFOV);

        // ============================================================
        // 更新摄像机位置
        // ============================================================

        if (cameraB != null)
        {
            Vector3 playerLocalToA = portalParentA.InverseTransformPoint(playerHead);
            Quaternion playerLocalRotToA = Quaternion.Inverse(portalParentA.rotation) * playerWorldRot;
            cameraB.transform.position = portalParentB.TransformPoint(playerLocalToA);
            cameraB.transform.rotation = portalParentB.rotation * playerLocalRotToA;
        }

        if (cameraA != null)
        {
            Vector3 playerLocalToB = portalParentB.InverseTransformPoint(playerHead);
            Quaternion playerLocalRotToB = Quaternion.Inverse(portalParentB.rotation) * playerWorldRot;
            cameraA.transform.position = portalParentA.TransformPoint(playerLocalToB);
            cameraA.transform.rotation = portalParentA.rotation * playerLocalRotToB;
        }

        // ============================================================
        // 可见性优化
        // ============================================================

        frameCounter++;
        if (frameCounter >= checkInterval)
        {
            frameCounter = 0;
            bool forcePortalCameraRendering = isTeleporting;
            if (enableVisibilityOptimization && !forcePortalCameraRendering)
            {
                bool portalAVisible = IsPortalVisible(playerHead, playerForward, portalPlaneA, rendererA);
                bool portalBVisible = IsPortalVisible(playerHead, playerForward, portalPlaneB, rendererB);
                isCameraBRendering = portalAVisible;
                isCameraARendering = portalBVisible;
                if (cameraA != null) cameraA.enabled = isCameraARendering;
                if (cameraB != null) cameraB.enabled = isCameraBRendering;
            }
            else
            {
                isCameraARendering = true;
                isCameraBRendering = true;
                if (cameraA != null) cameraA.enabled = true;
                if (cameraB != null) cameraB.enabled = true;
            }
        }

        float distA = Vector3.Distance(playerCenter, portalPlaneA.position);
        float distB = Vector3.Distance(playerCenter, portalPlaneB.position);
        playerNearestPortal = distA < distB ? "Portal A" : "Portal B";

        // ============================================================
        // FOV 同步
        // ============================================================

        if (cameraA != null)
        {
            cameraA.fieldOfView = syncFOV;
            cameraA.nearClipPlane = cameraNearClip;
        }
        if (cameraB != null)
        {
            cameraB.fieldOfView = syncFOV;
            cameraB.nearClipPlane = cameraNearClip;
        }

        if (portalMatA != null) portalMatA.SetFloat("_FOV", syncFOV);
        if (portalMatB != null) portalMatB.SetFloat("_FOV", syncFOV);

        // ============================================================
        // 传送检测
        // ============================================================

        bool didTeleportThisFrame = false;

        if (Time.frameCount > teleportBlockedUntilFrame)
        {
            didTeleportThisFrame = ProcessPortalTeleport(
                portalPlaneA, portalPlaneB,
                portalParentA, portalParentB,
                playerHead, playerFeet,
                ref portalStateA, ref portalStateB,
                ref lastBodySideA,
                true
            );

            if (!didTeleportThisFrame)
            {
                didTeleportThisFrame = ProcessPortalTeleport(
                    portalPlaneB, portalPlaneA,
                    portalParentB, portalParentA,
                    playerHead, playerFeet,
                    ref portalStateB, ref portalStateA,
                    ref lastBodySideB,
                    false
                );
            }

            if (didTeleportThisFrame)
            {
                teleportBlockedUntilFrame = Time.frameCount + teleportBlockFrames;

                if (stopAfterTeleportSameFrame)
                {
                    return;
                }
            }
        }
        else
        {
            if (debugTeleportVerbose && Time.frameCount % debugLogIntervalFrames == 0)
            {
                TPLog("Teleport check blocked until frame " + teleportBlockedUntilFrame);
            }
        }

        // ============================================================
        // Oblique Clipping
        // ============================================================

        bool headInsidePortalA = IsHeadInsidePortalVisualVolume(portalPlaneA, playerHead);
        bool headInsidePortalB = IsHeadInsidePortalVisualVolume(portalPlaneB, playerHead);

        if (cameraA != null && cameraA.enabled)
        {
            if (headInsidePortalB)
            {
                isClippingActiveA = true;
                ApplyObliqueClipping(cameraA, portalPlaneA);
            }
            else
            {
                ProcessPortal(cameraA, portalPlaneA, ref isClippingActiveA);
            }
        }

        if (cameraB != null && cameraB.enabled)
        {
            if (headInsidePortalA)
            {
                isClippingActiveB = true;
                ApplyObliqueClipping(cameraB, portalPlaneB);
            }
            else
            {
                ProcessPortal(cameraB, portalPlaneB, ref isClippingActiveB);
            }
        }
    }

    // ============================================================
    // 过渡更新 - PATCHED
    // ============================================================

    void UpdateTransition(VRCPlayerApi.TrackingData headData)
    {
        // 兼容旧调用
        float syncFOV = isVRPlayer ? vrTargetFOV : (currentFOV > 0 ? currentFOV : 60f);
        UpdateTransition(headData, syncFOV);
    }

    void UpdateTransition(VRCPlayerApi.TrackingData headData, float syncFOV)
    {
        // PATCH 1: VR 玩家直接跳过过渡，省性能不晕
        if (isVRPlayer)
        {
            isTeleporting = false;
            if (portalViewTransitionCube != null && portalViewTransitionCube.activeSelf)
                portalViewTransitionCube.SetActive(false);
            return;
        }

        if (portalViewTransitionCube == null) return;

        if (!isTeleporting)
        {
            if (portalViewTransitionCube.activeSelf)
            {
                portalViewTransitionCube.SetActive(false);
            }
            return;
        }

        // 传送过渡中：确保 Cube 开启
        if (!portalViewTransitionCube.activeSelf)
        {
            portalViewTransitionCube.SetActive(true);
        }

        // 传送过渡中：计算补偿旋转（只补偿 pitch + roll，不补偿 yaw）
        float elapsed = Time.time - transitionStartTime;
        float t = Mathf.Clamp01(elapsed / transitionDuration);
        // SmoothStep 平滑
        t = t * t * (3f - 2f * t);

        // 玩家当前头朝向
        Quaternion currentHeadRot = headData.rotation;

        // 传送门旋转差
        Quaternion portalDelta = toPortalRotAtTeleport * Quaternion.Inverse(fromPortalRotAtTeleport);

        // 只提取 pitch + roll 部分（去掉 yaw）
        Quaternion pitchRollOnly = ExtractPitchRollOnly(portalDelta);

        // 玩家传送后"应该"看到的世界旋转（只补偿 pitch + roll）
        Quaternion wantedWorldRot = pitchRollOnly * currentHeadRot;

        // 从"补偿的世界旋转" lerp 到"玩家实际头朝向"
        Quaternion transitionRot = Quaternion.Slerp(wantedWorldRot, currentHeadRot, t);

        // 应用到 Cube
        portalViewTransitionCube.transform.rotation = transitionRot;
        portalViewTransitionCube.transform.position = headData.position;

        // PATCH 2: 过渡相机 FOV 同步玩家
        Camera[] childCameras = portalViewTransitionCube.GetComponentsInChildren<Camera>(true);
        foreach (var cam in childCameras)
        {
            if (cam != null)
            {
                cam.fieldOfView = syncFOV;
                cam.nearClipPlane = cameraNearClip;
            }
        }

        // 过渡完成
        if (t >= 1.0f)
        {
            isTeleporting = false;

            // 查找子集相机并关闭
            foreach (var cam in childCameras)
            {
                if (cam.gameObject != portalViewTransitionCube)
                {
                    cam.enabled = false;
                }
            }

            // 关闭 Cube
            portalViewTransitionCube.SetActive(false);

            TPLog("Transition complete. Cameras disabled. Cube hidden.");
        }
    }

    // ============================================================
    // 从旋转中提取 pitch + roll 部分（去掉 yaw）
    // VRChat 允许玩家传送后改变转身方向（yaw），所以不需要补偿 yaw
    // PATCH 3: 无欧拉角版本，避免万向锁抖动
    // ============================================================

    Quaternion ExtractPitchRollOnly(Quaternion fullRotation)
    {
        // 旧版 euler 版本会有抖动，已替换
        // 提取 yaw：把 forward 投影到 XZ 平面
        Vector3 fwd = fullRotation * Vector3.forward;
        fwd.y = 0f;
        if (fwd.sqrMagnitude < 0.0001f)
        {
            // 几乎垂直朝上/下，yaw 无意义，直接返回原旋转
            return fullRotation;
        }
        fwd.Normalize();
        float yaw = Mathf.Atan2(fwd.x, fwd.z) * Mathf.Rad2Deg;
        Quaternion yawOnly = Quaternion.Euler(0f, yaw, 0f);
        // pitchRollOnly = fullRotation * inverse(yawOnly)
        Quaternion pitchRollOnly = fullRotation * Quaternion.Inverse(yawOnly);
        return pitchRollOnly;
    }

    // ============================================================
    // 传送时开启过渡 - PATCHED
    // ============================================================

    void BeginTransition(Transform fromPlane, Transform toPlane)
    {
        // PATCH 1: VR 玩家跳过过渡
        if (isVRPlayer)
        {
            isTeleporting = false;
            return;
        }

        // 记录传送时的状态
        fromPortalRotAtTeleport = fromPlane.rotation;
        toPortalRotAtTeleport = toPlane.rotation;

        // 开启过渡
        isTeleporting = true;
        transitionStartTime = Time.time;

        // 确保 Cube 开启，并激活子集相机
        if (portalViewTransitionCube != null)
        {
            portalViewTransitionCube.SetActive(true);

            // 激活所有子集相机
            Camera[] childCameras = portalViewTransitionCube.GetComponentsInChildren<Camera>(true);
            foreach (var cam in childCameras)
            {
                if (cam.gameObject != portalViewTransitionCube)
                {
                    cam.enabled = true;
                }
            }
        }

        TPLog("Transition begin. from=" + fromPlane.name + " to=" + toPlane.name);
    }

    // ============================================================
    // 工具方法
    // ============================================================

    void TPLog(string msg)
    {
        if (!debugTeleportLog) return;
        Debug.Log("[PortalTP] f=" + Time.frameCount + " " + msg);
    }

    int SideFromZ(float z)
    {
        if (z > portalSideEpsilon) return 1;
        if (z < -portalSideEpsilon) return -1;
        return 0;
    }

    bool IsPointInPortalRectExpanded(Vector3 localPoint, float extra)
    {
        return Mathf.Abs(localPoint.x) < portalTriggerWidth * 0.5f + extra &&
               Mathf.Abs(localPoint.y) < portalTriggerHeight * 0.5f + extra;
    }

    bool IsBodyInPortalXY(Transform portalPlane, Vector3 playerHead, Vector3 playerFeet)
    {
        Vector3 localHead = portalPlane.InverseTransformPoint(playerHead);
        Vector3 localFeet = portalPlane.InverseTransformPoint(playerFeet);

        bool headInXY = LocalPointInPortalRect(localHead);
        bool feetInXY = LocalPointInPortalRect(localFeet);

        return headInXY || feetInXY;
    }

    bool IsBodyInColliderZone(Transform portalPlane, Vector3 playerHead, Vector3 playerFeet)
    {
        Vector3 localHead = portalPlane.InverseTransformPoint(playerHead);
        Vector3 localFeet = portalPlane.InverseTransformPoint(playerFeet);

        float headZ = localHead.z;
        float feetZ = localFeet.z;

        float bodyMinZ = Mathf.Min(headZ, feetZ);
        float bodyMaxZ = Mathf.Max(headZ, feetZ);

        float speedBuffer = 0f;
        if (localPlayer != null && localPlayer.IsValid())
        {
            float v = localPlayer.GetVelocity().magnitude;
            speedBuffer = Mathf.Clamp(v * Time.deltaTime * 2.0f, 0f, 1.5f);
        }
        float colliderThreshold = noClipDepth + colliderDisableBuffer + speedBuffer;

        return IsBodyInPortalXY(portalPlane, playerHead, playerFeet) &&
               bodyMinZ < colliderThreshold &&
               bodyMaxZ > -colliderThreshold;
    }

    void SetColliderFlag(bool isPortalA, bool disabled)
    {
        if (isPortalA) colliderADisabled = disabled;
        else colliderBDisabled = disabled;
    }

    bool IsHeadInsidePortalVisualVolume(Transform portalPlane, Vector3 playerHead)
    {
        if (portalPlane == null) return false;
        Vector3 localHead = portalPlane.InverseTransformPoint(playerHead);
        bool inRect = LocalPointInPortalRect(localHead);
        bool inDepth = Mathf.Abs(localHead.z) < travellerTrackDepth;
        return inRect && inDepth;
    }

    Vector3 LocalPointForPortal(Transform portal, Vector3 worldPoint)
    {
        if (useScaleFreePortalMatrix)
        {
            Matrix4x4 worldToLocal = Matrix4x4.TRS(portal.position, portal.rotation, Vector3.one).inverse;
            return worldToLocal.MultiplyPoint(worldPoint);
        }
        return portal.InverseTransformPoint(worldPoint);
    }

    Vector3 WorldPointFromPortal(Transform portal, Vector3 localPoint)
    {
        if (useScaleFreePortalMatrix)
        {
            Matrix4x4 localToWorld = Matrix4x4.TRS(portal.position, portal.rotation, Vector3.one);
            return localToWorld.MultiplyPoint(localPoint);
        }
        return portal.TransformPoint(localPoint);
    }

    Vector3 LocalDirForPortal(Transform portal, Vector3 worldDir)
    {
        if (useScaleFreePortalMatrix)
        {
            Matrix4x4 worldToLocal = Matrix4x4.TRS(portal.position, portal.rotation, Vector3.one).inverse;
            return worldToLocal.MultiplyVector(worldDir);
        }
        return portal.InverseTransformDirection(worldDir);
    }

    Vector3 WorldDirFromPortal(Transform portal, Vector3 localDir)
    {
        if (useScaleFreePortalMatrix)
        {
            Matrix4x4 localToWorld = Matrix4x4.TRS(portal.position, portal.rotation, Vector3.one);
            return localToWorld.MultiplyVector(localDir);
        }
        return portal.TransformDirection(localDir);
    }

    int SideFromLocalZ(float z)
    {
        if (z > crossingEpsilon) return 1;
        if (z < -crossingEpsilon) return -1;
        return 0;
    }

    bool LocalPointInPortalRect(Vector3 localPoint)
    {
        if (!useCircularPortalCheck)
        {
            // 矩形判定 - 原版
            return Mathf.Abs(localPoint.x) < portalTriggerWidth * 0.5f &&
                   Mathf.Abs(localPoint.y) < portalTriggerHeight * 0.5f;
        }
        // 圆形/椭圆判定 - 无三角函数
        float rx = portalTriggerWidth * 0.5f;
        float ry = portalTriggerHeight * 0.5f;
        if (rx <= 0.0001f || ry <= 0.0001f) return false;
        float nx = localPoint.x / rx;
        float ny = localPoint.y / ry;
        return nx * nx + ny * ny <= 1f;
    }

    void SetHeadTracking(bool isPortalA, bool tracking, Vector3 previousLocal)
    {
        if (isPortalA)
        {
            trackingHeadA = tracking;
            previousHeadLocalA = previousLocal;
        }
        else
        {
            trackingHeadB = tracking;
            previousHeadLocalB = previousLocal;
        }
    }

    bool GetHeadTracking(bool isPortalA)
    {
        return isPortalA ? trackingHeadA : trackingHeadB;
    }

    Vector3 GetPreviousHeadLocal(bool isPortalA)
    {
        return isPortalA ? previousHeadLocalA : previousHeadLocalB;
    }

    Quaternion LocalHalfTurn()
    {
        return Quaternion.AngleAxis(180f, Vector3.up);
    }

    float DeltaYawBetweenPortals(Transform fromPortal, Transform toPortal)
    {
        Quaternion outputPortalRot = toPortal.rotation;
        if (useClassicHalfTurn)
        {
            outputPortalRot = Quaternion.AngleAxis(180f, toPortal.up) * toPortal.rotation;
        }

        float inputY = fromPortal.rotation.eulerAngles.y;
        float outputY = outputPortalRot.eulerAngles.y;
        return outputY - inputY;
    }

    Vector3 ApplyOptionalMomentumSnapping(Transform fromPortal, Transform toPortal, Vector3 playerVel, Vector3 localVel)
    {
        if (!enableFlatPortalMomentumSnapping) return localVel;
        if (playerVel.sqrMagnitude < 0.0001f) return localVel;

        bool fromFlat = Mathf.Abs(Vector3.Dot(fromPortal.forward, Vector3.up)) > flatPortalDotThreshold;
        if (!fromFlat) return localVel;

        float verticalAlignment = Mathf.Abs(Vector3.Dot(playerVel.normalized, Vector3.up));
        if (verticalAlignment > verticalVelocitySnapThreshold)
        {
            return Vector3.forward * Mathf.Abs(playerVel.y);
        }

        return localVel;
    }

    // ============================================================
    // 传送核心
    // ============================================================

    bool ProcessPortalTeleport(
        Transform portalPlane, Transform otherPortalPlane,
        Transform fromParent, Transform toParent,
        Vector3 playerHead, Vector3 playerFeet,
        ref int thisPortalState, ref int otherPortalState,
        ref int lastBodySide,
        bool isPortalA)
    {
        Vector3 localHeadForTrigger = portalPlane.InverseTransformPoint(playerHead);
        Vector3 localFeetForTrigger = portalPlane.InverseTransformPoint(playerFeet);

        float headZ = localHeadForTrigger.z;
        float feetZ = localFeetForTrigger.z;
        float bodyMinZ = Mathf.Min(headZ, feetZ);
        float bodyMaxZ = Mathf.Max(headZ, feetZ);

        bool headInXY = LocalPointInPortalRect(localHeadForTrigger);
        bool feetInXY = LocalPointInPortalRect(localFeetForTrigger);
        bool bodyInXY = headInXY || feetInXY;

        int currentBodySide;
        if (bodyMinZ > 0f) currentBodySide = 1;
        else if (bodyMaxZ < 0f) currentBodySide = -1;
        else currentBodySide = 0;

        float speedBuffer = 0f;
        if (localPlayer != null && localPlayer.IsValid())
        {
            float v = localPlayer.GetVelocity().magnitude;
            speedBuffer = Mathf.Clamp(v * Time.deltaTime * 2.0f, 0f, 1.5f);
        }
        float colliderThreshold = noClipDepth + colliderDisableBuffer + speedBuffer;
        bool bodyInColliderZone = bodyInXY && (bodyMinZ < colliderThreshold && bodyMaxZ > -colliderThreshold);
        bool otherBodyInColliderZone = false;
        if (otherPortalPlane != null)
        {
            otherBodyInColliderZone = IsBodyInColliderZone(otherPortalPlane, playerHead, playerFeet);
        }

        string portalName = isPortalA ? "A" : "B";
        string otherName = isPortalA ? "B" : "A";

        if (debugTeleportVerbose && Time.frameCount % debugLogIntervalFrames == 0)
        {
            if (bodyInXY || bodyInColliderZone || thisPortalState != 0 || GetHeadTracking(isPortalA))
            {
                TPLog(
                    "SebCheck " + portalName +
                    " state=" + thisPortalState +
                    " otherState=" + otherPortalState +
                    " tracking=" + GetHeadTracking(isPortalA) +
                    " headInXY=" + headInXY +
                    " feetInXY=" + feetInXY +
                    " colliderZone=" + bodyInColliderZone +
                    " otherColliderZone=" + otherBodyInColliderZone +
                    " headZ=" + headZ +
                    " feetZ=" + feetZ +
                    " currentBodySide=" + currentBodySide +
                    " lastSide=" + lastBodySide
                );
            }
        }

        // ============================================================
        // 同 Collider 保护
        // ============================================================

        if (portalGun != null)
        {
            Collider markedCollider = isPortalA ? portalGun.GetMarkedColliderA() : portalGun.GetMarkedColliderB();
            Collider otherMarkedCollider = isPortalA ? portalGun.GetMarkedColliderB() : portalGun.GetMarkedColliderA();

            bool sharedCollider = markedCollider != null && otherMarkedCollider != null && markedCollider == otherMarkedCollider;

            if (sharedCollider && protectSharedMarkedCollider && !warnedSharedCollider)
            {
                warnedSharedCollider = true;
                TPLog("Warning: Portal A and B are using the SAME collider. Shared collider protection enabled.");
            }

            if (markedCollider != null)
            {
                if (bodyInColliderZone)
                {
                    if (markedCollider.enabled)
                    {
                        markedCollider.enabled = false;

                        if (isPortalA) colliderADisabled = true;
                        else colliderBDisabled = true;

                        if (sharedCollider)
                        {
                            colliderADisabled = true;
                            colliderBDisabled = true;
                        }

                        TPLog("Disable collider by portal " + portalName + " shared=" + sharedCollider + " state=" + thisPortalState + " otherState=" + otherPortalState);
                    }
                }
                else
                {
                    if (!markedCollider.enabled && thisPortalState == 0)
                    {
                        bool canEnable = true;

                        if (protectSharedMarkedCollider && sharedCollider)
                        {
                            if (otherPortalState != 0 || otherBodyInColliderZone)
                            {
                                canEnable = false;
                            }
                        }

                        if (canEnable)
                        {
                            markedCollider.enabled = true;

                            if (isPortalA) colliderADisabled = false;
                            else colliderBDisabled = false;

                            if (sharedCollider)
                            {
                                colliderADisabled = false;
                                colliderBDisabled = false;
                            }

                            TPLog("Enable collider by portal " + portalName + " shared=" + sharedCollider + " state=" + thisPortalState + " otherState=" + otherPortalState);
                        }
                        else if (debugTeleportVerbose && Time.frameCount % debugLogIntervalFrames == 0)
                        {
                            TPLog("Keep shared collider disabled by portal " + portalName + " because portal " + otherName + " still needs it. otherState=" + otherPortalState + " otherZone=" + otherBodyInColliderZone);
                        }
                    }
                }
            }
        }

        // ============================================================
        // Portal crossing - 防隧穿扫掠版
        // 去掉了 travellerTrackDepth 开关，常开追踪 + 线段扫掠
        // ============================================================

        if (thisPortalState == 0)
        {
            Vector3 currentHeadLocal = LocalPointForPortal(portalPlane, playerHead);
            Vector3 currentFeetLocal = LocalPointForPortal(portalPlane, playerFeet);

            // 首次初始化：记录上一帧位置，不做传送判断
            if (!GetHeadTracking(isPortalA))
            {
                SetHeadTracking(isPortalA, true, currentHeadLocal);
                int startSide = SideFromLocalZ(currentHeadLocal.z);
                if (startSide != 0) lastBodySide = startSide;
                if (debugTeleportVerbose)
                {
                    TPLog("Start tracking head at portal " + portalName + " local=" + currentHeadLocal + " side=" + startSide);
                }
                return false;
            }

            Vector3 previousHeadLocal = GetPreviousHeadLocal(isPortalA);

            int oldSide = SideFromLocalZ(previousHeadLocal.z);
            int newSide = SideFromLocalZ(currentHeadLocal.z);

            bool crossedPlane = false;
            Vector3 crossingLocal = Vector3.zero;

            // 1. 经典侧面变号检测
            if (oldSide != 0 && newSide != 0 && oldSide != newSide)
            {
                float denom = previousHeadLocal.z - currentHeadLocal.z;
                if (Mathf.Abs(denom) > 0.0001f)
                {
                    float t = previousHeadLocal.z / denom;
                    crossingLocal = Vector3.Lerp(previousHeadLocal, currentHeadLocal, Mathf.Clamp01(t));
                    crossedPlane = true;
                }
            }
            else
            {
                // 2. 扫掠补救：线段与 z=0 平面求交，防止高速隧穿 / epsilon 死区漏检
                float dz = currentHeadLocal.z - previousHeadLocal.z;
                if (Mathf.Abs(dz) > 0.0001f)
                {
                    float t = -previousHeadLocal.z / dz;
                    if (t >= 0f && t <= 1f)
                    {
                        crossingLocal = Vector3.Lerp(previousHeadLocal, currentHeadLocal, t);
                        // 交点在门框内才算
                        if (LocalPointInPortalRect(crossingLocal))
                        {
                            crossedPlane = true;
                            oldSide = previousHeadLocal.z > 0f ? 1 : -1;
                            newSide = -oldSide;
                        }
                    }
                }
            }

            bool crossedInsidePortalRect = crossedPlane && LocalPointInPortalRect(crossingLocal);

            // 更新追踪位置 - 常开，不再用 travellerTrackDepth 关掉
            SetHeadTracking(isPortalA, true, currentHeadLocal);
            if (newSide != 0) lastBodySide = newSide;

            if (crossedInsidePortalRect)
            {
                teleportSeq++;
                TPLog(
                    "SebTeleport #" + teleportSeq + " " + portalName + " -> " + otherName +
                    " oldSide=" + oldSide +
                    " newSide=" + newSide +
                    " prevLocal=" + previousHeadLocal +
                    " currentLocal=" + currentHeadLocal +
                    " crossingLocal=" + crossingLocal
                );

                SetHeadTracking(isPortalA, false, currentHeadLocal);

                if (isPortalA)
                    TeleportToB(playerHead, playerFeet, portalPlaneA, portalPlaneB, portalParentA, portalParentB);
                else
                    TeleportToA(playerHead, playerFeet, portalPlaneB, portalPlaneA, portalParentB, portalParentA);

                return true;
            }
            else if (crossedPlane && debugTeleportVerbose)
            {
                TPLog("Plane crossed outside portal rect at " + portalName + " crossingLocal=" + crossingLocal);
            }
        }
        else if (thisPortalState == 2)
        {
            bool headOut = Mathf.Abs(headZ) > noClipDepth;
            bool feetOut = Mathf.Abs(feetZ) > noClipDepth;

            if (headOut && feetOut)
            {
                thisPortalState = 0;

                if (currentBodySide != 0)
                {
                    lastBodySide = currentBodySide;
                }
                else
                {
                    float centerZ = (headZ + feetZ) * 0.5f;
                    lastBodySide = centerZ > 0f ? 1 : -1;
                }

                SetHeadTracking(isPortalA, false, LocalPointForPortal(portalPlane, playerHead));

                TPLog("Portal " + portalName + " exit cooldown cleared. lastSide=" + lastBodySide + " headZ=" + headZ + " feetZ=" + feetZ);

                if (portalGun != null)
                {
                    Collider markedCollider = isPortalA ? portalGun.GetMarkedColliderA() : portalGun.GetMarkedColliderB();
                    Collider otherMarkedCollider = isPortalA ? portalGun.GetMarkedColliderB() : portalGun.GetMarkedColliderA();
                    bool sharedCollider = markedCollider != null && otherMarkedCollider != null && markedCollider == otherMarkedCollider;

                    bool canEnable = true;
                    if (protectSharedMarkedCollider && sharedCollider)
                    {
                        if (otherPortalState != 0 || otherBodyInColliderZone)
                        {
                            canEnable = false;
                        }
                    }

                    if (markedCollider != null && !markedCollider.enabled && canEnable)
                    {
                        markedCollider.enabled = true;

                        if (isPortalA) colliderADisabled = false;
                        else colliderBDisabled = false;

                        if (sharedCollider)
                        {
                            colliderADisabled = false;
                            colliderBDisabled = false;
                        }

                        TPLog("Re-enable collider after exiting portal " + portalName + " shared=" + sharedCollider);
                    }
                }
            }
        }

        return false;
    }

    // ============================================================
    // 传送执行（更新：包含过渡）
    // ============================================================

    void TeleportToB(Vector3 playerHead, Vector3 playerFeet, Transform fromPlane, Transform toPlane, Transform fromParent, Transform toParent)
    {
        TeleportSebStyle(fromPlane, toPlane, fromParent, toParent, true, playerHead, playerFeet);
    }

    void TeleportToA(Vector3 playerHead, Vector3 playerFeet, Transform fromPlane, Transform toPlane, Transform fromParent, Transform toParent)
    {
        TeleportSebStyle(fromPlane, toPlane, fromParent, toParent, false, playerHead, playerFeet);
    }

    void TeleportSebStyle(Transform fromPlane, Transform toPlane, Transform fromParent, Transform toParent, bool fromAtoB, Vector3 playerHead, Vector3 playerFeet)
    {
        VRCPlayerApi.TrackingData headData = localPlayer.GetTrackingData(VRCPlayerApi.TrackingDataType.Head);
        Quaternion playerHeadRot = headData.rotation;
        Vector3 playerVel = localPlayer.GetVelocity();

        // 1) Sebastian Lague 核心：world -> from portal local -> to portal world
        Vector3 localHeadPos = LocalPointForPortal(fromPlane, playerHead);
        Quaternion halfTurn = LocalHalfTurn();
        if (useClassicHalfTurn)
        {
            localHeadPos = halfTurn * localHeadPos;
        }
        Vector3 newHeadPos = WorldPointFromPortal(toPlane, localHeadPos);

        // 2) 玩家保持站立：只改 yaw，不控制 pitch/roll
        float diffY = DeltaYawBetweenPortals(fromPlane, toPlane);
        VRCPlayerApi.TrackingData rootData = isVRPlayer
            ? localPlayer.GetTrackingData(VRCPlayerApi.TrackingDataType.AvatarRoot)
            : localPlayer.GetTrackingData(VRCPlayerApi.TrackingDataType.Origin);

        Quaternion newPlayerRot;
        if (keepPlayerUpright)
        {
            float rootY = rootData.rotation.eulerAngles.y;
            newPlayerRot = Quaternion.Euler(0f, rootY + diffY, 0f);
        }
        else
        {
            Quaternion localRot = Quaternion.Inverse(fromPlane.rotation) * playerHeadRot;
            if (useClassicHalfTurn) localRot = halfTurn * localRot;
            newPlayerRot = toPlane.rotation * localRot;
        }

        // 3) VRChat 重点：TeleportTo 的点
        Vector3 newTeleportPos;
        if (useVRCTrackingRootTeleport)
        {
            Vector3 headToRoot = rootData.position - playerHead;
            if (keepPlayerUpright)
            {
                headToRoot = Quaternion.AngleAxis(diffY, Vector3.up) * headToRoot;
            }
            else
            {
                Quaternion fullDelta = toPlane.rotation * Quaternion.Inverse(fromPlane.rotation);
                if (useClassicHalfTurn) fullDelta = toPlane.rotation * halfTurn * Quaternion.Inverse(fromPlane.rotation);
                headToRoot = fullDelta * headToRoot;
            }
            newTeleportPos = newHeadPos + headToRoot;
        }
        else
        {
            float playerHeight = playerHead.y - playerFeet.y;
            newTeleportPos = newHeadPos - new Vector3(0f, playerHeight, 0f);
        }

        // PATCH: 出口微推 - 防止 VRChat 角色控制器落地吃速度
        // 动量完全保留，只是把出生点从地板里拔出来
        float exitNudge = 0.03f + Mathf.Clamp(playerVel.magnitude * Time.deltaTime * 0.5f, 0f, 0.12f);
        newTeleportPos += toPlane.forward * exitNudge;
        // 地板门额外抬一点点
        if (Vector3.Dot(toPlane.forward, Vector3.up) > 0.9f)
        {
            newTeleportPos += Vector3.up * 0.03f;
        }

        // 4) 速度
        Vector3 localVel = LocalDirForPortal(fromPlane, playerVel);
        localVel = ApplyOptionalMomentumSnapping(fromPlane, toPlane, playerVel, localVel);
        if (useClassicHalfTurn)
        {
            localVel = halfTurn * localVel;
        }
        Vector3 newVel = WorldDirFromPortal(toPlane, localVel);

        // ============================================================
        // 开启过渡（新增）
        // ============================================================

        BeginTransition(fromPlane, toPlane);

        // ============================================================
        // 执行传送
        // ============================================================

        localPlayer.TeleportTo(
            newTeleportPos,
            newPlayerRot,
            VRC.SDKBase.VRC_SceneDescriptor.SpawnOrientation.AlignPlayerWithSpawnPoint
        );
        localPlayer.SetVelocity(newVel);
        // PATCH: 延迟速度重发，防止 IsGrounded 吃速度
        pendingVelocity = newVel;
        pendingVelocityFrames = 2;

        // ============================================================
        // 后续处理
        // ============================================================

        if (fromAtoB)
        {
            portalStateA = 0;
            portalStateB = 2;

            Vector3 localToB_afterTeleport = LocalPointForPortal(portalPlaneB, newHeadPos);
            lastBodySideB = localToB_afterTeleport.z > 0f ? 1 : -1;
            SetHeadTracking(true, false, LocalPointForPortal(portalPlaneA, playerHead));
            SetHeadTracking(false, true, localToB_afterTeleport);

            if (portalGun != null)
            {
                Collider markedB = portalGun.GetMarkedColliderB();
                if (markedB != null && markedB.enabled)
                {
                    markedB.enabled = false;
                    colliderBDisabled = true;

                    Collider markedA = portalGun.GetMarkedColliderA();
                    if (markedA != null && markedA == markedB) colliderADisabled = true;
                }
            }

            TPLog("Seb After TeleportToB: teleportPos=" + newTeleportPos + " newHead=" + newHeadPos + " headLocalZToB=" + localToB_afterTeleport.z + " lastBodySideB=" + lastBodySideB + " velocity=" + newVel + " diffY=" + diffY);
        }
        else
        {
            portalStateB = 0;
            portalStateA = 2;

            Vector3 localToA_afterTeleport = LocalPointForPortal(portalPlaneA, newHeadPos);
            lastBodySideA = localToA_afterTeleport.z > 0f ? 1 : -1;
            SetHeadTracking(false, false, LocalPointForPortal(portalPlaneB, playerHead));
            SetHeadTracking(true, true, localToA_afterTeleport);

            if (portalGun != null)
            {
                Collider markedA = portalGun.GetMarkedColliderA();
                if (markedA != null && markedA.enabled)
                {
                    markedA.enabled = false;
                    colliderADisabled = true;

                    Collider markedB = portalGun.GetMarkedColliderB();
                    if (markedB != null && markedB == markedA) colliderBDisabled = true;
                }
            }

            TPLog("Seb After TeleportToA: teleportPos=" + newTeleportPos + " newHead=" + newHeadPos + " headLocalZToA=" + localToA_afterTeleport.z + " lastBodySideA=" + lastBodySideA + " velocity=" + newVel + " diffY=" + diffY);
        }

        UpdateCamerasNow(newHeadPos, newPlayerRot);
    }

    void UpdateCamerasNow(Vector3 newPlayerHeadPos, Quaternion newPlayerRot)
    {
        if (cameraA != null)
        {
            Vector3 localToB = portalParentB.InverseTransformPoint(newPlayerHeadPos);
            Quaternion localRotToB = Quaternion.Inverse(portalParentB.rotation) * newPlayerRot;
            cameraA.transform.position = portalParentA.TransformPoint(localToB);
            cameraA.transform.rotation = portalParentA.rotation * localRotToB;
        }

        if (cameraB != null)
        {
            Vector3 localToA = portalParentA.InverseTransformPoint(newPlayerHeadPos);
            Quaternion localRotToA = Quaternion.Inverse(portalParentA.rotation) * newPlayerRot;
            cameraB.transform.position = portalParentB.TransformPoint(localToA);
            cameraB.transform.rotation = portalParentB.rotation * localRotToA;
        }
    }

    bool IsPortalVisible(Vector3 playerPos, Vector3 playerForward, Transform portal, Renderer portalRenderer)
    {
        if (portal == null) return false;
        if (portalRenderer != null && !portalRenderer.isVisible) return false;

        Vector3 toPortal = portal.position - playerPos;
        float distance = toPortal.magnitude;

        if (distance > maxRenderDistance) return false;

        float angle = Vector3.Angle(playerForward, toPortal);
        if (angle > maxViewAngle) return false;

        return true;
    }

    void ProcessPortal(Camera cam, Transform clipPlane, ref bool isClippingActive)
    {
        if (IsCameraWithinDepth(cam, clipPlane))
        {
            isClippingActive = false;
            cam.ResetProjectionMatrix();
        }
        else
        {
            isClippingActive = true;
            ApplyObliqueClipping(cam, clipPlane);
        }
    }

    bool IsCameraWithinDepth(Camera cam, Transform portalPlane)
    {
        Vector3 localCamPos = portalPlane.InverseTransformPoint(cam.transform.position);
        return Mathf.Abs(localCamPos.z) < noClipDepth;
    }

    void ApplyObliqueClipping(Camera cam, Transform portalPlane)
    {
        ApplyObliqueClipping(cam, portalPlane, false);
    }

    void ApplyObliqueClipping(Camera cam, Transform portalPlane, bool flipSide)
    {
        Vector3 camPos = cam.transform.position;
        Vector3 planeNormal = portalPlane.forward;

        float side = Mathf.Sign(Vector3.Dot(planeNormal, portalPlane.position - camPos));
        if (Mathf.Abs(side) < 0.5f) side = 1f;
        if (flipSide) side *= -1f;

        Vector3 planePos = portalPlane.position + planeNormal * clipPlaneOffset * side;

        Matrix4x4 worldToCamMatrix = cam.worldToCameraMatrix;
        Vector3 camSpacePos = worldToCamMatrix.MultiplyPoint(planePos);
        Vector3 camSpaceNormal = worldToCamMatrix.MultiplyVector(planeNormal) * side;
        float camSpaceDst = -Vector3.Dot(camSpacePos, camSpaceNormal);

        Vector4 clipPlaneVector = new Vector4(
            camSpaceNormal.x,
            camSpaceNormal.y,
            camSpaceNormal.z,
            camSpaceDst
        );

        cam.ResetProjectionMatrix();
        cam.projectionMatrix = cam.CalculateObliqueMatrix(clipPlaneVector);
    }

    // ============================================================
    // Gizmos
    // ============================================================

    private void OnDrawGizmosSelected()
    {
        if (!showDebugGizmos) return;

        if (portalPlaneA != null)
            DrawPortalGizmo(portalPlaneA, gizmoColorA, isCameraBRendering, portalStateA, colliderADisabled, lastBodySideA);

        if (portalPlaneB != null)
            DrawPortalGizmo(portalPlaneB, gizmoColorB, isCameraARendering, portalStateB, colliderBDisabled, lastBodySideB);

        if (cameraA != null)
        {
            Gizmos.color = gizmoColorA;
            Gizmos.DrawWireSphere(cameraA.transform.position, 0.15f);
            Gizmos.DrawRay(cameraA.transform.position, cameraA.transform.forward * 1.5f);
        }

        if (cameraB != null)
        {
            Gizmos.color = gizmoColorB;
            Gizmos.DrawWireSphere(cameraB.transform.position, 0.15f);
            Gizmos.DrawRay(cameraB.transform.position, cameraB.transform.forward * 1.5f);
        }

        if (portalPlaneA != null && portalPlaneB != null)
        {
            Gizmos.color = Color.yellow;
            DrawDashedLine(portalPlaneA.position, portalPlaneB.position, 20);
        }
    }

    void DrawDashedLine(Vector3 from, Vector3 to, int segments)
    {
        for (int i = 0; i < segments; i += 2)
        {
            float t1 = (float)i / segments;
            float t2 = (float)(i + 1) / segments;
            Gizmos.DrawLine(Vector3.Lerp(from, to, t1), Vector3.Lerp(from, to, t2));
        }
    }

    void DrawPortalGizmo(Transform portal, Color color, bool isActive, int state, bool colliderDisabled, int bodySide)
    {
        Matrix4x4 oldMatrix = Gizmos.matrix;
        Gizmos.matrix = portal.localToWorldMatrix;

        Vector3 boxSize = new Vector3(portalTriggerWidth, portalTriggerHeight, noClipDepth * 2f);

        Gizmos.color = color;
        Gizmos.DrawWireCube(Vector3.zero, boxSize);

        Color fillColor = color;
        fillColor.a = isActive ? 0.2f : 0.05f;
        Gizmos.color = fillColor;
        Gizmos.DrawCube(Vector3.zero, boxSize);

        Vector3 colliderZoneSize = new Vector3(portalTriggerWidth, portalTriggerHeight, (noClipDepth + colliderDisableBuffer) * 2f);
        Gizmos.color = colliderDisabled ? new Color(1f, 0f, 0f, 0.15f) : new Color(0f, 1f, 0f, 0.1f);
        Gizmos.DrawWireCube(Vector3.zero, colliderZoneSize);

        Gizmos.matrix = oldMatrix;

        if (state == 0) Gizmos.color = Color.green;
        else if (state == 1) Gizmos.color = Color.yellow;
        else Gizmos.color = Color.red;
        Gizmos.DrawSphere(portal.position + Vector3.up * (portalTriggerHeight / 2f + 0.2f), 0.1f);

        Gizmos.color = colliderDisabled ? Color.red : Color.green;
        Gizmos.DrawSphere(portal.position + Vector3.up * (portalTriggerHeight / 2f + 0.4f), 0.08f);

        if (bodySide == 1) Gizmos.color = Color.cyan;
        else if (bodySide == -1) Gizmos.color = Color.magenta;
        else Gizmos.color = Color.white;
        Gizmos.DrawSphere(portal.position + Vector3.up * (portalTriggerHeight / 2f + 0.6f), 0.06f);

        Gizmos.color = color;
        Gizmos.DrawRay(portal.position, portal.forward * 0.8f);
    }

    private void OnDrawGizmos()
    {
        if (!showDebugGizmos) return;

        if (portalPlaneA != null)
        {
            Gizmos.color = gizmoColorA;
            Gizmos.DrawWireSphere(portalPlaneA.position, 0.3f);
        }

        if (portalPlaneB != null)
        {
            Gizmos.color = gizmoColorB;
            Gizmos.DrawWireSphere(portalPlaneB.position, 0.3f);
        }
    }
}
