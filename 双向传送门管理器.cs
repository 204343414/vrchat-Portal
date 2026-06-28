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
    // SebLague 风格递归渲染（使用现有 A/B 相机与材质，无需重新拖引用）
    // ============================================================

    [Header("════════════ Seb递归渲染 1.0 ════════════")]
    [Tooltip("开启后不再依赖 Camera.enabled 自动渲染，而是在 LateUpdate 中用 Camera.Render() 手动多次渲染递归层。")]
    public bool enableSebRecursiveRendering = true;

    [Range(0, 8)]
    [Tooltip("最大递归次数：0=不手动渲染，1=普通一层，2+=递归。")]
    public int recursiveRenderLimit = 3;

    [Tooltip("递归相机手动 Render 前强制 Camera.enabled=false，避免自动渲染重复开销。")]
    [HideInInspector]
    public bool recursiveForceManualCamerasDisabled = true;

    [Tooltip("最深层临时隐藏对面门面，让最后一层看到门后世界/天空盒，作为递归终点。")]
    [HideInInspector]
    public bool recursiveUseSkyboxTerminal = true;

    [Tooltip("渲染当前出口侧时临时隐藏出口门面，避免递归相机被门面挡住。")]
    [HideInInspector]
    public bool recursiveHideExitScreen = true;

    [Tooltip("简易提前停止：递归相机看不到下一扇门时，不继续更深递归。")]
    [HideInInspector]
    public bool recursiveEarlyStop = true;

    [Tooltip("递归提前停止：超过这个距离认为看不到下一层。")]
    [HideInInspector]
    public float recursiveMaxDistance = 80f;

    [Tooltip("递归提前停止：超过这个视角夹角认为看不到下一层。")]
    [HideInInspector]
    public float recursiveMaxViewAngle = 100f;

    [Tooltip("递归调试日志。")]
    [HideInInspector]
    public bool debugRecursiveRenderLog = false;

    [Tooltip("递归调试日志间隔帧。")]
    [HideInInspector]
    public int debugRecursiveLogIntervalFrames = 60;

    [Tooltip("Seb shader 的显示开关属性名。Screen 2D 递归版 shader 已加入 _DisplayMask。")]
    [HideInInspector]
    public string recursiveDisplayMaskProperty = "_DisplayMask";

    [Tooltip("递归终点是否用 Seb 的 displayMask 关闭 linked portal。关闭后将用隐藏 Renderer 的方式，更像天空盒终点。")]
    [HideInInspector]
    public bool recursiveTerminalUseDisplayMask = true;

    [Tooltip("隐藏当前出口门面是否用 displayMask。开启更贴近 Seb，关闭则用 Renderer.enabled=false。")]
    [HideInInspector]
    public bool recursiveHideExitUseDisplayMask = false;

    [Tooltip("递归手动渲染时强制相机 ClearFlags=Skybox，避免 RenderTexture 残影/拖影。")]
    [HideInInspector]
    public bool recursiveForceClearSkybox = true;

    [Tooltip("递归裁剪使用 SebLague 原版 oblique near clip 公式，不使用旧版带符号 clipPlaneOffset 位移。")]
    [HideInInspector]
    public bool recursiveUseSebObliqueClip = true;

    [Tooltip("Seb oblique 裁剪偏移。会取绝对值；建议 0.01~0.05。你的旧 clipPlaneOffset=-0.1 不会再反向污染递归裁剪。")]
    [HideInInspector]
    public float recursiveNearClipOffset = 0.02f;

    [Tooltip("离门太近时不用 oblique projection，避免抖动/反向裁切。")]
    [HideInInspector]
    public float recursiveNearClipLimit = 0.0001f;

    [Tooltip("强制使用递归 oblique 裁剪。你的门间距/near 很小，建议开启；否则离门太近时 ResetProjectionMatrix 会像 near 没对准。")]
    [HideInInspector]
    public bool recursiveForceObliqueClip = true;

    [Tooltip("如果递归裁剪方向确实反了，开启此项翻转 Seb oblique 法线方向。默认关闭。")]
    [HideInInspector]
    public bool recursiveFlipObliqueClipNormal = false;

    [Tooltip("递归渲染专用经典 Portal 半转。一个门在前墙、一个门在侧墙时如果画面方向不对，先试这个。默认跟随 useClassicHalfTurn。")]
    [HideInInspector]
    public bool recursiveRenderUseClassicHalfTurn = false;

    [Tooltip("递归每次 Render 前，把 Camera.nearClipPlane 动态推到刚越过出口传送门平面。用于修复第一层 near 没贴门导致看到下一层/背面的情况。")]
    [HideInInspector]
    public bool recursiveSyncNearClipToPortalPlane = true;

    [Tooltip("递归画面校准：让近裁剪刚好越过出口门面。看到门背面/第二层穿帮就略加大；裁太多就减小。推荐 0.01~0.05。")]
    public float recursiveDynamicNearClipPadding = 0.02f;

    [Header("════════════ 近距离门面置顶修正 ════════════")]
    [Tooltip("玩家头在门框内且非常靠近门面时，把门面 shader 的 ZTest 临时切到 Always，减少薄门/墙后穿帮和闪烁。需要使用递归 Overlay 版 shader。")]
    public bool enablePortalOverlayWhenHeadNear = true;

    [Tooltip("头部离门平面多近时启用门面置顶。建议 0.03~0.12。")]
    public float portalOverlayDepth = 0.08f;

    [Tooltip("shader ZTest 属性名。Overlay 版 shader 已加入 _ZTest。LEqual=4，Always=8。")]
    public string portalOverlayZTestProperty = "_ZTest";

    [HideInInspector]
    [Tooltip("旧调试开关：默认关闭。递归和过渡本应互不干扰；过渡问题通常来自过渡相机 nearClipPlane。")]
    public bool recursivePauseDuringTransition = false;

    [Tooltip("动态 near clip 最大值，防止异常情况下 near 太大导致整屏被裁。")]
    [HideInInspector]
    public float recursiveDynamicNearClipMax = 50f;

    [Tooltip("输出递归裁剪/near clip 调试日志。")]
    [HideInInspector]
    public bool debugRecursiveClipLog = false;

    // ============================================================
    // 新增：过渡系统（极简）
    // ============================================================

    [Header("════════════ 过渡系统（新版） ════════════")]
    [Tooltip("过渡 Cube。子集包含过渡相机。传送时显示并控制旋转，过渡完成后关闭。")]
    public GameObject portalViewTransitionCube;
    [Tooltip("过渡时长（秒）。")]
    public float transitionDuration = 0.5f;

    [Tooltip("过渡相机安全 Near Clip。过渡相机不做传送门裁剪，只需要一个合法的小 near；避免 cameraNearClip 被调到 0 时过渡画面异常。")]
    public float transitionCameraSafeNearClip = 0.01f;

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
    public int recursiveDepthRenderedA = 0;
    public int recursiveDepthRenderedB = 0;

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
    private Camera[] transitionChildCameras;

    // PATCH: 速度延迟重发，防止 VRChat 接地吃速度
    private Vector3 pendingVelocity = Vector3.zero;
    private int pendingVelocityFrames = 0;

    // Seb 递归渲染缓存（固定 8 层，配合 recursiveRenderLimit 滑条）
    private Vector3[] recursivePositionsA;
    private Quaternion[] recursiveRotationsA;
    private Vector3[] recursivePositionsB;
    private Quaternion[] recursiveRotationsB;
    private RenderTexture cachedPortalTextureA;
    private RenderTexture cachedPortalTextureB;

    // ============================================================
    // Start
    // ============================================================

    void Start()
    {
        localPlayer = Networking.LocalPlayer;

        if (cameraA != null) cameraA.nearClipPlane = cameraNearClip;
        if (cameraB != null) cameraB.nearClipPlane = cameraNearClip;

        recursivePositionsA = new Vector3[8];
        recursiveRotationsA = new Quaternion[8];
        recursivePositionsB = new Vector3[8];
        recursiveRotationsB = new Quaternion[8];

        if (enableSebRecursiveRendering && recursiveForceManualCamerasDisabled)
        {
            if (cameraA != null) cameraA.enabled = false;
            if (cameraB != null) cameraB.enabled = false;
        }

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

        if (portalMatA != null) portalMatA.SetFloat(recursiveDisplayMaskProperty, 1f);
        if (portalMatB != null) portalMatB.SetFloat(recursiveDisplayMaskProperty, 1f);
        SyncPortalRenderTextureBindings();

        // 默认关闭过渡 Cube（只在传送时开启）
        if (portalViewTransitionCube != null)
        {
            portalViewTransitionCube.SetActive(false);

            // 缓存并关闭子集相机，直到传送时才开启。避免过渡中每帧 GetComponentsInChildren。
            transitionChildCameras = portalViewTransitionCube.GetComponentsInChildren<Camera>(true);
            foreach (var cam in transitionChildCameras)
            {
                if (cam != null && cam.gameObject != portalViewTransitionCube)
                {
                    cam.enabled = false;
                    cam.nearClipPlane = Mathf.Max(transitionCameraSafeNearClip, 0.001f);
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
                if (cameraA != null) cameraA.enabled = enableSebRecursiveRendering && recursiveForceManualCamerasDisabled ? false : isCameraARendering;
                if (cameraB != null) cameraB.enabled = enableSebRecursiveRendering && recursiveForceManualCamerasDisabled ? false : isCameraBRendering;
            }
            else
            {
                isCameraARendering = true;
                isCameraBRendering = true;
                if (cameraA != null) cameraA.enabled = enableSebRecursiveRendering && recursiveForceManualCamerasDisabled ? false : true;
                if (cameraB != null) cameraB.enabled = enableSebRecursiveRendering && recursiveForceManualCamerasDisabled ? false : true;
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

        UpdatePortalOverlayZTest(playerHead);

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
                    // 传送发生同帧优先交给过渡系统。递归手动渲染延后一帧，避免抢过渡 Cube/相机的显示状态。
                    if (enableSebRecursiveRendering && !(recursivePauseDuringTransition && isTeleporting))
                    {
                        RenderSebRecursivePortals(playerHead, playerWorldRot, syncFOV);
                    }
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
        // SebLague 风格递归渲染（手动 Camera.Render，多层从深到浅）
        // ============================================================

        if (enableSebRecursiveRendering)
        {
            RenderSebRecursivePortals(playerHead, playerWorldRot, syncFOV);
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

        // PATCH 2: 过渡相机只同步玩家视角数据，不参与传送门 oblique 裁剪。
        // nearClipPlane 使用独立安全值，避免 cameraNearClip=0 时过渡画面异常。
        if (transitionChildCameras == null)
        {
            transitionChildCameras = portalViewTransitionCube.GetComponentsInChildren<Camera>(true);
        }
        foreach (var cam in transitionChildCameras)
        {
            if (cam != null)
            {
                cam.fieldOfView = syncFOV;
                cam.nearClipPlane = Mathf.Max(transitionCameraSafeNearClip, 0.001f);
            }
        }

        // 过渡完成
        if (t >= 1.0f)
        {
            isTeleporting = false;

            // 查找子集相机并关闭
            foreach (var cam in transitionChildCameras)
            {
                if (cam != null && cam.gameObject != portalViewTransitionCube)
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

            // 激活所有子集相机（使用缓存，过渡相机不做 portal 裁剪）
            if (transitionChildCameras == null)
            {
                transitionChildCameras = portalViewTransitionCube.GetComponentsInChildren<Camera>(true);
            }
            foreach (var cam in transitionChildCameras)
            {
                if (cam != null && cam.gameObject != portalViewTransitionCube)
                {
                    cam.nearClipPlane = Mathf.Max(transitionCameraSafeNearClip, 0.001f);
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
        if (!isVRPlayer && portalViewTransitionCube != null)
        {
            portalViewTransitionCube.transform.position = headData.position;
            portalViewTransitionCube.transform.rotation = headData.rotation;
        }

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

    // ============================================================
    // 近距离门面置顶：头部进入门框且贴近时，临时 ZTest Always。
    // ============================================================

    void UpdatePortalOverlayZTest(Vector3 playerHead)
    {
        if (!enablePortalOverlayWhenHeadNear)
        {
            SetPortalZTest(portalMatA, 4f);
            SetPortalZTest(portalMatB, 4f);
            return;
        }

        bool nearA = IsHeadInPortalOverlayZone(portalPlaneA, playerHead);
        bool nearB = IsHeadInPortalOverlayZone(portalPlaneB, playerHead);

        SetPortalZTest(portalMatA, nearA ? 8f : 4f);
        SetPortalZTest(portalMatB, nearB ? 8f : 4f);
    }

    bool IsHeadInPortalOverlayZone(Transform portalPlane, Vector3 playerHead)
    {
        if (portalPlane == null) return false;
        Vector3 localHead = portalPlane.InverseTransformPoint(playerHead);
        if (!LocalPointInPortalRect(localHead)) return false;
        return Mathf.Abs(localHead.z) < portalOverlayDepth;
    }

    void SetPortalZTest(Material mat, float zTest)
    {
        if (mat == null) return;
        mat.SetFloat(portalOverlayZTestProperty, zTest);
    }

    // ============================================================
    // SebLague 风格递归渲染核心
    // ============================================================

    void RenderSebRecursivePortals(Vector3 viewerPos, Quaternion viewerRot, float syncFOV)
    {
        if (recursivePauseDuringTransition && isTeleporting) return;
        if (recursiveRenderLimit <= 0) return;
        if (portalParentA == null || portalParentB == null) return;
        if (portalPlaneA == null || portalPlaneB == null) return;

        if (recursiveForceManualCamerasDisabled)
        {
            if (cameraA != null) cameraA.enabled = false;
            if (cameraB != null) cameraB.enabled = false;
        }

        SyncPortalRenderTextureBindings();

        // A 门表面显示 B 侧视角：严格对应 Seb 中 thisPortal=B, linkedPortal=A。
        recursiveDepthRenderedA = RenderSebRecursiveOneSide(
            cameraB,
            portalParentA,
            portalParentB,
            portalPlaneA,
            portalPlaneB,
            rendererA,
            rendererB,
            portalMatA,
            portalMatB,
            isCameraBRendering || isTeleporting || !enableVisibilityOptimization,
            viewerPos,
            viewerRot,
            syncFOV,
            recursivePositionsA,
            recursiveRotationsA
        );

        // B 门表面显示 A 侧视角：严格对应 Seb 中 thisPortal=A, linkedPortal=B。
        recursiveDepthRenderedB = RenderSebRecursiveOneSide(
            cameraA,
            portalParentB,
            portalParentA,
            portalPlaneB,
            portalPlaneA,
            rendererB,
            rendererA,
            portalMatB,
            portalMatA,
            isCameraARendering || isTeleporting || !enableVisibilityOptimization,
            viewerPos,
            viewerRot,
            syncFOV,
            recursivePositionsB,
            recursiveRotationsB
        );

        if (debugRecursiveRenderLog && Time.frameCount % debugRecursiveLogIntervalFrames == 0)
        {
            TPLog("RecursiveRender ADepth=" + recursiveDepthRenderedA + " BDepth=" + recursiveDepthRenderedB + " limit=" + recursiveRenderLimit);
        }
    }

    int RenderSebRecursiveOneSide(
        Camera portalCam,
        Transform linkedParent,
        Transform thisParent,
        Transform linkedPlane,
        Transform thisPlane,
        Renderer linkedScreen,
        Renderer thisScreen,
        Material linkedMat,
        Material thisMat,
        bool linkedVisibleFromPlayer,
        Vector3 viewerPos,
        Quaternion viewerRot,
        float syncFOV,
        Vector3[] positions,
        Quaternion[] rotations
    )
    {
        // Seb: if player is not looking at linked portal screen, skip rendering this view.
        if (!linkedVisibleFromPlayer) return 0;
        if (portalCam == null) return 0;
        if (linkedParent == null || thisParent == null) return 0;
        if (linkedPlane == null || thisPlane == null) return 0;
        if (positions == null || rotations == null) return 0;

        int limit = recursiveRenderLimit;
        if (limit < 0) limit = 0;
        if (limit > 8) limit = 8;
        if (limit > positions.Length) limit = positions.Length;
        if (limit > rotations.Length) limit = rotations.Length;
        if (limit == 0) return 0;

        portalCam.fieldOfView = syncFOV;
        portalCam.nearClipPlane = cameraNearClip;
        portalCam.ResetProjectionMatrix();
        if (recursiveForceManualCamerasDisabled) portalCam.enabled = false;

        // Seb 原逻辑：从 player camera 的 localToWorldMatrix 开始，重复乘 this * linked^-1。
        // 注意这里 linkedParent 是玩家正在看的门，thisParent 是门后出口。
        Matrix4x4 localToWorldMatrix = Matrix4x4.TRS(viewerPos, viewerRot, Vector3.one);
        Matrix4x4 linkedWorldToLocal = Matrix4x4.TRS(linkedParent.position, linkedParent.rotation, Vector3.one).inverse;
        Matrix4x4 thisLocalToWorld = Matrix4x4.TRS(thisParent.position, thisParent.rotation, Vector3.one);
        Matrix4x4 halfTurnMatrix = Matrix4x4.Rotate(Quaternion.AngleAxis(180f, Vector3.up));
        bool renderHalfTurn = useClassicHalfTurn || recursiveRenderUseClassicHalfTurn;

        int startIndex = limit;
        int count = 0;

        for (int i = 0; i < limit; i++)
        {
            if (i > 0 && recursiveEarlyStop)
            {
                // Seb 用 BoundsOverlap 判断 linked portal 在当前递归相机里是否还可见。
                // 这里用 WorldToViewportPoint 对门面四角做近似 bounds overlap，语义保持一致。
                if (!PortalBoundsOverlapCameraView(portalCam, linkedPlane))
                {
                    break;
                }
            }

            if (renderHalfTurn)
            {
                localToWorldMatrix = thisLocalToWorld * halfTurnMatrix * linkedWorldToLocal * localToWorldMatrix;
            }
            else
            {
                localToWorldMatrix = thisLocalToWorld * linkedWorldToLocal * localToWorldMatrix;
            }

            int renderOrderIndex = limit - i - 1;
            positions[renderOrderIndex] = localToWorldMatrix.GetColumn(3);
            rotations[renderOrderIndex] = localToWorldMatrix.rotation;

            portalCam.transform.SetPositionAndRotation(positions[renderOrderIndex], rotations[renderOrderIndex]);
            startIndex = renderOrderIndex;
            count++;
        }

        if (count <= 0 || startIndex >= limit) return 0;

        bool oldThisScreenEnabled = true;
        bool oldLinkedScreenEnabled = true;
        if (thisScreen != null) oldThisScreenEnabled = thisScreen.enabled;
        if (linkedScreen != null) oldLinkedScreenEnabled = linkedScreen.enabled;

        // Seb 原版在本函数结束前会恢复显示。这里不读 GetFloat，避免 Udon API 差异；默认恢复为 1。
        float oldThisMask = 1f;
        float oldLinkedMask = 1f;

        CameraClearFlags oldClearFlags = portalCam.clearFlags;
        if (recursiveForceClearSkybox)
        {
            // 很多“残影/拖影”其实是 RT 没有每次完整清屏，尤其 Camera 是 DepthOnly/Don'tClear 时。
            portalCam.clearFlags = CameraClearFlags.Skybox;
        }

        // Seb: Hide screen so that camera can see through portal screen.
        // 原工程透明 shader 用 displayMask 更稳；如果材质不支持，则可回退 Renderer.enabled=false。
        if (recursiveHideExitScreen)
        {
            if (recursiveHideExitUseDisplayMask && thisMat != null)
            {
                SetPortalDisplayMask(thisMat, 0f);
            }
            else if (thisScreen != null)
            {
                thisScreen.enabled = false;
            }
        }

        // Seb: linkedPortal.screen.material.SetInt("displayMask", 0)，作为最深层递归终点。
        bool linkedTerminalHiddenByRenderer = false;
        if (recursiveUseSkyboxTerminal)
        {
            if (recursiveTerminalUseDisplayMask && linkedMat != null)
            {
                SetPortalDisplayMask(linkedMat, 0f);
            }
            else if (linkedScreen != null)
            {
                linkedScreen.enabled = false;
                linkedTerminalHiddenByRenderer = true;
            }
            portalCam.clearFlags = CameraClearFlags.Skybox;
        }

        for (int i = startIndex; i < limit; i++)
        {
            portalCam.transform.SetPositionAndRotation(positions[i], rotations[i]);
            SyncRecursiveNearClipToPortalPlane(portalCam, thisPlane, i, startIndex);
            ApplyObliqueClippingSebStyle(portalCam, thisPlane);
            portalCam.Render();

            // Seb: after rendering the deepest layer, re-enable linked portal screen.
            if (i == startIndex && recursiveUseSkyboxTerminal)
            {
                if (recursiveTerminalUseDisplayMask && linkedMat != null)
                {
                    SetPortalDisplayMask(linkedMat, 1f);
                }
                else if (linkedTerminalHiddenByRenderer && linkedScreen != null)
                {
                    linkedScreen.enabled = oldLinkedScreenEnabled;
                    linkedTerminalHiddenByRenderer = false;
                }
                portalCam.clearFlags = oldClearFlags;
            }
        }

        // Restore states before player camera renders.
        if (thisMat != null) SetPortalDisplayMask(thisMat, oldThisMask);
        if (linkedMat != null) SetPortalDisplayMask(linkedMat, oldLinkedMask);
        if (thisScreen != null) thisScreen.enabled = oldThisScreenEnabled;
        if (linkedScreen != null) linkedScreen.enabled = oldLinkedScreenEnabled;
        portalCam.clearFlags = oldClearFlags;
        portalCam.nearClipPlane = cameraNearClip;
        portalCam.ResetProjectionMatrix();

        return count;
    }

    void SyncRecursiveNearClipToPortalPlane(Camera cam, Transform clipPlane, int renderIndex, int startIndex)
    {
        if (cam == null) return;

        if (!recursiveSyncNearClipToPortalPlane || clipPlane == null)
        {
            cam.nearClipPlane = cameraNearClip;
            cam.ResetProjectionMatrix();
            return;
        }

        // 普通 Camera.nearClipPlane 是垂直于 cam.forward 的平面。
        // 这里先把 near 推到“沿相机 forward 到传送门平面”的距离之后，
        // 再叠加 oblique clip，把真正裁剪面贴到 portal plane。
        // 这能修复 VRChat/透明门面/递归 RT 下第一层 near 仍停在 0.01 导致看到门背面或下一层画面的情况。
        float forwardDst = Vector3.Dot(clipPlane.position - cam.transform.position, cam.transform.forward);
        float newNear = cameraNearClip;

        if (forwardDst > cameraNearClip)
        {
            newNear = forwardDst + Mathf.Abs(recursiveDynamicNearClipPadding);
            if (newNear < cameraNearClip) newNear = cameraNearClip;
            if (newNear > recursiveDynamicNearClipMax) newNear = recursiveDynamicNearClipMax;
        }

        cam.nearClipPlane = newNear;
        cam.ResetProjectionMatrix();

        if (debugRecursiveClipLog && Time.frameCount % debugRecursiveLogIntervalFrames == 0)
        {
            TPLog("RecursiveNear renderIndex=" + renderIndex + " start=" + startIndex + " forwardDst=" + forwardDst + " near=" + newNear + " cam=" + cam.name + " clip=" + clipPlane.name);
        }
    }

    void ApplyObliqueClippingSebStyle(Camera cam, Transform clipPlane)
    {
        if (!recursiveUseSebObliqueClip)
        {
            ApplyObliqueClipping(cam, clipPlane);
            return;
        }

        if (cam == null || clipPlane == null) return;

        // SebLague 原版 SetNearClipPlane 逻辑：
        // Transform clipPlane = transform;
        // int dot = Sign(Dot(clipPlane.forward, transform.position - portalCam.position));
        // camSpaceDst = -Dot(camSpacePos, camSpaceNormal) + nearClipOffset;
        // 注意：这里 nearClipOffset 始终使用正值，避免旧配置 clipPlaneOffset=-0.1 把裁剪面推到反方向。
        int dot = System.Math.Sign(Vector3.Dot(clipPlane.forward, clipPlane.position - cam.transform.position));
        if (dot == 0) dot = 1;
        if (recursiveFlipObliqueClipNormal) dot *= -1;

        Vector3 camSpacePos = cam.worldToCameraMatrix.MultiplyPoint(clipPlane.position);
        Vector3 camSpaceNormal = cam.worldToCameraMatrix.MultiplyVector(clipPlane.forward) * dot;
        float camSpaceDst = -Vector3.Dot(camSpacePos, camSpaceNormal) + Mathf.Abs(recursiveNearClipOffset);

        if (recursiveForceObliqueClip || Mathf.Abs(camSpaceDst) > recursiveNearClipLimit)
        {
            Vector4 clipPlaneCameraSpace = new Vector4(
                camSpaceNormal.x,
                camSpaceNormal.y,
                camSpaceNormal.z,
                camSpaceDst
            );
            cam.ResetProjectionMatrix();
            cam.projectionMatrix = cam.CalculateObliqueMatrix(clipPlaneCameraSpace);
        }
        else
        {
            cam.ResetProjectionMatrix();
        }
    }

    bool PortalBoundsOverlapCameraView(Camera cam, Transform portalPlane)
    {
        if (cam == null || portalPlane == null) return false;

        Vector3 toPortal = portalPlane.position - cam.transform.position;
        float dist = toPortal.magnitude;
        if (dist > recursiveMaxDistance) return false;
        if (dist < 0.001f) return true;

        float angle = Vector3.Angle(cam.transform.forward, toPortal);
        if (angle > recursiveMaxViewAngle) return false;

        float hx = portalTriggerWidth * 0.5f;
        float hy = portalTriggerHeight * 0.5f;

        Vector3 p0 = portalPlane.TransformPoint(new Vector3(-hx, -hy, 0f));
        Vector3 p1 = portalPlane.TransformPoint(new Vector3(-hx,  hy, 0f));
        Vector3 p2 = portalPlane.TransformPoint(new Vector3( hx, -hy, 0f));
        Vector3 p3 = portalPlane.TransformPoint(new Vector3( hx,  hy, 0f));

        Vector3 v0 = cam.WorldToViewportPoint(p0);
        Vector3 v1 = cam.WorldToViewportPoint(p1);
        Vector3 v2 = cam.WorldToViewportPoint(p2);
        Vector3 v3 = cam.WorldToViewportPoint(p3);

        bool anyInFront = v0.z > 0f || v1.z > 0f || v2.z > 0f || v3.z > 0f;
        if (!anyInFront) return false;

        float minX = Mathf.Min(Mathf.Min(v0.x, v1.x), Mathf.Min(v2.x, v3.x));
        float maxX = Mathf.Max(Mathf.Max(v0.x, v1.x), Mathf.Max(v2.x, v3.x));
        float minY = Mathf.Min(Mathf.Min(v0.y, v1.y), Mathf.Min(v2.y, v3.y));
        float maxY = Mathf.Max(Mathf.Max(v0.y, v1.y), Mathf.Max(v2.y, v3.y));

        return maxX >= 0f && minX <= 1f && maxY >= 0f && minY <= 1f;
    }

    void SetPortalDisplayMask(Material mat, float value)
    {
        if (mat == null) return;
        mat.SetFloat(recursiveDisplayMaskProperty, value);
    }

    void SyncPortalRenderTextureBindings()
    {
        // 保持原工程拖好的 targetTexture 关系：cameraB -> A 门；cameraA -> B 门。
        // 只在 RT 引用变化时 SetTexture，避免每帧重复改材质状态。
        RenderTexture texA = null;
        RenderTexture texB = null;

        if (cameraB != null) texA = cameraB.targetTexture;
        if (cameraA != null) texB = cameraA.targetTexture;

        if (portalMatA != null && texA != null && cachedPortalTextureA != texA)
        {
            portalMatA.SetTexture("_MainTex", texA);
            cachedPortalTextureA = texA;
        }

        if (portalMatB != null && texB != null && cachedPortalTextureB != texB)
        {
            portalMatB.SetTexture("_MainTex", texB);
            cachedPortalTextureB = texB;
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
