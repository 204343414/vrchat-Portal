using UdonSharp;
using UnityEngine;
using VRC.SDKBase;
using VRC.SDK3.Rendering;
using System.Collections.Generic;

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

    // 形状类型常量：传给 portalShapeA / portalShapeB。用 int 而不是自定义 enum，
    // 是为了避开 UdonSharp 对自定义枚举的一些已知限制（默认值/相等比较等）。
    // 注意：const 字段不会被 Unity 序列化/显示，不能挂 [Header]，所以 Header 要贴在下面第一个真正显示的字段上。
    public const int PORTAL_SHAPE_CIRCLE = 0;
    public const int PORTAL_SHAPE_TRIANGLE = 1;
    public const int PORTAL_SHAPE_BOX = 2;
    // 未设置的哨兵值：只有旧场景升级时（字段缺失、被 Unity 用编译期默认值补上）才会保持这个值。
    // 一旦玩家在 Inspector 里手动填过 0/1/2 中任意一个合法值，就再也不会被旧开关覆盖。
    public const int PORTAL_SHAPE_UNSET = -1;

    [Header("════════════ 传送门形状 ════════════")]
    [Tooltip("传送门A的判定形状：0=圆形/椭圆，1=三角形（等腰三角，尖角朝上，底边在下），2=方框（矩形）。\n三种形状都以 portalTriggerWidth/portalTriggerHeight 作为外接包围盒尺寸。\n影响范围：实际传送触发判定、碰撞穿透区域、门面可见性/体积判定，以及下方 Gizmos 调试线框显示，四处逻辑统一使用同一个形状。\n留空(-1)则跟随下方旧版 useCircularPortalCheck 自动换算。")]
    public int portalShapeA = PORTAL_SHAPE_UNSET;

    [Tooltip("传送门B的判定形状，取值含义同 portalShapeA（0=圆形，1=三角形，2=方框，-1=跟随旧开关）。A、B两门形状可以不同。")]
    public int portalShapeB = PORTAL_SHAPE_UNSET;

    [Tooltip("旧版全局圆形开关（已废弃）。仅当 portalShapeA/portalShapeB 还是 -1（未设置）时才会被读取，用于把旧场景自动换算成新形状；一旦 portalShapeA/B 被手动设成 0/1/2，此开关不再生效。")]
    public bool useCircularPortalCheck = true;

    [Header("════════════ 碰撞控制 ════════════")]
    public 传送枪 portalGun;

    [Tooltip("玩家进入传送门前后多远开始临时切换图层。实际判定深度 = noClipDepth + colliderDisableBuffer + 速度缓冲；想让切换更早/更晚，优先微调这个值。")]
    public float colliderDisableBuffer = 0.15f;

    [Tooltip("玩家靠近传送门时，被传送枪标记的碰撞体所在物体将从此 Layer 临时切到 playerPassThroughLayer。默认 28。")]
    public int solidCollisionLayer = 28;

    [Tooltip("玩家靠近传送门时临时切换到的 Layer。请在 Unity Collision Matrix 中配置为不与 Player 碰撞，但仍与物品/刚体碰撞。默认 25。")]
    public int playerPassThroughLayer = 25;

    [Tooltip("刚体进入传送门区域时切换至的 Layer（推荐 17 = Walkthrough）\n17层：玩家可穿透，但其他物体仍可碰撞。比13和5都更合适")]
    public int rigidbodyPassThroughLayer = 17;

    [Tooltip("刚体离开传送门区域后是否自动还原原始 Layer")]
    public bool restoreRigidbodyLayerOnExit = true;

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

    [Header("════════════ 刚体传送（Portal 原著复刻）════════════")]
    [Tooltip("是否启用刚体传送功能")]
    public bool enableRigidbodyTeleport = true;
    [Tooltip("刚体检测周期（复用 checkInterval）")]
    public float rbCheckIntervalMultiplier = 1f;
    [Tooltip("刚体检测用的 OverlapBox 额外深度扩展（建议 0.5~1.0 防止高速物体漏检）")]
    public float rbTriggerDepthExtension = 0.8f;
    [Tooltip("是否对抓取中的刚体也做传送（配合传送枪的 UpdateHeldAfterTeleport）")]
    public bool allowHeldRigidbodyTeleport = true;

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

    [Header("════════════ 配置快照导出 ════════════")]
    [Tooltip("开局时（Start）把当前 Inspector 关键配置 + 传送门A/B父物体下所有子物体的 Collider/Renderer/Mesh/Camera/Light/AudioSource/Rigidbody 信息打印到控制台。\n用途：把这份文本复制给别人分析场景结构、排查性能问题，不用截图/不用一个个字段抄。\n只在 Start 执行一次，不影响运行时性能。")]
    public bool dumpConfigSnapshotOnStart = true;

    [Header("════════════ 同Collider专修/调试 ════════════")]
    [Tooltip("总日志开关。关闭后本脚本不输出传送门日志。")]
    public bool debugTeleportLog = true;
    [Tooltip("核心传送短日志：只输出 T# 和 OUT，推荐测试时开启。")]
    public bool debugTeleportCoreLog = true;
    [Tooltip("图层切换日志：28<->25。稳定后建议关闭，避免刷屏。")]
    public bool debugLayerLog = false;
    [Tooltip("过渡相机日志。稳定后建议关闭，避免刷屏。")]
    public bool debugTransitionLog = false;
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
    [Tooltip("玩家头部进入传送门前后多深范围内才开始追踪，建议 0.6~1.2")]
    public float travellerTrackDepth = 0.8f;

    [Tooltip("跨越平面判断死区，防止 z 接近 0 抖动")]
    public float crossingEpsilon = 0.005f;

    [Header("════════════ 传送触发平面 ════════")]
    [Tooltip("传送触发平面离门中心的距离。默认等于 noClipDepth（门厚度外边缘）。设为 0 则回到旧版 z=0 中心触发。Gizmos 中用黄色线框显示触发平面位置。")]
    public float teleportTriggerOffset = 0.3f;

    [Tooltip("传送 traveller 使用根骨/玩家位置而不是头部。推荐开启：歪头不会触发传送，TeleportTo 也不再从头部反推 root；关闭则回到旧头部模式。")]
    public bool useRootAsTraveller = true;

    [Tooltip("混合 traveller：门平面内 XY 使用根骨/root，穿越深度 Z 使用头部/head。推荐开启：避免歪头横向影响，又避免地板/天花板门脚先触发导致头卡天花板。")]
    public bool useHybridRootXYHeadZTraveller = true;

    [Tooltip("出口侧保险：如果计算出的出口 traveller 落在入口侧/门背面，则只沿出口法线拉回到正确侧一点点。主要防45度斜面/角色控制器误差导致来回鬼畜。")]
    public bool enableExitSideCorrection = true;

    [Tooltip("出口侧保险的最小离门距离，建议 0.01~0.03。不是速度推力，只在落到错误侧或太贴门时修正。")]
    public float exitSideMinDistance = 0.02f;

    [Tooltip("头部 traveller 旧模式使用：用 Head 算出新 Head，再用 AvatarRoot/Origin 偏移算 TeleportTo 位置。根骨 traveller 模式会直接 TeleportTo 新 root。")]
    [HideInInspector]
    public bool useVRCTrackingRootTeleport = true;

    // 刚体追踪 - 100% Udon 兼容（固定数组 + 计数器，避免 List.Add / Dictionary）
    private const int MAX_TRACKED_RBS = 32;
    private Rigidbody[] trackedRigidbodies = new Rigidbody[MAX_TRACKED_RBS];
    private Vector3[] rbPrevPosList = new Vector3[MAX_TRACKED_RBS];
    private int[] rbOriginalLayerList = new int[MAX_TRACKED_RBS];
    private int trackedRBCount = 0;

    [Tooltip("传送空间变换忽略 Transform 缩放，避免门/父物体 scale 影响传送位置")]
    [HideInInspector]
    public bool useScaleFreePortalMatrix = true;

    [Tooltip("经典 Portal 半转。新模式开启：传送枪不再翻B门本体，所有门到门映射统一使用 to * halfTurn * from^-1。")]
    public bool useClassicHalfTurn = true;

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

    // 性能优化：GetVelocity() 在同一帧内结果不变，每帧只在 LateUpdate 开头算一次 speedBuffer，
    // 供 IsBodyInColliderZone / ProcessPortalTeleport 共用，避免同一帧重复调用 3~4 次。
    // 数值算法与之前完全一致，只是省掉重复计算，不改变任何判定结果。
    private float cachedSpeedBufferThisFrame = 0f;

    // Layer 穿透状态：替代旧版 markedCollider.enabled=false。
    // 只改被传送枪打中的 Collider 所在 GameObject 的 layer：28 -> 25；离开后恢复原始 layer。
    private bool layerOverrideAActive = false;
    private bool layerOverrideBActive = false;
    private int originalLayerA = -1;
    private int originalLayerB = -1;
    private Collider layerOverrideColliderA;
    private Collider layerOverrideColliderB;

    private int lastBodySideA = 0;
    private int lastBodySideB = 0;

    private int teleportBlockedUntilFrame = -1;
    private int teleportSeq = 0;
    private bool warnedSharedCollider = false;

    // Sebastian Lague 风格 traveller tracking
    private bool travellerTrackingA = false;
    private bool travellerTrackingB = false;
    // traveller tracking：保存触发用 traveller local 和对应的 teleport local。
    private Vector3 previousTravellerLocalA = Vector3.zero;
    private Vector3 previousTravellerLocalB = Vector3.zero;
    // 保存真实 TeleportTo 点的 local（root模式=root，head模式=head），用于按 crossingT 插值传送位置。
    private Vector3 previousTeleportLocalA = Vector3.zero;
    private Vector3 previousTeleportLocalB = Vector3.zero;

    // ============================================================
    // 过渡系统变量
    // ============================================================

    private float transitionStartTime = 0f;
    private Quaternion fromPortalRotAtTeleport;
    private Quaternion toPortalRotAtTeleport;
    private bool isTeleporting = false;
    private Camera[] transitionChildCameras;

    [Header("════════════ 速度应用调试 ════════════")]
    [Tooltip("TeleportTo 后额外重发速度的帧数。经典Portal/无限下坠建议为0；旧值2会在每次传送后抵消数帧重力，导致越飞越高。")]
    public int velocityReapplyFrames = 0;

    // PATCH: 速度延迟重发，防止 VRChat 接地吃速度。经典Portal模式默认不重发，避免注入能量。
    private Vector3 pendingVelocity = Vector3.zero;
    private int pendingVelocityFrames = 0;

    // Seb 递归渲染缓存（固定 8 层，配合 recursiveRenderLimit 滑条）
    private Vector3[] recursivePositionsA;
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

        // 兼容旧场景序列化：Unity 已挂到场景里的 UdonBehaviour 不会因为脚本默认值从 29 改到 25 就自动刷新。
        // 如果 Inspector 里还保留旧值 29，这里运行时强制迁移到用户实测可用的 25。
        if (playerPassThroughLayer == 29)
        {
            playerPassThroughLayer = 25;
            TPLog("[启动] 检测到旧穿透层29，已自动改为25。若仍看到29，请检查场景中是否有旧脚本/旧Prefab未更新。");
        }

        // 新版坐标约定：传送枪不再把B门本体翻180度，经典半转必须放在门到门映射里。
        // 旧场景里 useClassicHalfTurn 可能已序列化为 false，这里运行时强制迁移，避免半新半旧状态。
        if (!useClassicHalfTurn)
        {
            useClassicHalfTurn = true;
            TPLog("[启动] 已启用经典Portal半转映射。请确认传送枪 applyBHalfTurnInGun=false。");
        }

        // 传送触发偏移：旧场景序列化值为 0 时，自动同步到 noClipDepth。
        if (teleportTriggerOffset <= 0f)
        {
            teleportTriggerOffset = noClipDepth;
        }

        if (cameraA != null) cameraA.nearClipPlane = cameraNearClip;
        if (cameraB != null) cameraB.nearClipPlane = cameraNearClip;

        recursivePositionsA = new Vector3[8];
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

        TPLog("[启动] 是否VR=" + isVRPlayer + " capsuleRadius=" + playerCapsuleRadius + " capsuleHeight=" + playerCapsuleHeight);

        if (dumpConfigSnapshotOnStart)
        {
            DumpConfigSnapshot();
        }
    }

    // ============================================================
    // 主循环
    // ============================================================

    private void LateUpdate()
    {
        if (localPlayer == null || !localPlayer.IsValid()) return;
        if (portalParentA == null || portalParentB == null) return;
        if (portalPlaneA == null || portalPlaneB == null) return;

        // 性能优化：本帧 speedBuffer 只算一次，供下面碰撞穿透判定复用（算法不变，见字段注释）。
        cachedSpeedBufferThisFrame = Mathf.Clamp(localPlayer.GetVelocity().magnitude * Time.deltaTime * 2.0f, 0f, 1.5f);

        // 刚体传送处理（优先保证能检测到，先用高频）
        if (enableRigidbodyTeleport)
        {
            ProcessRigidbodyTravellers();
        }

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

        Quaternion cameraHalfTurn = LocalHalfTurn();
        if (cameraB != null)
        {
            Vector3 playerLocalToA = portalParentA.InverseTransformPoint(playerHead);
            Quaternion playerLocalRotToA = Quaternion.Inverse(portalParentA.rotation) * playerWorldRot;
            if (useClassicHalfTurn)
            {
                playerLocalToA = cameraHalfTurn * playerLocalToA;
                playerLocalRotToA = cameraHalfTurn * playerLocalRotToA;
            }
            cameraB.transform.position = portalParentB.TransformPoint(playerLocalToA);
            cameraB.transform.rotation = portalParentB.rotation * playerLocalRotToA;
        }

        if (cameraA != null)
        {
            Vector3 playerLocalToB = portalParentB.InverseTransformPoint(playerHead);
            Quaternion playerLocalRotToB = Quaternion.Inverse(portalParentB.rotation) * playerWorldRot;
            if (useClassicHalfTurn)
            {
                playerLocalToB = cameraHalfTurn * playerLocalToB;
                playerLocalRotToB = cameraHalfTurn * playerLocalRotToB;
            }
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
            // 头在传送门体积内时强制渲染：IsPortalVisible 可能因角度/距离返回 false，
            // 导致贴门时相机被优化掉、画面消失。
            if (IsHeadInsidePortalVolume(portalPlaneA, playerHead, ResolvePortalShape(true)) || IsHeadInsidePortalVolume(portalPlaneB, playerHead, ResolvePortalShape(false)))
            {
                forcePortalCameraRendering = true;
            }
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
                TPLog("[传送检测暂停] 直到帧 " + teleportBlockedUntilFrame);
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

        bool headInsidePortalA = IsHeadInsidePortalVisualVolume(portalPlaneA, playerHead, ResolvePortalShape(true));
        bool headInsidePortalB = IsHeadInsidePortalVisualVolume(portalPlaneB, playerHead, ResolvePortalShape(false));

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

        // 传送门旋转差。
        // 新经典 Portal 坐标约定下，A/B 门本体不再预翻转；真正的门到门映射是：to * halfTurn * from^-1。
        // 过渡视角也必须使用同一套旋转，否则地板/天花板无限下坠时过渡画面会翻转/补偿方向错误。
        Quaternion portalDelta;
        if (useClassicHalfTurn)
        {
            portalDelta = toPortalRotAtTeleport * LocalHalfTurn() * Quaternion.Inverse(fromPortalRotAtTeleport);
        }
        else
        {
            portalDelta = toPortalRotAtTeleport * Quaternion.Inverse(fromPortalRotAtTeleport);
        }

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

            if (debugTransitionLog) TPLog("[过渡完成] 相机已关闭，过渡体已隐藏");
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

        if (debugTransitionLog) TPLog("[过渡开始] 从=" + fromPlane.name + " to=" + toPlane.name);
    }

    // ============================================================
    // 工具方法
    // ============================================================

    void TPLog(string msg)
    {
        if (!debugTeleportLog) return;
        Debug.Log("[传送门] 帧=" + Time.frameCount + " " + msg);
    }

    // ============================================================
    // 配置快照导出：把当前 Inspector 关键配置 + A/B 门下所有子物体信息打印到控制台。
    // 只在 Start() 里按 dumpConfigSnapshotOnStart 开关跑一次，不影响运行时（LateUpdate）性能。
    // 不用 TPLog（会被 debugTeleportLog 总开关吃掉），直接 Debug.Log，保证这个开关独立生效。
    // ============================================================

    string GetPortalShapeName(int shape)
    {
        if (shape == PORTAL_SHAPE_CIRCLE) return "圆形(0)";
        if (shape == PORTAL_SHAPE_TRIANGLE) return "三角形(1)";
        if (shape == PORTAL_SHAPE_BOX) return "方框(2)";
        if (shape == PORTAL_SHAPE_UNSET) return "未设置(-1，跟随旧开关)";
        return "未知值(" + shape + ")";
    }

    void DumpConfigSnapshot()
    {
        Debug.Log("========== [配置快照] 开始（双向传送门管理器：" + gameObject.name + "） ==========");
        DumpGlobalConfigSnapshot();
        DumpPortalGunConfigSnapshot();
        DumpPortalHierarchySnapshot("A", portalParentA, portalPlaneA, cameraA, portalMatA, ResolvePortalShape(true), portalShapeA);
        DumpPortalHierarchySnapshot("B", portalParentB, portalPlaneB, cameraB, portalMatB, ResolvePortalShape(false), portalShapeB);
        Debug.Log("========== [配置快照] 结束 ==========");
    }

    void DumpGlobalConfigSnapshot()
    {
        Debug.Log("[配置快照][全局] cameraNearClip=" + cameraNearClip);
        Debug.Log("[配置快照][门厚度/形状] noClipDepth=" + noClipDepth + " portalTriggerWidth=" + portalTriggerWidth + " portalTriggerHeight=" + portalTriggerHeight + " clipPlaneOffset=" + clipPlaneOffset);
        Debug.Log("[配置快照][门厚度/形状] portalShapeA原始=" + portalShapeA + " portalShapeB原始=" + portalShapeB + " useCircularPortalCheck(旧开关)=" + useCircularPortalCheck);
        Debug.Log("[配置快照][碰撞控制] colliderDisableBuffer=" + colliderDisableBuffer + " solidCollisionLayer=" + solidCollisionLayer + " playerPassThroughLayer=" + playerPassThroughLayer);
        Debug.Log("[配置快照][性能优化] enableVisibilityOptimization=" + enableVisibilityOptimization + " maxRenderDistance=" + maxRenderDistance + " maxViewAngle=" + maxViewAngle + " checkInterval=" + checkInterval);
        Debug.Log("[配置快照][Seb递归渲染] enableSebRecursiveRendering=" + enableSebRecursiveRendering + " recursiveRenderLimit=" + recursiveRenderLimit + " recursiveEarlyStop=" + recursiveEarlyStop + " recursiveMaxDistance=" + recursiveMaxDistance + " recursiveMaxViewAngle=" + recursiveMaxViewAngle + " recursiveForceClearSkybox=" + recursiveForceClearSkybox);
        Debug.Log("[配置快照][传送触发] teleportTriggerOffset=" + teleportTriggerOffset + " useRootAsTraveller=" + useRootAsTraveller + " useHybridRootXYHeadZTraveller=" + useHybridRootXYHeadZTraveller + " useClassicHalfTurn=" + useClassicHalfTurn + " enableExitSideCorrection=" + enableExitSideCorrection);
        Debug.Log("[配置快照][过渡系统] portalViewTransitionCube=" + (portalViewTransitionCube != null ? portalViewTransitionCube.name : "未指定") + " transitionDuration=" + transitionDuration);
        Debug.Log("[配置快照][调试开关] debugTeleportLog=" + debugTeleportLog + " debugTeleportCoreLog=" + debugTeleportCoreLog + " debugLayerLog=" + debugLayerLog + " debugTransitionLog=" + debugTransitionLog + " debugTeleportVerbose=" + debugTeleportVerbose + " showDebugGizmos=" + showDebugGizmos);
        Debug.Log("[配置快照][运行环境] isVRPlayer=" + isVRPlayer + " vrTargetFOV=" + vrTargetFOV + " currentFOV=" + currentFOV);
    }

    void DumpPortalGunConfigSnapshot()
    {
        if (portalGun == null)
        {
            Debug.Log("[配置快照][传送枪] 未指定 portalGun 引用");
            return;
        }
        Debug.Log(
            "[配置快照][传送枪] 物体=" + portalGun.gameObject.name +
            " maxDistance=" + portalGun.maxDistance +
            " wallOffset=" + portalGun.wallOffset +
            " cooldownTime=" + portalGun.cooldownTime +
            " applyBHalfTurnInGun=" + portalGun.applyBHalfTurnInGun +
            " placementLayers=" + portalGun.placementLayers.value +
            " blockedLayers=" + portalGun.blockedLayers.value
        );
    }

    /// 不用用户自定义递归函数遍历子物体（UdonSharp 明确不支持自定义方法递归：所有调用共享同一份栈变量，深层递归会互相踩坏数据）。
    /// 改用引擎自带的 Transform.GetComponentsInChildren（引擎内部实现，不受此限制）一次性拿到整棵子树，再逐个用普通循环处理。
    void DumpPortalHierarchySnapshot(string label, Transform root, Transform plane, Camera cam, Material mat, int resolvedShape, int rawShape)
    {
        if (root == null)
        {
            Debug.Log("[配置快照][门" + label + "] portalParent" + label + " 未指定，跳过");
            return;
        }

        Debug.Log(
            "[配置快照][门" + label + "] 父物体=" + root.name +
            " 判定形状=" + GetPortalShapeName(resolvedShape) + "(原始字段值=" + rawShape + ")" +
            " 世界坐标=" + root.position + " 世界欧拉角=" + root.rotation.eulerAngles
        );

        if (plane != null)
        {
            Debug.Log("[配置快照][门" + label + "] portalPlane" + label + "=" + plane.name + " 世界坐标=" + plane.position);
        }
        else
        {
            Debug.Log("[配置快照][门" + label + "] portalPlane" + label + " 未指定");
        }

        if (cam != null)
        {
            Debug.Log(
                "[配置快照][门" + label + "] 摄像机=" + cam.name +
                " fov=" + cam.fieldOfView + " nearClip=" + cam.nearClipPlane + " farClip=" + cam.farClipPlane +
                " targetTexture=" + (cam.targetTexture != null ? (cam.targetTexture.width + "x" + cam.targetTexture.height) : "无") +
                " cullingMask=" + cam.cullingMask
            );
        }
        else
        {
            Debug.Log("[配置快照][门" + label + "] 摄像机未指定");
        }

        if (mat != null)
        {
            Debug.Log("[配置快照][门" + label + "] 材质=" + mat.name + " shader=" + (mat.shader != null ? mat.shader.name : "无"));
        }

        Transform[] allChildren = root.GetComponentsInChildren<Transform>(true);

        int totalTransforms = allChildren.Length;
        int totalRenderers = 0;
        int totalColliders = 0;
        int totalLights = 0;
        int totalAudioSources = 0;
        int totalRigidbodies = 0;
        int totalCameras = 0;
        long totalVerts = 0;
        long totalTris = 0;

        Debug.Log("[配置快照][门" + label + "] ---- 子物体清单开始（共 " + totalTransforms + " 个，含自身）----");

        foreach (Transform t in allChildren)
        {
            GameObject go = t.gameObject;

            int depth = 0;
            Transform walker = t;
            while (walker != null && walker != root && depth < 32)
            {
                walker = walker.parent;
                depth++;
            }
            string indent = "";
            for (int i = 0; i < depth; i++) indent += "  ";

            string line = "[配置快照][门" + label + "]" + indent + " ├ " + t.name +
                " active=" + go.activeSelf +
                " layer=" + go.layer +
                " localPos=" + t.localPosition + " localScale=" + t.localScale;

            Collider col = go.GetComponent<Collider>();
            if (col != null)
            {
                totalColliders++;
                string colShape = "Collider(未知子类型)";
                if (go.GetComponent<BoxCollider>() != null) colShape = "BoxCollider";
                else if (go.GetComponent<SphereCollider>() != null) colShape = "SphereCollider";
                else if (go.GetComponent<CapsuleCollider>() != null) colShape = "CapsuleCollider";
                else if (go.GetComponent<MeshCollider>() != null) colShape = "MeshCollider";
                line += " | " + colShape + "(trigger=" + col.isTrigger + ",enabled=" + col.enabled + ",bounds=" + col.bounds.size + ")";
            }

            Renderer rend = go.GetComponent<Renderer>();
            if (rend != null)
            {
                totalRenderers++;
                int matCount = rend.sharedMaterials != null ? rend.sharedMaterials.Length : 0;
                line += " | Renderer(材质数=" + matCount + ",阴影=" + rend.shadowCastingMode + ",接收阴影=" + rend.receiveShadows + ",bounds=" + rend.bounds.size + ")";
            }

            MeshFilter mf = go.GetComponent<MeshFilter>();
            if (mf != null && mf.sharedMesh != null)
            {
                int vc = mf.sharedMesh.vertexCount;
                int tc = mf.sharedMesh.triangles.Length / 3;
                totalVerts += vc;
                totalTris += tc;
                line += " | Mesh(顶点=" + vc + ",三角面=" + tc + ")";
            }

            SkinnedMeshRenderer smr = go.GetComponent<SkinnedMeshRenderer>();
            if (smr != null && smr.sharedMesh != null)
            {
                int vc = smr.sharedMesh.vertexCount;
                int tc = smr.sharedMesh.triangles.Length / 3;
                totalVerts += vc;
                totalTris += tc;
                line += " | SkinnedMesh(顶点=" + vc + ",三角面=" + tc + ")";
            }

            Light light = go.GetComponent<Light>();
            if (light != null)
            {
                totalLights++;
                line += " | Light(类型=" + light.type + ",强度=" + light.intensity + ",范围=" + light.range + ",阴影=" + light.shadows + ")";
            }

            AudioSource audio = go.GetComponent<AudioSource>();
            if (audio != null)
            {
                totalAudioSources++;
                line += " | AudioSource(clip=" + (audio.clip != null ? audio.clip.name : "无") + ",loop=" + audio.loop + ",playOnAwake=" + audio.playOnAwake + ",spatialBlend=" + audio.spatialBlend + ")";
            }

            Rigidbody rb = go.GetComponent<Rigidbody>();
            if (rb != null)
            {
                totalRigidbodies++;
                line += " | Rigidbody(mass=" + rb.mass + ",isKinematic=" + rb.isKinematic + ",useGravity=" + rb.useGravity + ")";
            }

            Camera childCam = go.GetComponent<Camera>();
            if (childCam != null)
            {
                totalCameras++;
                line += " | Camera(fov=" + childCam.fieldOfView + ",near=" + childCam.nearClipPlane + ",far=" + childCam.farClipPlane + ")";
            }

            Debug.Log(line);
        }

        Debug.Log(
            "[配置快照][门" + label + "] ---- 子物体清单结束：共 " + totalTransforms + " 个物体 | " +
            "Renderer=" + totalRenderers + " Collider=" + totalColliders + " Light=" + totalLights +
            " AudioSource=" + totalAudioSources + " Rigidbody=" + totalRigidbodies + " Camera=" + totalCameras +
            " | 总顶点≈" + totalVerts + " 总三角面≈" + totalTris + " ----"
        );
    }

    bool IsBodyInPortalXY(Transform portalPlane, Vector3 playerHead, Vector3 playerFeet, int shapeType)
    {
        Vector3 localHead = portalPlane.InverseTransformPoint(playerHead);
        Vector3 localFeet = portalPlane.InverseTransformPoint(playerFeet);

        bool headInXY = LocalPointInPortalRect(localHead, shapeType);
        bool feetInXY = LocalPointInPortalRect(localFeet, shapeType);

        return headInXY || feetInXY;
    }

    bool IsBodyInColliderZone(Transform portalPlane, Vector3 playerHead, Vector3 playerFeet, int shapeType)
    {
        Vector3 localHead = portalPlane.InverseTransformPoint(playerHead);
        Vector3 localFeet = portalPlane.InverseTransformPoint(playerFeet);

        float headZ = localHead.z;
        float feetZ = localFeet.z;

        float bodyMinZ = Mathf.Min(headZ, feetZ);
        float bodyMaxZ = Mathf.Max(headZ, feetZ);

        // 性能优化：speedBuffer 每帧只在 LateUpdate 里算一次（见 cachedSpeedBufferThisFrame 注释），这里直接复用。
        float colliderThreshold = noClipDepth + colliderDisableBuffer + cachedSpeedBufferThisFrame;

        return IsBodyInPortalXY(portalPlane, playerHead, playerFeet, shapeType) &&
               bodyMinZ < colliderThreshold &&
               bodyMaxZ > -colliderThreshold;
    }

    bool IsHeadInsidePortalVisualVolume(Transform portalPlane, Vector3 playerHead, int shapeType)
    {
        if (portalPlane == null) return false;
        Vector3 localHead = portalPlane.InverseTransformPoint(playerHead);
        bool inRect = LocalPointInPortalRect(localHead, shapeType);
        bool inDepth = Mathf.Abs(localHead.z) < travellerTrackDepth;
        return inRect && inDepth;
    }

    /// 头部是否在传送门体积内（XY 在门框内，深度在 noClipDepth 范围内）。
    /// 用于判断是否需要跳过 oblique 裁剪和强制渲染。
    bool IsHeadInsidePortalVolume(Transform portalPlane, Vector3 playerHead, int shapeType)
    {
        if (portalPlane == null) return false;
        Vector3 localHead = LocalPointForPortal(portalPlane, playerHead);
        return LocalPointInPortalRect(localHead, shapeType) && Mathf.Abs(localHead.z) < noClipDepth;
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
        // 使用 teleportTriggerOffset 作为触发平面：
        // z > +offset → +1（门外，正侧）
        // z < -offset → -1（门外，负侧）
        // |z| <= offset → 0（门体积内）
        // offset = 0 时退化为旧版：z < 0 → -1，z >= 0 → +1。
        if (teleportTriggerOffset <= 0f) return z < 0f ? -1 : 1;
        if (z > teleportTriggerOffset) return 1;
        if (z < -teleportTriggerOffset) return -1;
        return 0;
    }

    /// 解析 A/B 门各自实际生效的形状（0=圆形，1=三角形，2=方框）。
    /// portalShapeA/B 未设置（PORTAL_SHAPE_UNSET=-1）或被填了非法值时，回退到旧版全局开关 useCircularPortalCheck，
    /// 保证升级旧场景/旧 Prefab 时行为不突变；只要玩家在 Inspector 手动填了 0/1/2 中任意合法值，就完全按该值生效。
    int ResolvePortalShape(bool isPortalA)
    {
        int shape = isPortalA ? portalShapeA : portalShapeB;
        if (shape == PORTAL_SHAPE_CIRCLE || shape == PORTAL_SHAPE_TRIANGLE || shape == PORTAL_SHAPE_BOX)
        {
            return shape;
        }
        return useCircularPortalCheck ? PORTAL_SHAPE_CIRCLE : PORTAL_SHAPE_BOX;
    }

    /// 三角形判定用的符号面积法（无三角函数）：判断 (px,py) 与三角形一条边 (ax,ay)-(bx,by) 的相对朝向。
    float SignPointToEdge(float px, float py, float ax, float ay, float bx, float by)
    {
        return (px - bx) * (ay - by) - (ax - bx) * (py - by);
    }

    /// 等腰三角形判定：顶点朝上 (0, hy)，底边在下方 y=-hy，两个底角为 (-hx,-hy) 与 (hx,-hy)。
    /// hx=hy 时不是正三角形；若要精确等边三角形，请把 portalTriggerHeight 设为 portalTriggerWidth * 0.8660254（sqrt(3)/2）。
    /// 使用符号面积法，不依赖三角函数，且不关心三角形绕向（同时检查正负号）。
    bool PointInPortalTriangle(float x, float y, float hx, float hy)
    {
        float ax = 0f, ay = hy;
        float bx = -hx, by = -hy;
        float cx = hx, cy = -hy;

        float d1 = SignPointToEdge(x, y, ax, ay, bx, by);
        float d2 = SignPointToEdge(x, y, bx, by, cx, cy);
        float d3 = SignPointToEdge(x, y, cx, cy, ax, ay);

        bool hasNeg = (d1 < 0f) || (d2 < 0f) || (d3 < 0f);
        bool hasPos = (d1 > 0f) || (d2 > 0f) || (d3 > 0f);

        return !(hasNeg && hasPos);
    }

    bool LocalPointInPortalRect(Vector3 localPoint, int shapeType)
    {
        float hx = portalTriggerWidth * 0.5f;
        float hy = portalTriggerHeight * 0.5f;
        if (hx <= 0.0001f || hy <= 0.0001f) return false;

        if (shapeType == PORTAL_SHAPE_TRIANGLE)
        {
            return PointInPortalTriangle(localPoint.x, localPoint.y, hx, hy);
        }

        if (shapeType == PORTAL_SHAPE_CIRCLE)
        {
            // 圆形/椭圆判定 - 无三角函数
            float nx = localPoint.x / hx;
            float ny = localPoint.y / hy;
            return nx * nx + ny * ny <= 1f;
        }

        // PORTAL_SHAPE_BOX，以及任何异常值都兜底为方框（矩形）判定 - 原版
        return Mathf.Abs(localPoint.x) < hx && Mathf.Abs(localPoint.y) < hy;
    }

    void SetTravellerTracking(bool isPortalA, bool tracking, Vector3 previousLocal)
    {
        if (isPortalA)
        {
            travellerTrackingA = tracking;
            previousTravellerLocalA = previousLocal;
        }
        else
        {
            travellerTrackingB = tracking;
            previousTravellerLocalB = previousLocal;
        }
    }

    bool GetTravellerTracking(bool isPortalA)
    {
        return isPortalA ? travellerTrackingA : travellerTrackingB;
    }

    Vector3 GetPreviousTravellerLocal(bool isPortalA)
    {
        return isPortalA ? previousTravellerLocalA : previousTravellerLocalB;
    }

    void SetTeleportTrackingLocal(bool isPortalA, Vector3 previousLocal)
    {
        if (isPortalA) previousTeleportLocalA = previousLocal;
        else previousTeleportLocalB = previousLocal;
    }

    Vector3 GetPreviousTeleportLocal(bool isPortalA)
    {
        return isPortalA ? previousTeleportLocalA : previousTeleportLocalB;
    }

    bool IsFlatPortal(Transform portal)
    {
        if (portal == null) return false;
        return Mathf.Abs(Vector3.Dot(portal.forward, Vector3.up)) > flatPortalDotThreshold;
    }

    Vector3 TravellerLocalForPortal(Transform portal, Vector3 rootWorld, Vector3 headWorld)
    {
        if (!useRootAsTraveller)
        {
            return LocalPointForPortal(portal, headWorld);
        }

        Vector3 rootLocal = LocalPointForPortal(portal, rootWorld);
        if (!useHybridRootXYHeadZTraveller)
        {
            return rootLocal;
        }

        Vector3 headLocal = LocalPointForPortal(portal, headWorld);

        if (IsFlatPortal(portal))
        {
            // 地板/天花板：门面内 XY 用 root，穿越深度 Z 用 head。
            // 这样不会脚先传导致头卡天花板，也不会歪头改变门面内落点。
            return new Vector3(rootLocal.x, rootLocal.y, headLocal.z);
        }

        // 墙面：门面横向 X 用 root，门面高度 Y 用 head，穿越深度 Z 用 root。
        // 原因：VRCPlayerApi.GetPosition() 更像脚底/胶囊底部；若墙面门 localY 用 root，普通走门会因 y 太低而在门框外。
        // 但深度 Z 仍用 root，避免玩家只把头探过墙就触发整个人传送。
        return new Vector3(rootLocal.x, headLocal.y, rootLocal.z);
    }

    Vector3 TeleportPointLocalForPortal(Transform portal, Vector3 rootWorld, Vector3 headWorld)
    {
        if (!useRootAsTraveller)
        {
            return LocalPointForPortal(portal, headWorld);
        }

        Vector3 rootLocal = LocalPointForPortal(portal, rootWorld);
        if (!useHybridRootXYHeadZTraveller)
        {
            return rootLocal;
        }

        // 真实 TeleportTo 点始终是 root。hybrid 只改变“触发点”，不把 root 的真实 local 深度伪装成 head。
        // 地板/天花板按 head.z 触发（TravellerLocalForPortal 的 hybrid）。TeleportSebStyle 用 rootXY+headZ 混合点做映射，再按出口门类型决定 root 落点。
        return rootLocal;
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
        // 经典 halfTurn 模式下，速度应完全由同一套门到门矩阵处理。
        // 旧 snapping 是为“B门本体预翻转、映射里没有halfTurn”的历史模式兜底，继续启用会把地板/天花板动量再次翻错。
        if (useClassicHalfTurn) return localVel;
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


    void SetLayerOverrideState(bool isPortalA, bool active, int originalLayer, Collider col)
    {
        if (isPortalA)
        {
            layerOverrideAActive = active;
            originalLayerA = originalLayer;
            layerOverrideColliderA = col;
            colliderADisabled = active;
        }
        else
        {
            layerOverrideBActive = active;
            originalLayerB = originalLayer;
            layerOverrideColliderB = col;
            colliderBDisabled = active;
        }
    }

    bool GetLayerOverrideActive(bool isPortalA)
    {
        return isPortalA ? layerOverrideAActive : layerOverrideBActive;
    }

    int GetOriginalLayer(bool isPortalA)
    {
        return isPortalA ? originalLayerA : originalLayerB;
    }

    Collider GetLayerOverrideCollider(bool isPortalA)
    {
        return isPortalA ? layerOverrideColliderA : layerOverrideColliderB;
    }

    void ApplyPassThroughLayer(Collider markedCollider, bool isPortalA, bool sharedCollider, string portalName, string reason)
    {
        if (markedCollider == null) return;
        GameObject obj = markedCollider.gameObject;
        if (obj == null) return;

        bool alreadyActive = GetLayerOverrideActive(isPortalA);
        Collider oldCollider = GetLayerOverrideCollider(isPortalA);
        int rememberedLayer = GetOriginalLayer(isPortalA);

        if (!alreadyActive || oldCollider != markedCollider)
        {
            // 如果传送门重新打到了新物体，先尽量把旧物体恢复，避免旧物体永久停在 29。
            if (alreadyActive && oldCollider != null && oldCollider != markedCollider)
            {
                GameObject oldObj = oldCollider.gameObject;
                if (oldObj != null && rememberedLayer >= 0 && oldObj.layer == playerPassThroughLayer)
                {
                    oldObj.layer = rememberedLayer;
                }
            }

            int original = obj.layer;
            // 共享 Collider 时，后进入的一侧可能看到的已经是 29；这时沿用另一侧记录的原始 layer。
            if (original == playerPassThroughLayer)
            {
                if (isPortalA && layerOverrideBActive && layerOverrideColliderB == markedCollider && originalLayerB >= 0)
                {
                    original = originalLayerB;
                }
                else if (!isPortalA && layerOverrideAActive && layerOverrideColliderA == markedCollider && originalLayerA >= 0)
                {
                    original = originalLayerA;
                }
            }
            SetLayerOverrideState(isPortalA, true, original, markedCollider);
        }
        else
        {
            if (isPortalA) colliderADisabled = true;
            else colliderBDisabled = true;
        }

        if (sharedCollider)
        {
            colliderADisabled = true;
            colliderBDisabled = true;
        }

        // 不管原来是什么层（0/28/其他），只要不是已经在 passThrough 层，就强行切过去。
        // 原始层已经在上面 SetLayerOverrideState 时记录，Restore 时还原。
        if (obj.layer != playerPassThroughLayer)
        {
            int fromLayer = obj.layer;
            obj.layer = playerPassThroughLayer;
            if (debugLayerLog) TPLog("[L " + portalName + "] " + fromLayer + "->" + playerPassThroughLayer + " shared=" + sharedCollider + " reason=" + reason);
        }
    }

    void RestorePassThroughLayer(Collider markedCollider, bool isPortalA, bool sharedCollider, string portalName, string reason)
    {
        bool active = GetLayerOverrideActive(isPortalA);
        Collider storedCollider = GetLayerOverrideCollider(isPortalA);
        if (!active) return;
        // 优先恢复当初被本门切换的 Collider。传送门可能已经重新打到新物体，不能误恢复新物体、漏掉旧物体。
        if (storedCollider != null) markedCollider = storedCollider;
        if (markedCollider == null) return;

        GameObject obj = markedCollider.gameObject;
        if (obj == null) return;

        int restoreLayer = GetOriginalLayer(isPortalA);
        if (restoreLayer < 0) restoreLayer = solidCollisionLayer;

        if (obj.layer == playerPassThroughLayer)
        {
            obj.layer = restoreLayer;
            if (debugLayerLog) TPLog("[L " + portalName + "] " + playerPassThroughLayer + "->" + restoreLayer + " shared=" + sharedCollider + " reason=" + reason);
        }

        SetLayerOverrideState(isPortalA, false, -1, null);
        if (sharedCollider)
        {
            SetLayerOverrideState(true, false, -1, null);
            SetLayerOverrideState(false, false, -1, null);
        }
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
        int thisShapeType = ResolvePortalShape(isPortalA);
        int otherShapeType = ResolvePortalShape(!isPortalA);

        Vector3 localHeadForTrigger = portalPlane.InverseTransformPoint(playerHead);
        Vector3 localFeetForTrigger = portalPlane.InverseTransformPoint(playerFeet);

        float headZ = localHeadForTrigger.z;
        float feetZ = localFeetForTrigger.z;
        float bodyMinZ = Mathf.Min(headZ, feetZ);
        float bodyMaxZ = Mathf.Max(headZ, feetZ);

        bool headInXY = LocalPointInPortalRect(localHeadForTrigger, thisShapeType);
        bool feetInXY = LocalPointInPortalRect(localFeetForTrigger, thisShapeType);
        bool bodyInXY = headInXY || feetInXY;

        int currentBodySide;
        if (bodyMinZ > 0f) currentBodySide = 1;
        else if (bodyMaxZ < 0f) currentBodySide = -1;
        else currentBodySide = 0;

        // 性能优化：speedBuffer 每帧只在 LateUpdate 里算一次，这里直接复用，数值算法不变。
        float colliderThreshold = noClipDepth + colliderDisableBuffer + cachedSpeedBufferThisFrame;
        bool bodyInColliderZone = bodyInXY && (bodyMinZ < colliderThreshold && bodyMaxZ > -colliderThreshold);
        bool otherBodyInColliderZone = false;
        if (otherPortalPlane != null)
        {
            otherBodyInColliderZone = IsBodyInColliderZone(otherPortalPlane, playerHead, playerFeet, otherShapeType);
        }

        string portalName = isPortalA ? "A" : "B";
        string otherName = isPortalA ? "B" : "A";

        if (debugTeleportVerbose && Time.frameCount % debugLogIntervalFrames == 0)
        {
            if (bodyInXY || bodyInColliderZone || thisPortalState != 0 || GetTravellerTracking(isPortalA))
            {
                TPLog(
                    "[门检测] 门" + portalName +
                    " state=" + thisPortalState +
                    " otherState=" + otherPortalState +
                    " tracking=" + GetTravellerTracking(isPortalA) +
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
        // Layer 穿透控制（替代旧版关闭 Collider）
        // ============================================================

        if (portalGun != null)
        {
            Collider markedCollider = isPortalA ? portalGun.GetMarkedColliderA() : portalGun.GetMarkedColliderB();
            Collider otherMarkedCollider = isPortalA ? portalGun.GetMarkedColliderB() : portalGun.GetMarkedColliderA();

            bool sharedCollider = markedCollider != null && otherMarkedCollider != null && markedCollider == otherMarkedCollider;

            if (sharedCollider && protectSharedMarkedCollider && !warnedSharedCollider)
            {
                warnedSharedCollider = true;
                TPLog("[警告] A门和B门在同一个碰撞体上，已启用共享图层保护");
            }

            if (markedCollider != null)
            {
                if (bodyInColliderZone)
                {
                    ApplyPassThroughLayer(markedCollider, isPortalA, sharedCollider, portalName, "near");
                }
                else
                {
                    if (GetLayerOverrideActive(isPortalA) && thisPortalState == 0)
                    {
                        bool canRestore = true;

                        if (protectSharedMarkedCollider && sharedCollider)
                        {
                            if (otherPortalState != 0 || otherBodyInColliderZone)
                            {
                                canRestore = false;
                            }
                        }

                        if (canRestore)
                        {
                            RestorePassThroughLayer(markedCollider, isPortalA, sharedCollider, portalName, "far");
                        }
                        else if (debugTeleportVerbose && Time.frameCount % debugLogIntervalFrames == 0)
                        {
                            if (debugLayerLog) TPLog("[L hold " + portalName + "] other=" + otherName + " state=" + otherPortalState + " zone=" + otherBodyInColliderZone);
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
            // traveller 可以是：head、root、或 hybrid(root XY + head Z)。
            // hybrid 是默认推荐：歪头不会改变门面内XY，但地板/天花板门会等头部穿过深度Z后再传送。
            Vector3 currentTravellerLocal = TravellerLocalForPortal(portalPlane, playerFeet, playerHead);
            Vector3 currentTeleportLocal = TeleportPointLocalForPortal(portalPlane, playerFeet, playerHead);

            // 首次初始化：记录上一帧位置，不做传送判断
            if (!GetTravellerTracking(isPortalA))
            {
                SetTravellerTracking(isPortalA, true, currentTravellerLocal);
                SetTeleportTrackingLocal(isPortalA, currentTeleportLocal);
                int startSide = SideFromLocalZ(currentTravellerLocal.z);
                if (startSide != 0) lastBodySide = startSide;
                if (debugTeleportVerbose)
                {
                    TPLog("[开始追踪traveller] 门" + portalName + " mode=" + (useRootAsTraveller ? (useHybridRootXYHeadZTraveller ? "hybrid" : "root") : "head") + " local=" + currentTravellerLocal + " side=" + startSide);
                }
                return false;
            }

            Vector3 previousTravellerLocal = GetPreviousTravellerLocal(isPortalA);
            Vector3 previousTeleportLocal = GetPreviousTeleportLocal(isPortalA);

            int oldSide = SideFromLocalZ(previousTravellerLocal.z);
            int newSide = SideFromLocalZ(currentTravellerLocal.z);

            bool crossedPlane = false;
            Vector3 crossingLocal = Vector3.zero;
            float crossingT = 1f; // previous->current 线段上穿过门平面的时间比例；用于重建穿越瞬间速度

            // 1. 经典侧面变号检测：从门外（oldSide ≠ 0）穿越触发平面到门内或另一侧
            if (oldSide != 0 && oldSide != newSide)
            {
                float triggerZ = oldSide * teleportTriggerOffset;
                float denom = previousTravellerLocal.z - currentTravellerLocal.z;
                if (Mathf.Abs(denom) > 0.0001f)
                {
                    float t = (previousTravellerLocal.z - triggerZ) / denom;
                    crossingT = Mathf.Clamp01(t);
                    crossingLocal = Vector3.Lerp(previousTravellerLocal, currentTravellerLocal, crossingT);
                    crossedPlane = true;
                }
            }
            else
            {
                // 2. 扫掠补救：线段与 z=±triggerOffset 平面求交，防止高速隧穿漏检
                float prevZ = previousTravellerLocal.z;
                float currZ = currentTravellerLocal.z;
                float dz = currZ - prevZ;
                if (Mathf.Abs(dz) > 0.0001f)
                {
                    // 检查 z = +triggerOffset（从正侧进入）
                    float tPlus = (teleportTriggerOffset - prevZ) / dz;
                    if (tPlus >= 0f && tPlus <= 1f && prevZ > teleportTriggerOffset)
                    {
                        crossingT = Mathf.Clamp01(tPlus);
                        crossingLocal = Vector3.Lerp(previousTravellerLocal, currentTravellerLocal, crossingT);
                        if (LocalPointInPortalRect(crossingLocal, thisShapeType))
                        {
                            crossedPlane = true;
                            oldSide = 1;
                            newSide = currZ < -teleportTriggerOffset ? -1 : 0;
                        }
                    }
                    // 检查 z = -triggerOffset（从负侧进入）
                    if (!crossedPlane)
                    {
                        float tMinus = (-teleportTriggerOffset - prevZ) / dz;
                        if (tMinus >= 0f && tMinus <= 1f && prevZ < -teleportTriggerOffset)
                        {
                            crossingT = Mathf.Clamp01(tMinus);
                            crossingLocal = Vector3.Lerp(previousTravellerLocal, currentTravellerLocal, crossingT);
                            if (LocalPointInPortalRect(crossingLocal, thisShapeType))
                            {
                                crossedPlane = true;
                                oldSide = -1;
                                newSide = currZ > teleportTriggerOffset ? 1 : 0;
                            }
                        }
                    }
                }
            }

            bool crossedInsidePortalRect = crossedPlane && LocalPointInPortalRect(crossingLocal, thisShapeType);

            // 更新追踪位置 - 常开，不再用 travellerTrackDepth 关掉
            SetTravellerTracking(isPortalA, true, currentTravellerLocal);
            SetTeleportTrackingLocal(isPortalA, currentTeleportLocal);
            if (newSide != 0) lastBodySide = newSide;

            if (crossedInsidePortalRect)
            {
                teleportSeq++;
                if (debugTeleportCoreLog)
                {
                    TPLog(
                        "[T#" + teleportSeq + " " + portalName + ">" + otherName + "]" +
                        " z=" + previousTravellerLocal.z + "->" + currentTravellerLocal.z +
                        " t=" + crossingT +
                        " xy=(" + crossingLocal.x + "," + crossingLocal.y + ")"
                    );
                }

                SetTravellerTracking(isPortalA, false, currentTravellerLocal);

                // crossingLocal 是“触发用 traveller”的穿越点；crossingTeleportLocal 是真实 TeleportTo 点(root/head)在同一时刻的位置。
                // hybrid 模式下二者不同：触发点=rootXY+headZ，传送点=真实root。
                Vector3 crossingTeleportLocal = Vector3.Lerp(previousTeleportLocal, currentTeleportLocal, crossingT);

                if (isPortalA)
                    TeleportToB(playerFeet, playerHead, crossingLocal, crossingTeleportLocal, crossingT, oldSide, portalPlaneA, portalPlaneB, portalParentA, portalParentB);
                else
                    TeleportToA(playerFeet, playerHead, crossingLocal, crossingTeleportLocal, crossingT, oldSide, portalPlaneB, portalPlaneA, portalParentB, portalParentA);

                return true;
            }
            else if (crossedPlane && debugTeleportVerbose)
            {
                TPLog("[跨越平面但在门框外] 门" + portalName + " crossingLocal=" + crossingLocal);
            }
        }
        return false;
    }

    // ============================================================
    // 传送执行（更新：包含过渡）
    // ============================================================

    void TeleportToB(Vector3 playerRoot, Vector3 playerHead, Vector3 crossingLocal, Vector3 crossingTeleportLocal, float crossingT, int entryOldSide, Transform fromPlane, Transform toPlane, Transform fromParent, Transform toParent)
    {
        TeleportSebStyle(fromPlane, toPlane, fromParent, toParent, true, playerRoot, playerHead, crossingLocal, crossingTeleportLocal, crossingT, entryOldSide);
    }

    void TeleportToA(Vector3 playerRoot, Vector3 playerHead, Vector3 crossingLocal, Vector3 crossingTeleportLocal, float crossingT, int entryOldSide, Transform fromPlane, Transform toPlane, Transform fromParent, Transform toParent)
    {
        TeleportSebStyle(fromPlane, toPlane, fromParent, toParent, false, playerRoot, playerHead, crossingLocal, crossingTeleportLocal, crossingT, entryOldSide);
    }

    void TeleportSebStyle(Transform fromPlane, Transform toPlane, Transform fromParent, Transform toParent, bool fromAtoB, Vector3 playerRoot, Vector3 playerHead, Vector3 crossingLocal, Vector3 crossingTeleportLocal, float crossingT, int entryOldSide)
    {
        VRCPlayerApi.TrackingData headData = localPlayer.GetTrackingData(VRCPlayerApi.TrackingDataType.Head);
        Quaternion playerHeadRot = headData.rotation;
        Vector3 playerVel = localPlayer.GetVelocity();

        Quaternion halfTurn = LocalHalfTurn();

        // 1) 连续穿越物理：用 crossingT 还原“穿越瞬间”，再把本帧剩余时间在出口世界继续积分。
        // 这比直接映射当前帧 playerHead 更精确：当前帧 playerHead 包含了“穿门后仍在入口世界受重力”的位移。
        float postCrossDt = Mathf.Clamp01(1f - crossingT) * Time.deltaTime;
        Vector3 gravityAccel = Physics.gravity;
        if (localPlayer != null && localPlayer.IsValid())
        {
            gravityAccel *= localPlayer.GetGravityStrength();
        }
        Vector3 velAtCrossing = playerVel - gravityAccel * postCrossDt;

        Vector3 localVelAtCrossing = LocalDirForPortal(fromPlane, velAtCrossing);
        localVelAtCrossing = ApplyOptionalMomentumSnapping(fromPlane, toPlane, velAtCrossing, localVelAtCrossing);

        bool flatHybridTraveller = useRootAsTraveller && useHybridRootXYHeadZTraveller && IsFlatPortal(fromPlane);

        // flat hybrid：用 root XY（无漂移）+ head Z（正确穿越深度）构造混合映射点。
        //   旧版全用 crossingLocal（head 点）→ XY 有 headFromRoot 漂移。
        //   第一版全用 crossingTeleportLocal（root 点）→ Z 深度错误（root 比 head 早穿越 1.6m）。
        //   现在：XY 取 root（无漂移），Z 取 head（触发时的穿越深度 ≈ 0）。
        // 非 flat hybrid（墙面门 / 纯 root）：crossingLocal.z ≈ crossingTeleportLocal.z，直接用 root 即可。
        Vector3 mappedCrossingLocal;
        if (flatHybridTraveller)
        {
            mappedCrossingLocal = new Vector3(crossingTeleportLocal.x, crossingTeleportLocal.y, crossingLocal.z);
        }
        else
        {
            mappedCrossingLocal = crossingTeleportLocal;
        }
        if (useClassicHalfTurn)
        {
            mappedCrossingLocal = halfTurn * mappedCrossingLocal;
            localVelAtCrossing = halfTurn * localVelAtCrossing;
        }

        Vector3 crossingMappedPointAtExit = WorldPointFromPortal(toPlane, mappedCrossingLocal);
        Vector3 exitVelAtCrossing = WorldDirFromPortal(toPlane, localVelAtCrossing);
        Vector3 newMappedPointPos = crossingMappedPointAtExit + exitVelAtCrossing * postCrossDt + gravityAccel * (0.5f * postCrossDt * postCrossDt);

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
        Vector3 cameraHeadAfterTeleport;
        if (useRootAsTraveller)
        {
            Vector3 headFromRoot = playerHead - playerRoot;
            if (keepPlayerUpright)
            {
                headFromRoot = Quaternion.AngleAxis(diffY, Vector3.up) * headFromRoot;
            }
            else
            {
                Quaternion fullDelta = toPlane.rotation * Quaternion.Inverse(fromPlane.rotation);
                if (useClassicHalfTurn) fullDelta = toPlane.rotation * halfTurn * Quaternion.Inverse(fromPlane.rotation);
                headFromRoot = fullDelta * headFromRoot;
            }

            if (flatHybridTraveller)
            {
                // mappedCrossingLocal = rootXY + headZ → 混合点。
                // newMappedPointPos 是混合点的世界坐标：门面内位置来自 root（无漂移），深度来自 head。
                if (IsFlatPortal(toPlane))
                {
                    // 出口也是平面门：沿出口法线把深度从 head 调整到 root。
                    // headFromRoot 在出口法线方向的分量 = head 和 root 的深度差。
                    float hfrDepth = Vector3.Dot(headFromRoot, toPlane.forward);
                    newTeleportPos = newMappedPointPos - toPlane.forward * hfrDepth;
                    cameraHeadAfterTeleport = newTeleportPos + headFromRoot;
                }
                else
                {
                    // 出口是墙面门：混合点当作 head 点处理（旧行为），root = head - headFromRoot。
                    // 墙面门的 forward 是水平的，headFromRoot 在其方向的分量 ≈ 0，
                    // 所以 root 在出口门面表面，head 在 headFromRoot 偏移处。
                    cameraHeadAfterTeleport = newMappedPointPos;
                    newTeleportPos = cameraHeadAfterTeleport - headFromRoot;
                }
            }
            else
            {
                // 非 flat hybrid（墙面门 / 纯 root）：newMappedPointPos 就是 root 点。
                newTeleportPos = newMappedPointPos;
                cameraHeadAfterTeleport = newTeleportPos + headFromRoot;
            }
        }
        else if (useVRCTrackingRootTeleport)
        {
            // 旧头部模式：Portal 数学输出新 Head，再用当前 head->root 偏移反推 TeleportTo 点。
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
            newTeleportPos = newMappedPointPos + headToRoot;
            cameraHeadAfterTeleport = newMappedPointPos;
        }
        else
        {
            float playerHeight = playerHead.y - playerRoot.y;
            newTeleportPos = newMappedPointPos - new Vector3(0f, playerHeight, 0f);
            cameraHeadAfterTeleport = newMappedPointPos;
        }

        // 出口侧保险：正常情况下 halfTurn 后应该落在 entryOldSide 对应的出口侧。
        // 45度斜面/混合 traveller/CharacterController 时序误差偶尔会把点算到门背面，导致下一帧立刻反向传送。
        // 这里不是按速度推人，只在“错误侧或太贴门”时沿出口法线拉回到最小安全距离。
        float exitSideFix = 0f;
        if (enableExitSideCorrection)
        {
            int desiredExitSide = entryOldSide == 0 ? 1 : entryOldSide;
            Vector3 afterLocalForSide = TravellerLocalForPortal(toPlane, newTeleportPos, cameraHeadAfterTeleport);
            // 出口目标距离：取 exitSideMinDistance 和 teleportTriggerOffset 的较大值。
            // 这样传送后玩家落在出口门的外边缘（沉浸式：从一扇门的外缘进，从另一扇门的外缘出）。
            float exitTargetDist = Mathf.Max(Mathf.Abs(exitSideMinDistance), teleportTriggerOffset);
            float desiredZ = desiredExitSide * exitTargetDist;
            bool wrongSide = SideFromLocalZ(afterLocalForSide.z) != desiredExitSide;
            bool tooClose = Mathf.Abs(afterLocalForSide.z) < exitTargetDist;
            if (wrongSide || tooClose)
            {
                exitSideFix = desiredZ - afterLocalForSide.z;
                Vector3 fixOffset = toPlane.forward * exitSideFix;
                newTeleportPos += fixOffset;
                cameraHeadAfterTeleport += fixOffset;
            }
        }

        // 经典 Portal 模式：不再做按速度放大的出口微推。
        // 只保留上面的“错误侧保险”，避免来回鬼畜；正常落在正确侧时不注入位移。

        // 4) 速度：与上面的连续位置积分使用同一个 crossingT / velAtCrossing。
        Vector3 newVel = exitVelAtCrossing + gravityAccel * postCrossDt;

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
        pendingVelocityFrames = Mathf.Max(0, velocityReapplyFrames);

        // ============================================================
        // 后续处理
        // ============================================================

        if (fromAtoB)
        {
            // SebLague 风格：传送后从入口门移除 traveller，并加入出口门；出口门 previous 记录“真实传送后位置”。
            // 不再设置 portalState=2 冷却，也不再把 previousTravellerLocal 人为推到 ±0.4。
            // 这样不会向无限下坠循环注入额外位移/势能。
            portalStateA = 0;
            portalStateB = 0;

            Vector3 localToB_afterTeleport = TravellerLocalForPortal(portalPlaneB, newTeleportPos, cameraHeadAfterTeleport);
            Vector3 teleportLocalToB_afterTeleport = TeleportPointLocalForPortal(portalPlaneB, newTeleportPos, cameraHeadAfterTeleport);
            lastBodySideB = SideFromLocalZ(localToB_afterTeleport.z);
            SetTravellerTracking(true, false, TravellerLocalForPortal(portalPlaneA, playerRoot, playerHead));
            SetTeleportTrackingLocal(true, TeleportPointLocalForPortal(portalPlaneA, playerRoot, playerHead));
            SetTravellerTracking(false, true, localToB_afterTeleport);
            SetTeleportTrackingLocal(false, teleportLocalToB_afterTeleport);

            if (portalGun != null)
            {
                Collider markedB = portalGun.GetMarkedColliderB();
                Collider markedA = portalGun.GetMarkedColliderA();
                bool sharedCollider = markedA != null && markedB != null && markedA == markedB;
                ApplyPassThroughLayer(markedB, false, sharedCollider, "B", "afterTeleport");
            }

            if (debugTeleportCoreLog) TPLog("[OUT B " + (useRootAsTraveller ? (useHybridRootXYHeadZTraveller ? "hybrid" : "root") : "head") + "] z=" + localToB_afterTeleport.z + " vY=" + newVel.y + " yaw=" + diffY + " t=" + crossingT + " dt=" + postCrossDt + " fix=" + exitSideFix);
        }
        else
        {
            // SebLague 风格：传送后从入口门移除 traveller，并加入出口门；出口门 previous 记录“真实传送后位置”。
            // 不再设置 portalState=2 冷却，也不再把 previousTravellerLocal 人为推到 ±0.4。
            portalStateB = 0;
            portalStateA = 0;

            Vector3 localToA_afterTeleport = TravellerLocalForPortal(portalPlaneA, newTeleportPos, cameraHeadAfterTeleport);
            Vector3 teleportLocalToA_afterTeleport = TeleportPointLocalForPortal(portalPlaneA, newTeleportPos, cameraHeadAfterTeleport);
            lastBodySideA = SideFromLocalZ(localToA_afterTeleport.z);
            SetTravellerTracking(false, false, TravellerLocalForPortal(portalPlaneB, playerRoot, playerHead));
            SetTeleportTrackingLocal(false, TeleportPointLocalForPortal(portalPlaneB, playerRoot, playerHead));
            SetTravellerTracking(true, true, localToA_afterTeleport);
            SetTeleportTrackingLocal(true, teleportLocalToA_afterTeleport);

            if (portalGun != null)
            {
                Collider markedA = portalGun.GetMarkedColliderA();
                Collider markedB = portalGun.GetMarkedColliderB();
                bool sharedCollider = markedA != null && markedB != null && markedA == markedB;
                ApplyPassThroughLayer(markedA, true, sharedCollider, "A", "afterTeleport");
            }

            if (debugTeleportCoreLog) TPLog("[OUT A " + (useRootAsTraveller ? (useHybridRootXYHeadZTraveller ? "hybrid" : "root") : "head") + "] z=" + localToA_afterTeleport.z + " vY=" + newVel.y + " yaw=" + diffY + " t=" + crossingT + " dt=" + postCrossDt + " fix=" + exitSideFix);
        }

        UpdateCamerasNow(cameraHeadAfterTeleport, newPlayerRot);
    }

    void UpdateCamerasNow(Vector3 newPlayerHeadPos, Quaternion newPlayerRot)
    {
        Quaternion cameraHalfTurn = LocalHalfTurn();
        if (cameraA != null)
        {
            Vector3 localToB = portalParentB.InverseTransformPoint(newPlayerHeadPos);
            Quaternion localRotToB = Quaternion.Inverse(portalParentB.rotation) * newPlayerRot;
            if (useClassicHalfTurn)
            {
                localToB = cameraHalfTurn * localToB;
                localRotToB = cameraHalfTurn * localRotToB;
            }
            cameraA.transform.position = portalParentA.TransformPoint(localToB);
            cameraA.transform.rotation = portalParentA.rotation * localRotToB;
        }

        if (cameraB != null)
        {
            Vector3 localToA = portalParentA.InverseTransformPoint(newPlayerHeadPos);
            Quaternion localRotToA = Quaternion.Inverse(portalParentA.rotation) * newPlayerRot;
            if (useClassicHalfTurn)
            {
                localToA = cameraHalfTurn * localToA;
                localRotToA = cameraHalfTurn * localRotToA;
            }
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

        bool nearA = IsHeadInPortalOverlayZone(portalPlaneA, playerHead, ResolvePortalShape(true));
        bool nearB = IsHeadInPortalOverlayZone(portalPlaneB, playerHead, ResolvePortalShape(false));

        SetPortalZTest(portalMatA, nearA ? 8f : 4f);
        SetPortalZTest(portalMatB, nearB ? 8f : 4f);
    }

    bool IsHeadInPortalOverlayZone(Transform portalPlane, Vector3 playerHead, int shapeType)
    {
        if (portalPlane == null) return false;
        Vector3 localHead = portalPlane.InverseTransformPoint(playerHead);
        if (!LocalPointInPortalRect(localHead, shapeType)) return false;
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

        // 头在传送门体积内时：跳过 oblique 裁剪 + 强制渲染（Seb 风格直接算，不用可调阈值）。
        bool headInsideVolumeA = IsHeadInsidePortalVolume(portalPlaneA, viewerPos, ResolvePortalShape(true));
        bool headInsideVolumeB = IsHeadInsidePortalVolume(portalPlaneB, viewerPos, ResolvePortalShape(false));

        // A 门表面显示 B 侧视角：严格对应 Seb 中 thisPortal=B, linkedPortal=A。
        // 头在 A 门体积内 → 镜像相机贴近 B 门 → 跳过 B 门侧 oblique。
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
            isCameraBRendering || isTeleporting || !enableVisibilityOptimization || headInsideVolumeA,
            viewerPos,
            viewerRot,
            syncFOV,
            recursivePositionsA,
            recursiveRotationsB,
            headInsideVolumeA
        );

        // B 门表面显示 A 侧视角：严格对应 Seb 中 thisPortal=A, linkedPortal=B。
        // 头在 B 门体积内 → 镜像相机贴近 A 门 → 跳过 A 门侧 oblique。
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
            isCameraARendering || isTeleporting || !enableVisibilityOptimization || headInsideVolumeB,
            viewerPos,
            viewerRot,
            syncFOV,
            recursivePositionsB,
            recursiveRotationsB,
            headInsideVolumeB
        );

        if (debugRecursiveRenderLog && Time.frameCount % debugRecursiveLogIntervalFrames == 0)
        {
            TPLog("[递归渲染] A深度=" + recursiveDepthRenderedA + " BDepth=" + recursiveDepthRenderedB + " limit=" + recursiveRenderLimit);
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
        Quaternion[] rotations,
        bool skipAllOblique = false
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
            if (i > 0 && recursiveEarlyStop && !skipAllOblique)
            {
                // Seb 用 BoundsOverlap 判断 linked portal 在当前递归相机里是否还可见。
                // 这里用 WorldToViewportPoint 对门面四角做近似 bounds overlap，语义保持一致。
                // skipAllOblique 时也跳过 early stop：头在门里时相机太近，bounds overlap 可能误判。
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

            if (skipAllOblique)
            {
                // 头在传送门体积内：跳过 oblique，用正常投影矩阵。
                // 避免相机贴近裁剪面时法线翻转导致反向裁切 / 画面消失。
                portalCam.ResetProjectionMatrix();
            }
            else
            {
                ApplyObliqueClippingSebStyle(portalCam, thisPlane);
            }

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
            TPLog("[递归近裁剪] 序号=" + renderIndex + " start=" + startIndex + " forwardDst=" + forwardDst + " near=" + newNear + " cam=" + cam.name + " clip=" + clipPlane.name);
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
            DrawPortalGizmo(portalPlaneA, gizmoColorA, isCameraBRendering, portalStateA, colliderADisabled, lastBodySideA, ResolvePortalShape(true));

        if (portalPlaneB != null)
            DrawPortalGizmo(portalPlaneB, gizmoColorB, isCameraARendering, portalStateB, colliderBDisabled, lastBodySideB, ResolvePortalShape(false));

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

    /// 按 shapeType 取门框轮廓在局部 XY 平面上的顶点（闭合多边形，最后一点会自动连回第一点）。
    /// 圆形用 24 边多边形近似；三角形直接是三个顶点；方框是四个角。
    /// 仅用于 Gizmos 可视化，不参与实际判定（实际判定见 LocalPointInPortalRect，两者必须保持数学定义一致）。
    Vector3[] GetPortalShapeOutline2D(int shapeType, float hx, float hy)
    {
        if (shapeType == PORTAL_SHAPE_TRIANGLE)
        {
            return new Vector3[]
            {
                new Vector3(0f, hy, 0f),
                new Vector3(-hx, -hy, 0f),
                new Vector3(hx, -hy, 0f)
            };
        }

        if (shapeType == PORTAL_SHAPE_CIRCLE)
        {
            const int segments = 24;
            Vector3[] points = new Vector3[segments];
            for (int i = 0; i < segments; i++)
            {
                float angle = (float)i / segments * 360f * Mathf.Deg2Rad;
                points[i] = new Vector3(Mathf.Cos(angle) * hx, Mathf.Sin(angle) * hy, 0f);
            }
            return points;
        }

        // PORTAL_SHAPE_BOX 及兜底
        return new Vector3[]
        {
            new Vector3(-hx, -hy, 0f),
            new Vector3(-hx, hy, 0f),
            new Vector3(hx, hy, 0f),
            new Vector3(hx, -hy, 0f)
        };
    }

    /// 在 Gizmos.matrix 已设为门局部坐标系的前提下，于 z=zOffset 平面画出该形状的闭合线框。
    void DrawShapeOutlineAtZ(int shapeType, float hx, float hy, float zOffset)
    {
        Vector3[] outline = GetPortalShapeOutline2D(shapeType, hx, hy);
        int count = outline.Length;
        for (int i = 0; i < count; i++)
        {
            Vector3 a = outline[i];
            Vector3 b = outline[(i + 1) % count];
            a.z = zOffset;
            b.z = zOffset;
            Gizmos.DrawLine(a, b);
        }
    }

    /// 用形状轮廓近似画一个“棱柱体”线框：前后两个截面 + 连接四角/多边形顶点的纵向棱线。
    /// 圆形/三角形没有 Gizmos.DrawWireCube 对应的现成 API，所以统一走这条路径，方框也复用它以保证三种形状视觉逻辑一致。
    void DrawShapePrismWire(int shapeType, float hx, float hy, float halfDepth)
    {
        DrawShapeOutlineAtZ(shapeType, hx, hy, -halfDepth);
        DrawShapeOutlineAtZ(shapeType, hx, hy, halfDepth);

        Vector3[] outline = GetPortalShapeOutline2D(shapeType, hx, hy);
        for (int i = 0; i < outline.Length; i++)
        {
            Vector3 front = outline[i]; front.z = -halfDepth;
            Vector3 back = outline[i]; back.z = halfDepth;
            Gizmos.DrawLine(front, back);
        }
    }

    void DrawPortalGizmo(Transform portal, Color color, bool isActive, int state, bool colliderDisabled, int bodySide, int shapeType)
    {
        Matrix4x4 oldMatrix = Gizmos.matrix;
        Gizmos.matrix = portal.localToWorldMatrix;

        float hx = portalTriggerWidth * 0.5f;
        float hy = portalTriggerHeight * 0.5f;

        Gizmos.color = color;
        DrawShapePrismWire(shapeType, hx, hy, noClipDepth);

        // 用若干层半透明切片近似“体积填充”效果，方框/圆形/三角形统一走这条路径，
        // 不再依赖 Gizmos.DrawCube（只支持矩形），保证三种形状在 Scene 视图里的视觉逻辑一致。
        Color fillColor = color;
        fillColor.a = isActive ? 0.2f : 0.05f;
        Gizmos.color = fillColor;
        const int fillSlices = 5;
        for (int i = 0; i < fillSlices; i++)
        {
            float t = fillSlices <= 1 ? 0f : ((float)i / (fillSlices - 1) * 2f - 1f);
            DrawShapeOutlineAtZ(shapeType, hx, hy, t * noClipDepth);
        }

        Gizmos.color = colliderDisabled ? new Color(1f, 0f, 0f, 0.15f) : new Color(0f, 1f, 0f, 0.1f);
        DrawShapePrismWire(shapeType, hx, hy, noClipDepth + colliderDisableBuffer);

        // 传送触发平面：黄色线框，在 z = ±teleportTriggerOffset 处。
        if (teleportTriggerOffset > 0f)
        {
            Gizmos.color = new Color(1f, 1f, 0f, 0.6f);
            DrawShapeOutlineAtZ(shapeType, hx, hy, teleportTriggerOffset);
            DrawShapeOutlineAtZ(shapeType, hx, hy, -teleportTriggerOffset);
        }

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

    // ============================================================
    // 刚体传送核心（复用玩家逻辑 + shape 支持 + Layer 切换 + 抓取支持）
    // ============================================================

    private void ProcessRigidbodyTravellers()
    {
        if (portalPlaneA == null || portalPlaneB == null) return;

        // A门检测
        ProcessRigidbodyForPortal(true);
        // B门检测
        ProcessRigidbodyForPortal(false);
    }

    private void ProcessRigidbodyForPortal(bool isPortalA)
    {
        Transform thisPlane = isPortalA ? portalPlaneA : portalPlaneB;
        Transform otherPlane = isPortalA ? portalPlaneB : portalPlaneA;
        Transform thisParent = isPortalA ? portalParentA : portalParentB;
        Transform otherParent = isPortalA ? portalParentB : portalParentA;
        int thisShape = ResolvePortalShape(isPortalA);

        if (thisPlane == null || otherPlane == null) return;

        float hx = portalTriggerWidth * 0.5f;
        float hy = portalTriggerHeight * 0.5f;
        float depth = noClipDepth + rbTriggerDepthExtension;

        Vector3 center = thisPlane.position;
        Vector3 halfExtents = new Vector3(hx, hy, depth * 0.5f);
        Quaternion orient = thisPlane.rotation;

        Collider[] cols = Physics.OverlapBox(center, halfExtents, orient, ~0, QueryTriggerInteraction.Collide);

        foreach (Collider col in cols)
        {
            if (col == null) continue;
            Rigidbody rb = col.attachedRigidbody;
            if (rb == null || rb.isKinematic) continue;

            // 过滤自己抓取的刚体（如果不允许持物传送则跳过）
            if (!allowHeldRigidbodyTeleport && portalGun != null && portalGun.GetHeldRigidbody() == rb)
                continue;

            Vector3 localPos = thisPlane.InverseTransformPoint(rb.position);
            bool inPortalRect = LocalPointInPortalRect(localPos, thisShape);
            bool inDepth = Mathf.Abs(localPos.z) < (noClipDepth + rbTriggerDepthExtension);

            // 只要刚体在门框范围内，就强制切换到 rigidbodyPassThroughLayer（13）
            if (inPortalRect && inDepth)
            {
                // 记录原始图层（只记录一次）
                bool alreadyTracked = false;
                for (int k = 0; k < trackedRBCount; k++)
                {
                    if (trackedRigidbodies[k] == rb)
                    {
                        alreadyTracked = true;
                        break;
                    }
                }

                if (!alreadyTracked && trackedRBCount < MAX_TRACKED_RBS)
                {
                    trackedRigidbodies[trackedRBCount] = rb;
                    rbPrevPosList[trackedRBCount] = rb.position;
                    rbOriginalLayerList[trackedRBCount] = rb.gameObject.layer;
                    trackedRBCount++;
                }

                if (rb.gameObject.layer != rigidbodyPassThroughLayer)
                {
                    rb.gameObject.layer = rigidbodyPassThroughLayer;
                }
            }

            // 【新增】持续检测：只要刚体中心在门框内，就尝试触发传送（解决中心接触不触发的问题）
            if (inPortalRect && inDepth)
            {
                // 直接执行传送（不依赖穿越检测）
                TeleportRigidbody(rb, thisPlane, otherPlane, thisParent, otherParent, isPortalA, -1);
                continue;
            }

            if (!inPortalRect) continue;

            // 穿越检测 - 100% Udon 兼容（固定数组 + 计数器）
            int trackedIndex = -1;
            for (int i = 0; i < trackedRBCount; i++)
            {
                if (trackedRigidbodies[i] == rb)
                {
                    trackedIndex = i;
                    break;
                }
            }

            if (trackedIndex == -1)
            {
                // 首次见到这个刚体，记录位置（数组版）
                if (trackedRBCount < MAX_TRACKED_RBS)
                {
                    trackedRigidbodies[trackedRBCount] = rb;
                    rbPrevPosList[trackedRBCount] = rb.position;
                    rbOriginalLayerList[trackedRBCount] = rb.gameObject.layer;
                    trackedRBCount++;
                }
                continue;
            }

            Vector3 prevPos = rbPrevPosList[trackedIndex];

            Vector3 localPrev = thisPlane.InverseTransformPoint(prevPos);
            float prevZ = localPrev.z;
            float currZ = localPos.z;

            bool crossed = (prevZ > 0 && currZ <= 0) || (prevZ < 0 && currZ >= 0);
            if (!crossed || !LocalPointInPortalRect(localPos, thisShape)) continue;

            // 执行传送
            TeleportRigidbody(rb, thisPlane, otherPlane, thisParent, otherParent, isPortalA, trackedIndex);

            // 更新 prev
            rbPrevPosList[trackedIndex] = rb.position;
        }

        // 清理已销毁/无效的刚体 + 还原离开区域的刚体图层
        for (int i = trackedRBCount - 1; i >= 0; i--)
        {
            Rigidbody rb = trackedRigidbodies[i];
            if (rb == null)
            {
                // 数组压缩
                for (int j = i; j < trackedRBCount - 1; j++)
                {
                    trackedRigidbodies[j] = trackedRigidbodies[j + 1];
                    rbPrevPosList[j] = rbPrevPosList[j + 1];
                    rbOriginalLayerList[j] = rbOriginalLayerList[j + 1];
                }
                trackedRigidbodies[trackedRBCount - 1] = null;
                trackedRBCount--;
                continue;
            }

            // 检查该刚体是否已经离开门区域
            Vector3 localPos = thisPlane.InverseTransformPoint(rb.position);
            bool stillInRect = LocalPointInPortalRect(localPos, thisShape);
            bool stillInDepth = Mathf.Abs(localPos.z) < (noClipDepth + rbTriggerDepthExtension);

            if (!stillInRect || !stillInDepth)
            {
                // 离开区域 → 还原图层（更可靠版本）
                if (restoreRigidbodyLayerOnExit && i < trackedRBCount)
                {
                    int originalLayer = rbOriginalLayerList[i];
                    if (originalLayer >= 0 && rb.gameObject.layer == rigidbodyPassThroughLayer)
                    {
                        rb.gameObject.layer = originalLayer;
                    }
                }

                // 从追踪列表中移除
                for (int j = i; j < trackedRBCount - 1; j++)
                {
                    trackedRigidbodies[j] = trackedRigidbodies[j + 1];
                    rbPrevPosList[j] = rbPrevPosList[j + 1];
                    rbOriginalLayerList[j] = rbOriginalLayerList[j + 1];
                }
                trackedRigidbodies[trackedRBCount - 1] = null;
                trackedRBCount--;
            }
        }
    }

    private void TeleportRigidbody(Rigidbody rb, Transform fromPlane, Transform toPlane, Transform fromParent, Transform toParent, bool fromAtoB, int trackedIndex = -1)
    {
        if (rb == null) return;

        // 复用玩家映射逻辑
        Vector3 localPos = fromPlane.InverseTransformPoint(rb.position);
        Quaternion localRot = Quaternion.Inverse(fromPlane.rotation) * rb.rotation;

        // 经典半转
        if (useClassicHalfTurn)
        {
            localRot = LocalHalfTurn(localRot);
            localPos = LocalHalfTurn(localPos);
        }

        Vector3 worldPos = toPlane.TransformPoint(localPos);
        Quaternion worldRot = toPlane.rotation * localRot;

        // ============================================================
        // 刚体物理映射（参考 SebLague Portals 实现）
        // ============================================================

        // 1. 线性速度映射
        Vector3 localVel = fromPlane.InverseTransformDirection(rb.velocity);
        if (useClassicHalfTurn) localVel = LocalHalfTurn(localVel);
        Vector3 newVel = toPlane.TransformDirection(localVel);

        // 2. 角速度映射（关键！让翻滚的物体自然）
        Vector3 localAngularVel = fromPlane.InverseTransformDirection(rb.angularVelocity);
        if (useClassicHalfTurn) localAngularVel = LocalHalfTurn(localAngularVel);
        Vector3 newAngularVel = toPlane.TransformDirection(localAngularVel);

        // 3. 出口侧保险（加强版，防止掉虚空）
        if (enableExitSideCorrection)
        {
            Vector3 localAfter = toPlane.InverseTransformPoint(worldPos);
            float minDist = Mathf.Max(exitSideMinDistance, 0.12f);
            int desiredSide = 1;
            float desiredZ = desiredSide * minDist;

            if (Mathf.Abs(localAfter.z) < minDist || (localAfter.z * desiredSide < 0))
            {
                Vector3 fix = toPlane.forward * (desiredZ - localAfter.z);
                worldPos += fix;
            }
        }

        // Layer 切换（进入穿透层）
        int origLayer = rb.gameObject.layer;
        if (trackedIndex >= 0 && trackedIndex < trackedRBCount)
        {
            rbOriginalLayerList[trackedIndex] = origLayer;
        }
        rb.gameObject.layer = rigidbodyPassThroughLayer;

        // 4. 应用所有物理状态（位置 + 旋转 + 线速度 + 角速度）
        rb.position = worldPos;
        rb.rotation = worldRot;
        rb.velocity = newVel;
        rb.angularVelocity = newAngularVel;

        // 如果是抓取状态，通知枪更新 held offset（无缝跨门）
        if (portalGun != null && portalGun.GetHeldRigidbody() == rb && allowHeldRigidbodyTeleport)
        {
            portalGun.UpdateHeldAfterTeleport(worldPos, worldRot);
        }

        if (debugTeleportLog)
        {
            Debug.Log("[刚体传送] " + rb.name + " 从 " + (fromAtoB ? "A" : "B") + " -> " + (fromAtoB ? "B" : "A"));
        }
    }

    // 复用已有的 LocalHalfTurn（如果不存在就定义）
    private Vector3 LocalHalfTurn(Vector3 v)
    {
        return new Vector3(-v.x, v.y, -v.z);
    }

    private Quaternion LocalHalfTurn(Quaternion q)
    {
        return Quaternion.Euler(0, 180, 0) * q;
    }
}
