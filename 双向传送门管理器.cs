using UdonSharp;
using UnityEngine;
using VRC.SDKBase;

[UdonBehaviourSyncMode(BehaviourSyncMode.None)]
public class 双向传送门管理器 : UdonSharpBehaviour
{
    // ============================================================
    // 基础配置
    // ============================================================

    [Header("════════════ 基础设置 ════════════")]
    [Min(0.001f)]
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

    [Tooltip("玩家进入传送门前后多远开始临时切换图层。实际判定深度 = noClipDepth + colliderDisableBuffer + 速度缓冲。" +
             "速度缓冲 = 玩家速度×物理步长×2（八轮起不再钳死1.5米，上限50米仅防异常值），保证任意速度的玩家在到达墙面前一帧，墙已切到穿透层。想让切换更早/更晚，优先微调这个值。")]
    public float colliderDisableBuffer = 0.15f;

    [Tooltip("被传送枪标记的墙/地板/屏蔽碰撞体的原始实体层。当前推荐 17 = Walkthrough：挡刚体/物品，不挡玩家。")]
    public int solidCollisionLayer = 17;

    [Tooltip("玩家靠近传送门时，墙/地板/屏蔽碰撞体临时切换到的 Layer。当前推荐同样用 17 = Walkthrough：挡刚体/物品，不挡玩家。")]
    public int playerPassThroughLayer = 17;

    [Header("════════════ 传送门Clip Volume 批量穿透 ════════════")]
    [Tooltip("A门 Clip Volume：拖入一个 BoxCollider（可挂在门自身/子物体上，可设为Trigger）。" +
             "玩家靠近A门时，该Box体积内所有【非刚体、非Trigger、非传送门自身hierarchy】的静态Collider统一切到playerPassThroughLayer，离开后自动还原。" +
             "用于解决传送门打在多面墙/地板穿模叠在一起时，只切一面墙不够、其他穿模墙面仍挡住玩家的问题。" +
             "使用方法：把你已有的BoxCollider拖到这里，再在Unity里把它的Z轴size调大到足以包住穿模叠在一起的墙段（建议0.5~1.0米）。")]
    public BoxCollider clipVolumeColliderA;

    [Tooltip("B门 Clip Volume，规则同A门")]
    public BoxCollider clipVolumeColliderB;

    [Tooltip("Clip Volume 穿透逻辑开关。关闭则完全不做批量切换，回退到只切单markedCollider的旧行为。")]
    public bool enableClipVolumePassThrough = true;

    [Header("════════════ 粒子传送 ════════════")]
    [Tooltip("启用粒子传送：已注册粒子系统里穿过A/B门平面的粒子会被映射到另一侧（位置+速度同门映射）。" +
             "注册来源只有自动收集（root扫描/放置发现/收集根），手动白名单已移除。" +
             "检测时自动读主模块 Simulation Space：World(0)按世界坐标直通；Local(1)按系统Transform局部↔世界转换；" +
             "Custom(2)按Local处理（假设customSimulationSpace=系统自身Transform，Console会警告一次）。")]
    public bool enableParticleTeleport = true;

    [Tooltip("粒子缓冲初始大小。六轮升级：活粒子顶满缓冲时会自动翻倍扩容并补读（扩容帧全体粒子按'首见'规则处理，后侧兜底接管，零漏检），此值只是初始分配，不再构成上限。")]
    [Range(32, 2048)]
    public int particleTeleportBufferSize = 1024;

    [Tooltip("粒子系统【原点】离两扇门都超过这个距离时整体跳过（性能闸门，按系统原点算不是按粒子位置）。远处系统喷射的粒子飞进门区也不会被处理——有这种特效就把这个值加大。")]
    public float particleTeleportMaxDistance = 100f;

    [Tooltip("自动收集粒子系统：开启后有三路来源——" +
             "1) 放置时发现：传送枪放门成功时/Start时，用OverlapSphere枚举门周围半径内的碰撞体，顺藤摸瓜找它们层级里的粒子系统自动注册（免手拖，推荐主用）；" +
             "2) 本物体 transform.root 子树定时扫描（预制件自带特效零配置生效）；" +
             "3) 下方'收集根'物体子树（以上都覆盖不到时的兜底，可选）。" +
             "诚实边界：Udon 无法枚举没有碰撞体的物体，完全独立且无碰撞体的粒子系统仍需白名单/收集根。")]
    public bool autoDiscoverParticleSystems = true;

    [Tooltip("收集根（可选）：特效物件没挂在传送门同一根节点下、自己又没有碰撞体时，把它们的容器物体拖进来。")]
    public GameObject[] particleDiscoveryRoots;

    [Tooltip("粒子传送排除根（黑名单）：这些物体（含其子层级）下的粒子系统不参与检测/传送。\n" +
             "传送枪自身层级对【World空间】系统自动排除（枪口火花等被传送会鬼畜）；枪载【Local空间】系统（如激光）放行参与传送。" +
             "其他不想被传送的特效拖进来即可。")]
    public Transform[] particleTeleportExclusionRoots;

    [Tooltip("自动发现半径：放置时发现(OverlapSphere)的半径，也是root扫描的距离过滤。门放哪扫到哪。")]
    public float particleDiscoveryRadius = 100f;

    [Tooltip("自动收集刷新间隔（秒）。门可以被传送枪移动，定期重新扫描保证参与列表跟上。")]
    public float particleDiscoveryRefreshInterval = 5f;

    [Tooltip("粒子传送平面外推距离：检测平面沿门法线向外推这么多。带碰撞粒子有两层保护：" +
             "1) 此值大于粒子碰撞半径(≈粒子尺寸×Collision模块Radius Scale的一半)时，粒子在撞墙前先穿过检测面被规则1收走（视觉最干净）；" +
             "2) 没调那么大也没关系——粒子被门的墙弹回时会被规则4'反弹捕获'传送（墙边最多可见一帧反弹）。" +
             "想要'撞墙前就被吸进门'的视觉就把此值调大到超过粒子碰撞半径（Collision模块勾Visualize Bounds可看见碰撞球）。")]
    public float particleTeleportPlaneOffset = 0.05f;

    [Tooltip("后侧追补深度上限（米）。二十一轮语义修正：门洞捕获=【门框内】且【门平面后侧】，与深度无关——" +
             "深度窗是历史遗留（漂移入框在高速时发生得很深，任何有限窗都追不上，你实测速度1000已复现）。" +
             "<=0 = 不限制（推荐，默认）：框内+门后的粒子一律传送，任意速度零漏；" +
             ">0 = 只在深度<=此值内补抓（旧场景兼容/想收紧捕获体积时用，老场景序列化值0.5需手动改回0）。" +
             "框内是硬约束：框外的墙后粒子永不误抓（不会从B门墙上凭空射出粒子）。")]
    public float particleTeleportRetroWindow = -1f;

    [Tooltip("粒子传送诊断日志：每60帧输出一次'检测了多少粒子/传送了多少次'。粒子隧穿不传送时用它定位卡在哪一环：检测数=0说明系统没被读取（Simulation Space不是World/系统没播放/距离闸门）；检测数>0但传送=0说明穿越判定不命中（空间语义/门框范围/方向）。")]
    public bool debugParticleTeleportLog = false;

    [Tooltip("门框判定按粒子尺寸外扩（十八轮）：粒子是面片不是点，把门框判定外扩'每颗粒子startSize的一半'（上限1m），" +
             "粒子身体压到门框边缘也算进洞——就是'按粒子长宽判断'的便宜实现（碰撞模块的Radius Scale在Udon读不到，只能用出生尺寸近似）。" +
             "规则1/2/3/4 的框内判定统一生效；对拉伸光束，startSize就是光束宽度。")]
    public bool particleUseSizeInflatedCheck = true;

    private int particleDebugFrameCounter = 0;
    private int particleDebugTestedCount = 0;
    private int particleDebugTeleportCount = 0;
    private int particleDebugStuckCount = 0;
    private int particleDebugStuckSamples = 0;
    private int particleDebugTruncatedSystems = 0;
    private int particleDebugBounceCount = 0;
    // 十八轮：每帧事件日志限流（疯狂刷屏问题）——每60帧窗口打印前25条+之后每100条1条，其余计数进汇总
    private int particleDebugEventLogged = 0;
    private int particleDebugEventSkipped = 0;

    // 二十轮：帧内去重列表（见 ProcessParticleTeleports 头注）
    private ParticleSystem[] processedSystemsThisFrame = new ParticleSystem[64];
    private int processedSystemsThisFrameCount = 0;

    private ParticleSystem.Particle[] particleTeleportBuffer;
    // 十六轮：配对历史改为【按系统槽位隔离】的固定池（PARTICLE_PAIRING_SLOTS 个系统 ×
    // PARTICLE_PAIRING_STRIDE 颗粒子，平铺成一维数组，索引 = 系统槽位*STRIDE + 粒子下标）。
    // 修复十五轮审计发现的架构缺陷：旧版所有系统共用一套配对数组，帧内后处理的系统覆盖
    // 先处理系统的记录 → 下一帧种子对不上 → 配对全体失效 → 规则4反弹捕获永不触发、
    // 规则1退化成反推。隔离后每个系统只读自己槽位的记录。
    // 诚实降级：系统超过16个、或单系统活粒子超过1024（STRIDE）时，超出的部分按"未配对"
    // 处理（出生/一帧反推线段兜底，恒速直线光束仍然精确），不扩池不报错。
    private const int PARTICLE_PAIRING_SLOTS = 16;
    private const int PARTICLE_PAIRING_STRIDE = 1024;
    private ParticleSystem[] particlePairingSlotSystems = new ParticleSystem[PARTICLE_PAIRING_SLOTS];

    // randomSeed 是粒子出生时分配、终生不变的稳定ID，是跨帧识别"同一颗粒子"的可靠钥匙
    // （旧的"归属系统"配对太弱：同系统里寿命相近的两颗粒子会被误认成同一颗）。
    // GetParticles 的槽位顺序在有粒子死亡时会洗牌（Unity官方论坛实证），配对失败=槽位换了粒子
    // → 走"首见"路径（出生反推 + 首见后侧兜底），保证零漏检。
    // particlePrevValid 显式标记"本槽位有记录"（替代旧的 seed!=0 技巧，seed 恰好为 0 的粒子也能正常配对）。
    private Vector3[] particlePrevPositions = new Vector3[PARTICLE_PAIRING_SLOTS * PARTICLE_PAIRING_STRIDE];
    private uint[] particlePrevSeeds = new uint[PARTICLE_PAIRING_SLOTS * PARTICLE_PAIRING_STRIDE];
    private bool[] particlePrevValid = new bool[PARTICLE_PAIRING_SLOTS * PARTICLE_PAIRING_STRIDE];
    // 上帧实测速度：规则4"反弹捕获"用——配对粒子的门法向速度一帧内由"朝门"反转为"离门"，
    // 说明它被门位置的墙体碰撞体弹回（碰撞粒子在穿越检测面前就反弹时的唯一可观测信号）。
    private Vector3[] particlePrevVelocities = new Vector3[PARTICLE_PAIRING_SLOTS * PARTICLE_PAIRING_STRIDE];
    // 二十一论：本种子是否已被传送过（诊断用：区分"漏捕的入口粒子"与"已传送后飞过墙区的粒子"）。
    // 传送成功时置true，种子换人时随 paired 判定自然失效（未配对粒子的该位读作false）。
    private bool[] particlePrevTeleported = new bool[PARTICLE_PAIRING_SLOTS * PARTICLE_PAIRING_STRIDE];

    // 传送后防回弹设计（十六轮终稿，用户裁决）：【不做时间型免疫帧】——Portal 语义要求
    // 激光粒子可短时间内反复套娃穿门（A→B→A…），帧数免疫会吞掉第三次合法进洞、制造
    // 新的"穿墙"。防回弹靠三条状态型保证：
    //   1) 出口落点前推：沿出口光束方向推，法向分量恰好=offset+0.01（二十五轮起——
    //      最小安全值：必须越过出口检测面(z=offset)，否则下帧线段会再穿检测面被规则1
    //      回抓=乒乓；近垂直入射时贴门面0.06m，可见死区最小）；
    //   2) 半转映射后出口速度必然背离出口门平面（几何不变量，与速度大小无关）；
    //   3) 规则本身状态型：规则1要求真实穿越、规则3要求未配对、规则4要求速度反转。
    // 若实测仍有"反复传送"，用 [粒子传送][事件] 日志定位是具体哪条规则在重抓，修该规则。
    // （玩家侧同哲学：teleportBlockFrames 默认0关闭，实际守卫是出口侧修正+lastBodySide。）

    // 规则4v2 反弹点重建的 z 容差带（门局部坐标）：反弹发生在门面所在墙面上，碰撞球心
    // 距墙面 = 粒子碰撞半径（Unity 官方文档：半径≈粒子尺寸×Collision模块Radius Scale）。
    // 容差覆盖常见碰撞半径与墙厚：反弹点重建的 z 落在此带内+门框内=在门面上弹的。
    private const float PARTICLE_BOUNCE_Z_MIN = -0.4f;
    private const float PARTICLE_BOUNCE_Z_MAX = 1.2f;

    // Custom 空间警告去重（每系统只警告一次，最多记8个）
    private ParticleSystem[] customSpaceWarnedSystems = new ParticleSystem[8];
    private int customSpaceWarnedCount = 0;
    private ParticleSystem[] discoveredParticleSystems;
    private float particleDiscoveryTimer = 0f;

    // ══════════ 粒子传送 · 新架构字段（独立脚本 粒子测试.cs 移植，全部经实测）══════════
    [Header("粒子传送 · 身份证通道 / 亚帧 / 预测")]
    [Tooltip("把每颗粒子的上一帧位置写进它自己的 Particle.axisOfRotation（随粒子本体走，不受 GetParticles 下标洗牌影响）。\n实测下标洗牌率约 2%/帧，旧的下标配对会因此产生虚构反推线段与反向误传。")]
    public bool useParticlePassport = true;
    [Tooltip("启动时调用 AllocateAxisOfRotationAttribute()。实测非必需，仅作保险。")]
    public bool allocateAxisAttribute = false;
    [Tooltip("亚帧补偿：把粒子从穿面到被观测之间已流逝的时间补到出口位置。关掉会出现间距 v*dt 的条纹。")]
    public bool useSubFrameCompensation = true;
    [Tooltip("预测式捕获：对引擎即将走的那一步做扫掠，提前一步捕获。消除入口伪隧穿与出口门面缝隙。\n检测到实体墙时会自动禁用（预测式需要把粒子放到门面之前，那个位置在墙里）。")]
    public bool usePredictiveCapture = true;

    [Header("粒子传送 · 射线量墙（碰撞墙场景）")]
    [Tooltip("用射线量出门所在墙体的正面位置，自动把检测面外扩到『墙面 + 粒子碰撞半径 + 余量』，粒子在撞墙前被收走。")]
    public bool autoMeasureWall = false;
    [Tooltip("只允许命中的碰撞体（门附着的那堵墙）。留空则取门 Transform 上的 Collider。\n合并到大系统后建议由传送枪放置时把 hit.collider 直接喂进来。")]
    public Collider portalWallColliderA;
    public Collider portalWallColliderB;
    public LayerMask wallProbeMask = ~0;
    public float wallProbeMaxDepth = 3f;
    public float wallExitMargin = 0.02f;
    public float wallRemeasureInterval = 1f;
    [Tooltip("粒子碰撞半径手动上限（米）。<=0 自动：startSize随机上限 × collision.radiusScale × 0.5")]
    public float particleRadiusOverride = -1f;

    // 身份证标记位（实测真值表：angularVelocity 出生必被引擎清零、其余时刻 100% 保真）
    private const float PASSPORT_MARKER = 1e-9f;
    private const float PASSPORT_PENDING_A = 2e-9f;
    private const float PASSPORT_PENDING_B = 3e-9f;
    private const float PASSPORT_NEWBORN_AGE_SLACK = 1.05f;
    private const float PASSPORT_SANITY_FACTOR = 4f;
    private const float PASSPORT_SANITY_BASE = 1f;

    private float effPlaneOffset = 0.05f;      // 本帧实际使用的检测面外扩量
    private float exitMinNormal = -1f;         // 实体墙时出射点法向距离的硬下界
    private Vector3 lastExitFrontAnchor = Vector3.zero;
    private float particleLastFrameDt = 0f;
    private float wallFrontA = 0f, wallBackA = 0f, wallFrontB = 0f, wallBackB = 0f;
    private bool wallOkA = false, wallOkB = false;
    private float wallTimer = 0f;
    private bool wallLoggedOnce = false;
    private ParticleSystem[] passportDisabledSystems = new ParticleSystem[16];
    private int passportDisabledCount = 0;
    private ParticleSystem[] sizeWarnedSystems = new ParticleSystem[16];
    private int sizeWarnedCount = 0;

    // 诊断计数器
    private int debugPassportUsed = 0, debugPassportMissing = 0, debugGateRejected = 0;
    private int debugPredictiveHits = 0, debugRule5 = 0, debugRejectedOutbound = 0;
    private int debugTrueLeakA = 0, debugTrueLeakB = 0;
    private float debugTrueLeakDepthA = 0f, debugTrueLeakDepthB = 0f;
    private int debugBounceOutOfFrame = 0, debugBounceNoFlip = 0, debugBounceBadT = 0, debugBounceBadZ = 0;
    private int debugNewbornTotal = 0, debugNewbornSentinel = 0, debugNewbornStale = 0;
    private float debugStaleDist = 0f;
    private float debugOvershootSum = 0f, debugOvershootMax = 0f;
    private int debugOvershootN = 0;
    private int[] debugExitHist = new int[8];
    private float debugExitMin = 0f, debugExitMax = 0f;
    private bool debugExitAny = false;
    private uint debugWorstSeed = 0;
    private float debugWorstOvershoot = 0f, debugWorstExitDist = 0f;
    private Vector3 debugWorstPos = Vector3.zero, debugWorstVel = Vector3.zero;
    private int debugWorstRule = 0;
    private bool debugWorstPredicted = false;
    private int debugReverseTeleports = 0, debugAtoB = 0, debugBtoA = 0;
    private bool debugVerboseEvents = false;


    // 放置时发现：OverlapSphere 缓冲(512) + 注册表（满员自动翻倍扩容，十轮收尾）
    private Collider[] discoveryOverlapBuffer;
    private ParticleSystem[] placedDiscoverySystems;
    private int placedDiscoveryCount = 0;

    [Header("════════════ 性能优化 ════════════")]
    public bool enableVisibilityOptimization = true;
    public float maxRenderDistance = 50f;
    public float maxViewAngle = 100f;
    public int checkInterval = 2;

    [Tooltip("以保守门面投影交集剔除不可见的递归，不改变分辨率或最大层数。")]
    public bool enablePortalMaskCulling = true;
    public float portalRenderNearDistance = 1f;
    [Tooltip("玩家视野宽高比的保守上界；Udon无法读取Screen尺寸。默认覆盖32:9，更宽屏幕应调大。仅影响入口粗筛，不改变画面投影。")]
    public float portalViewerAspectBound = 3.6f;
    private int portalInvisibleChecksA;
    private int portalInvisibleChecksB;
    private Vector2[] portalRenderMask;
    private Vector2[] portalRenderMaskScratch;
    private Vector2[] portalProjectedQuad;
    private Vector3[] portalViewQuad;
    private int portalRenderMaskCount;

    [Tooltip("强制关闭 A/B 传送门相机的 Occlusion Culling。传送门相机会穿墙渲染另一侧世界，Unity 遮挡剔除可能把门后/墙后的 clone 或刚体错误剔除。建议保持开启。")]
    public bool disablePortalCameraOcclusionCulling = true;

    [Header("════════════ 调试 ════════════")]
    public bool showDebugGizmos = false;
    public Color gizmoColorA = new Color(1f, 0.4f, 0.1f, 0.5f);
    public Color gizmoColorB = new Color(0.1f, 0.6f, 1f, 0.5f);

    [Header("════════════ 自动适配配置 ════════════")]
    [Tooltip("VR模式下强制使用的FOV (建议110)")]
    public float vrTargetFOV = 110f;

    [Header("════════════ 刚体传送（Portal 原著复刻）════════════")]
    [Tooltip("是否启用刚体传送功能")]
    public bool enableRigidbodyTeleport = true;
    [Tooltip("刚体检测用的 OverlapBox 额外深度扩展（基础值）。实际追踪深度还会按刚体沿门法线速度动态扩大，以降低高速漏检。")]
    public float rbTriggerDepthExtension = 0.8f;
    [Tooltip("抓取刚体进入传送门前允许提前切到无环境碰撞层的额外深度。默认0.15米，避免门附近约1米范围都失去环境碰撞；传送门本身仍能提前一小段接管。")]
    public float heldRigidbodyTriggerDepthExtension = 0.15f;
    [Tooltip("是否按刚体沿门法线速度动态扩大追踪深度。不是直接传送，只是更早加入 Seb traveller 追踪，降低高速隧穿。")]
    public bool enableRigidbodyDynamicTrackingDepth = true;
    [Tooltip("动态追踪深度上限，防止极高速刚体把 OverlapBox 扩得过大造成性能/误触发问题。")]
    public float rbMaxDynamicTrackingDepth = 8f;
    [Tooltip("刚体传送时使用 crossingT 连续修正：回到穿越平面瞬间，再积分本帧剩余时间。用于减少地板双洞越飞越高。")]
    public bool enableRigidbodyCrossingTimeCorrection = true;
    [Tooltip("刚体 crossingT 修正的剩余时间估算方式：开启=按 当前离门平面距离/当前法线速度 反推，更适合刚体物理；关闭=按 crossingT * Time.deltaTime，较依赖渲染帧时间。")]
    public bool useRigidbodyDistanceBasedPostCrossTime = true;
    [Tooltip("距离/速度反推剩余时间时使用的最小法线速度，低于此值则回退到 crossingT*deltaTime，避免除零和低速抖动。")]
    public float rbPostCrossNormalSpeedEpsilon = 0.05f;
    [Tooltip("刚体 crossingT 修正的剩余时间上限，防止极端低速/异常位置反推出过大的 dt。")]
    public float rbPostCrossMaxDt = 0.05f;
    [Tooltip("是否对抓取中的刚体也做传送（配合传送枪的 UpdateHeldAfterTeleport）")]
    public bool allowHeldRigidbodyTeleport = true;

    [Header("════════════ 刚体穿门镜像（clone虚影）════════════")]
    [Tooltip("刚体进入 Clip Volume 时，在出口侧创建一份本地VRCInstantiate副本作为碰撞/视觉虚影。clone不带Rigidbody，由代码每帧同步位置，刚体真正穿越后翻转映射方向持续镜像。其他玩家看不到。")]
    public bool enableRigidbodyPortalClones = true;

    [Tooltip("clone最大数量上限，防止极端情况clone爆炸")]
    [Range(4, 64)]
    public int maxRigidbodyClones = 16;

    [Tooltip("clone和本体距离小于此值时自动隐藏clone，避免贴门/两门相距近（如地板双洞）时视觉重叠z-fighting。" +
             "本体就在clone旁边7cm时clone是多余的（你已经能看到本体）；正常对墙场景clone在对面墙几米外不会触发隐藏。" +
             "建议保持 teleportTriggerOffset 的1~2倍。")]
    public float cloneNearCullDistance = 0.25f;

    // 刚体clone追踪数组：originalRigidbody -> cloneGameObject
    // 注意：clone只在刚体"靠近门、还没传送"期间存在；刚体传送后立即销毁clone
    private const int MAX_RIGIDBODY_CLONES = 64;
    private Rigidbody[] cloneOriginalRigidbodies = new Rigidbody[MAX_RIGIDBODY_CLONES];
    private GameObject[] cloneGameObjects = new GameObject[MAX_RIGIDBODY_CLONES];
    private Transform[] cloneTargetPortals = new Transform[MAX_RIGIDBODY_CLONES]; // clone的"映射源门"：rb在这扇门一侧，clone被映射到对面
    private bool[] clonePendingActivation = new bool[MAX_RIGIDBODY_CLONES]; // 初次VRCInstantiate后先不激活，本帧末位置算好再激活
    private int cloneCount = 0;
    // 材质跟随缓存：每个clone的渲染器配对（本体渲染器数组 <-> clone渲染器数组）。
    // 创建时由 SyncCloneMaterials 写入，UpdateRigidbodyClonePoses 每帧用 RepointCloneMaterials 校正，
    // 保证clone渲染器永远指向本体当前的材质实例（含动画控制器正在驱动的动画值，比如材质变色）。
    // 为什么必须每帧校正而不是只在创建时赋值一次：StripCloneComponents 对clone身上的 Animator 调用的
    // Destroy() 是【帧末延迟生效】的；Animator 真正被销毁时，Unity 会把被它动画过的渲染器材质
    // 还原回文件夹里的默认资产，clone创建时那次材质赋值会被这次还原静默撤销。
    private Renderer[][] cloneMaterialSyncOriginalRenderers = new Renderer[MAX_RIGIDBODY_CLONES][];
    private Renderer[][] cloneMaterialSyncCloneRenderers = new Renderer[MAX_RIGIDBODY_CLONES][];
    // MaterialPropertyBlock 同步缓冲：联网查证（Unity官方文档/论坛）确认，Animator 对材质属性的动画
    // 不写进材质本身，而是通过渲染器的 MaterialPropertyBlock 应用——只共享材质引用永远拿不到动画值。
    // 每帧对本体渲染器 GetPropertyBlock 进这个 block、再 SetPropertyBlock 到 clone 渲染器；block 复用，零GC。
    private MaterialPropertyBlock clonePropertyBlockSyncBuffer;
    // clone 组件清理类型列表（Udon不支持自定义类上的static字段，改成实例字段，Start里初始化）
    private System.Type[] cloneDestroyTypes;
    // 复用刚体检测用的rbOverlapBuffer已经在ProcessRigidbodyForPortal里，clone更新只在那个流程里做



    [Header("════════════ 刚体过门防CPU剔除（源刚体bounds临时扩大）════════════")]
    [Tooltip("刚体进入门volume被追踪时，临时把它的Mesh.bounds扩大+关闭动态遮挡，避免传送门相机（oblique近裁+特殊视角）把整块刚体误cull掉；离开追踪时自动还原。这是之前LLM验证有效的方向。")]
    public bool expandRigidbodyTransitionSourceMeshBounds = false;

    [Tooltip("临时扩大的bounds立方体边长（米）。20通常够用；仍偶发消失可加至50。离开追踪后会还原为原始bounds。")]
    [Range(10f, 200f)]
    public float rigidbodyTransitionExpandedBoundsSize = 200f;

    // 记录哪些源刚体的 Mesh/MeshRenderer/SkinnedMeshRenderer 被我们临时改过，离开时还原
    private const int MAX_RB_CULLING_OVERRIDES = 128;
    private Rigidbody[] rbCullingOverrideRigidbodies = new Rigidbody[MAX_RB_CULLING_OVERRIDES];
    // Mesh 路径：
    private MeshFilter[] rbCullingOverrideMeshFilters = new MeshFilter[MAX_RB_CULLING_OVERRIDES];
    private Mesh[] rbCullingOverrideOriginalMeshes = new Mesh[MAX_RB_CULLING_OVERRIDES]; // 注意：这里存的是 mf.mesh 实例引用；bounds直接改在这份实例上，还原时写回原始bounds
    private Bounds[] rbCullingOverrideOriginalMeshBounds = new Bounds[MAX_RB_CULLING_OVERRIDES];
    // MeshRenderer 动态遮挡路径：
    private MeshRenderer[] rbCullingOverrideMeshRenderers = new MeshRenderer[MAX_RB_CULLING_OVERRIDES];
    private bool[] rbCullingOverrideOriginalAllowOcclusion = new bool[MAX_RB_CULLING_OVERRIDES];
    // SkinnedMeshRenderer 路径：
    private SkinnedMeshRenderer[] rbCullingOverrideSMRs = new SkinnedMeshRenderer[MAX_RB_CULLING_OVERRIDES];
    private Bounds[] rbCullingOverrideOriginalSMRBounds = new Bounds[MAX_RB_CULLING_OVERRIDES];
    private bool[] rbCullingOverrideOriginalSMROffscreen = new bool[MAX_RB_CULLING_OVERRIDES];
    private int rbCullingOverrideCount = 0;

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

    [Tooltip("Seb oblique 裁剪偏移。会取绝对值；建议 0.001~0.05。越小越贴门，越容易显示贴门刚体/clone，但更可能看到门背面/墙；越大越保守，可能加重缝隙。")]
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

    [Header("════════════ 玩家出门音效 ════════════")]
    [Tooltip("玩家每次从传送门出口出来时播放一次的音源。建议放一个独立 3D AudioSource，不要挂在 A/B 门父物体上。")]
    public AudioSource playerPortalExitAudioSource;

    [Tooltip("玩家出门音效数组。每次玩家传送成功后随机播放一个。")]
    public AudioClip[] playerPortalExitSounds;

    [Tooltip("玩家出门音效音量。")]
    [Range(0f, 1f)]
    public float playerPortalExitSoundVolume = 1f;

    [Tooltip("播放玩家出门音效前，是否把音源移动到出口门位置。默认关闭：避免用户误把 A/B 门本体或父物体上的 AudioSource 拖进来，导致每次传送时移动传送门。若要 3D 出口音效，请使用独立 AudioSource 物体再开启。")]
    public bool movePlayerExitAudioSourceToExitPortal = false;

    [Header("════════════ 过渡系统（新版） ════════════")]
    [Tooltip("过渡 Cube。子集包含过渡相机。传送时显示并控制旋转，过渡完成后关闭。")]
    public GameObject portalViewTransitionCube;
    [Tooltip("过渡时长（秒）。")]
    public float transitionDuration = 0.5f;

    [Tooltip("过渡相机安全 Near Clip。过渡相机不做传送门裁剪，只需要一个合法的小 near；避免 cameraNearClip 被调到 0 时过渡画面异常。")]
    public float transitionCameraSafeNearClip = 0.01f;

    [Header("════════════ 配置快照导出 ════════════")]
    [Tooltip("开局时（Start）把当前 Inspector 关键配置 + 传送门A/B父物体下所有子物体的 Collider/Renderer/Mesh/Camera/Light/AudioSource/Rigidbody 信息打印到控制台。\n用途：把这份文本复制给别人分析场景结构、排查性能问题，不用截图/不用一个个字段抄。\n只在 Start 执行一次，不影响运行时性能。")]
    public bool dumpConfigSnapshotOnStart = false;

    [Header("════════════ 同Collider专修/调试 ════════════")]
    [Tooltip("核心传送短日志：只输出 T# 和 OUT，推荐测试时开启。")]
    public bool debugTeleportCoreLog = false;
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

    [Tooltip("混合 traveller（推荐开启）：用【头部】判定是否穿过了门平面（检测点），用【根骨】计算实际传送落点（映射点）。所有门朝向统一标准：头穿过门平面→传送。关闭则退回检测与落点都用root的旧模式（脚过平面就触发，斜门下落易漏检）。")]
    public bool useHybridRootXYHeadZTraveller = true;

    [Tooltip("出口侧保险：如果计算出的出口 traveller 落在入口侧/门背面，则只沿出口法线拉回到正确侧一点点。主要防45度斜面/角色控制器误差导致来回鬼畜。")]
    public bool enableExitSideCorrection = true;

    [Tooltip("出口侧保险的最小离门距离，建议 0.01~0.03。不是速度推力，只在落到错误侧或太贴门时修正。")]
    public float exitSideMinDistance = 0.02f;

    [Tooltip("头部 traveller 旧模式使用：用 Head 算出新 Head，再用 AvatarRoot/Origin 偏移算 TeleportTo 位置。根骨 traveller 模式会直接 TeleportTo 新 root。")]
    [HideInInspector]
    public bool useVRCTrackingRootTeleport = true;

    // 刚体 traveller 追踪 - SebLague Portal.HandleTravellers 的 Udon 固定数组版。
    // 不能使用 List/Dictionary；A/B 两扇门各维护一组 trackedTravellers，等价于 Seb 原版每个 Portal 自己的 trackedTravellers。
    private const int MAX_TRACKED_RBS = 32;
    private Rigidbody[] trackedRigidbodiesA = new Rigidbody[MAX_TRACKED_RBS];
    private Rigidbody[] trackedRigidbodiesB = new Rigidbody[MAX_TRACKED_RBS];
    private Vector3[] rbPreviousOffsetFromPortalA = new Vector3[MAX_TRACKED_RBS];
    private Vector3[] rbPreviousOffsetFromPortalB = new Vector3[MAX_TRACKED_RBS];
    private int[] rbOriginalLayerA = new int[MAX_TRACKED_RBS];
    private int[] rbOriginalLayerB = new int[MAX_TRACKED_RBS];
    // 记录刚体最近一次明确处于传送门哪一侧。解决低速/贴门时首次追踪点已经在 z≈0 死区内，导致 previous/current 都无法可靠换边的问题。
    private int[] rbLastPortalSideA = new int[MAX_TRACKED_RBS];
    private int[] rbLastPortalSideB = new int[MAX_TRACKED_RBS];
    private int trackedRBCountA = 0;
    private int trackedRBCountB = 0;

    // 刚体检测 OverlapBox NonAlloc 缓冲。A/B 门顺序处理，共用一个缓冲即可，避免每帧分配 Collider[]。
    private const int MAX_RB_OVERLAP_COLLIDERS = 128;
    private Collider[] rbOverlapBuffer = new Collider[MAX_RB_OVERLAP_COLLIDERS];
    private const int MAX_RB_WALL_IGNORE_PAIRS = 256;
    private Rigidbody[] rbWallIgnoreRigidbodies = new Rigidbody[MAX_RB_WALL_IGNORE_PAIRS];
    private Collider[] rbWallIgnoreBodies = new Collider[MAX_RB_WALL_IGNORE_PAIRS];
    private Collider[] rbWallIgnoreWalls = new Collider[MAX_RB_WALL_IGNORE_PAIRS];
    private bool[] rbWallIgnoreOriginalStates = new bool[MAX_RB_WALL_IGNORE_PAIRS];
    private bool[] rbWallIgnorePortalA = new bool[MAX_RB_WALL_IGNORE_PAIRS];
    private bool[] rbWallIgnorePortalB = new bool[MAX_RB_WALL_IGNORE_PAIRS];
    private bool[] rbWallIgnoreSeen = new bool[MAX_RB_WALL_IGNORE_PAIRS];
    private int rbWallIgnorePairCount;


    // Clip Volume 批量穿透：固定数组追踪当前被我们切到 playerPassThroughLayer 的静态Collider
    private const int MAX_CLIP_VOLUME_COLLIDERS = 64;
    private Collider[] clipVolumeTrackedCollidersA = new Collider[MAX_CLIP_VOLUME_COLLIDERS];
    private int[] clipVolumeOriginalLayersA = new int[MAX_CLIP_VOLUME_COLLIDERS];
    private int clipVolumeTrackedCountA = 0;
    private Collider[] clipVolumeTrackedCollidersB = new Collider[MAX_CLIP_VOLUME_COLLIDERS];
    private int[] clipVolumeOriginalLayersB = new int[MAX_CLIP_VOLUME_COLLIDERS];
    private int clipVolumeTrackedCountB = 0;
    private const int MAX_CLIP_VOLUME_OVERLAP = 128;
    private Collider[] clipVolumeOverlapBuffer = new Collider[MAX_CLIP_VOLUME_OVERLAP];

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
    private Quaternion[] recursiveRotationsA;
    private Quaternion[] recursiveRotationsB;
    private RenderTexture cachedPortalTextureA;
    private RenderTexture cachedPortalTextureB;
    // Shader _TexWidth/_TexHeight 驱动缓存：避免每帧 SetFloat，仅当 RT 尺寸变化时更新材质
    private int cachedPortalTexWidthA = -1;
    private int cachedPortalTexHeightA = -1;
    private int cachedPortalTexWidthB = -1;
    private int cachedPortalTexHeightB = -1;

    // 每帧 setter 缓存：避免 Camera.fieldOfView/nearClipPlane/Material.SetFloat 每帧无条件跨域调用
    private float cachedLastSyncFOV = -1f;
    private float cachedLastZTestA = -1f;
    private float cachedLastZTestB = -1f;

    void OnDisable()
    {
        // 脚本被禁用/卸载时兜底还原所有被我们切过layer的collider，防止永久残留
        RestoreAllClipVolumeColliders(clipVolumeTrackedCollidersA, clipVolumeOriginalLayersA, ref clipVolumeTrackedCountA);
        RestoreAllClipVolumeColliders(clipVolumeTrackedCollidersB, clipVolumeOriginalLayersB, ref clipVolumeTrackedCountB);
        // 清理所有刚体与门墙的临时IgnoreCollision关系，避免禁用/重载后仍穿透地板。
        RestoreRigidbodyPortalWallIgnore(null);
        // 还原所有被我们临时改过bounds/occlusion的源刚体renderer，防止残留
        RestoreAllRigidbodyCullingOverrides();
        // 销毁所有刚体clone，防止残留物体
        DestroyAllRigidbodyClones();
    }

    // ============================================================
    // Start
    // ============================================================

    void Start()
    {
        localPlayer = Networking.LocalPlayer;

        // 粒子传送：开启自动收集时先跑一次初始发现
        if (enableParticleTeleport && autoDiscoverParticleSystems)
        {
            DiscoverParticleSystems();
            // 放置时发现：以两扇门的初始位置为中心各扫一遍
            if (portalPlaneA != null) DiscoverParticleSystemsAround(portalPlaneA.position);
            if (portalPlaneB != null) DiscoverParticleSystemsAround(portalPlaneB.position);
        }
        else
        {
            // 十一轮：堵死最后的静默路径——初始发现没跑时亲口说明原因，
            // 避免"放置发现没工作又没日志"的排查死胡同
            Debug.Log("[粒子传送][初始发现] 跳过：enableParticleTeleport=" + enableParticleTeleport + " autoDiscoverParticleSystems=" + autoDiscoverParticleSystems);
        }

        // 初始化 clone 要销毁的组件类型列表（Udon不支持自定义static字段，Start里构建实例数组）
        cloneDestroyTypes = new System.Type[]
        {
            typeof(Rigidbody),
            typeof(VRC.SDKBase.VRC_Pickup),
            typeof(AudioSource),
            typeof(Light),
            typeof(ParticleSystem),
            typeof(Animator),
            typeof(UdonSharpBehaviour),
        };

        if (cameraNearClip < 0.001f)
        {
            cameraNearClip = 0.001f;
            TPLog("[启动] cameraNearClip 不能为0或负数，已自动钳制到0.001。门后墙/缝隙请优先调 recursiveDynamicNearClipPadding / recursiveNearClipOffset。 ");
        }

        // 兼容旧场景序列化：Unity 已挂到场景里的 UdonBehaviour 不会因为脚本默认值变化就自动刷新。
        // 当前方案：墙/地板/屏蔽碰撞体进入门区域时用 17(Walkthrough)，挡刚体/物品但不挡玩家。
        if (playerPassThroughLayer == 29 || playerPassThroughLayer == 25)
        {
            playerPassThroughLayer = 17;
            TPLog("[启动] 检测到旧玩家穿透层，已自动改为17(Walkthrough)。若 Inspector 仍显示旧值，请检查旧脚本/旧Prefab序列化值。");
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

        if (cameraA != null) cameraA.nearClipPlane = SafeCameraNearClip();
        if (cameraB != null) cameraB.nearClipPlane = SafeCameraNearClip();
        ApplyPortalCameraOcclusionSettings();

        recursivePositionsA = new Vector3[8];
        recursivePositionsB = new Vector3[8];
        recursiveRotationsA = new Quaternion[8];
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
        // 八轮修复（玩家高速隧穿破案）：旧版把缓冲钳死在1.5米——下落速度>45m/s后缓冲停止生长，
        // 每帧位移超过 noClipDepth+colliderDisableBuffer+1.5（约2米）时，玩家一帧从切层区外
        // 直接跳过整个区域砸到地板/天花板碰撞体上：碰撞先于传送触发（停下）或胶囊隧穿薄板
        // （掉出去）——正是"天花板+地板门无空气阻力无限坠落加速循环"高速断裂的病根。
        // 现改为：缓冲 = 速度 × 步长 × 2，安全上限放到50米（仅防异常值，正常速度永不到顶）。
        // 步长取 max(渲染dt, 物理dt)：真正解算碰撞的是物理步（VRChat的FixedUpdate锁刷新率），
        // 高帧率下渲染dt小于物理步长，只用渲染dt会低估一帧的真实位移。
        float stepDt = Mathf.Max(Time.deltaTime, Time.fixedDeltaTime);
        float speedBuffer = localPlayer.GetVelocity().magnitude * stepDt * 2.0f;
        cachedSpeedBufferThisFrame = Mathf.Clamp(speedBuffer, 0f, 50f);

        // 刚体传送处理（优先保证能检测到，先用高频）
        if (enableRigidbodyTeleport)
        {
            ProcessRigidbodyTravellers();
        }

        // 粒子传送（白名单 + 自动收集的系统，只处理离门足够近的）
        if (enableParticleTeleport)
        {
            if (autoDiscoverParticleSystems)
            {
                particleDiscoveryTimer += Time.deltaTime;
                if (particleDiscoveryTimer >= particleDiscoveryRefreshInterval)
                {
                    particleDiscoveryTimer = 0f;
                    DiscoverParticleSystems();
                }
            }
            ProcessParticleTeleports();
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
            // 统一使用 scale-free 矩阵：即使 portalParent 被缩放，相机镜像位置也不会被错误拉伸
            Vector3 playerLocalToA = LocalPointForPortal(portalParentA, playerHead);
            Quaternion playerLocalRotToA = Quaternion.Inverse(portalParentA.rotation) * playerWorldRot;
            if (useClassicHalfTurn)
            {
                playerLocalToA = cameraHalfTurn * playerLocalToA;
                playerLocalRotToA = cameraHalfTurn * playerLocalRotToA;
            }
            cameraB.transform.position = WorldPointFromPortal(portalParentB, playerLocalToA);
            cameraB.transform.rotation = portalParentB.rotation * playerLocalRotToA;
        }

        if (cameraA != null)
        {
            Vector3 playerLocalToB = LocalPointForPortal(portalParentB, playerHead);
            Quaternion playerLocalRotToB = Quaternion.Inverse(portalParentB.rotation) * playerWorldRot;
            if (useClassicHalfTurn)
            {
                playerLocalToB = cameraHalfTurn * playerLocalToB;
                playerLocalRotToB = cameraHalfTurn * playerLocalRotToB;
            }
            cameraA.transform.position = WorldPointFromPortal(portalParentA, playerLocalToB);
            cameraA.transform.rotation = portalParentA.rotation * playerLocalRotToB;
        }

        // ============================================================
        // 可见性优化
        // ============================================================

        frameCounter++;
        if (enablePortalMaskCulling || frameCounter >= checkInterval)
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
                bool portalAVisible;
                bool portalBVisible;
                if (enablePortalMaskCulling)
                {
                    float aspect = cameraB != null ? cameraB.aspect : 16f / 9f;
                    if (cameraA != null) aspect = Mathf.Max(aspect, cameraA.aspect);
                    aspect = Mathf.Max(aspect, portalViewerAspectBound);
                    portalAVisible = IsPortalInViewerMask(portalPlaneA, playerHead, playerWorldRot, syncFOV, aspect);
                    portalBVisible = IsPortalInViewerMask(portalPlaneB, playerHead, playerWorldRot, syncFOV, aspect);
                    portalInvisibleChecksA = portalAVisible ? 0 : Mathf.Min(portalInvisibleChecksA + 1, 2);
                    portalInvisibleChecksB = portalBVisible ? 0 : Mathf.Min(portalInvisibleChecksB + 1, 2);
                    portalAVisible = portalInvisibleChecksA < 2;
                    portalBVisible = portalInvisibleChecksB < 2;
                }
                else
                {
                    portalAVisible = IsPortalVisible(playerHead, playerForward, portalPlaneA, rendererA);
                    portalBVisible = IsPortalVisible(playerHead, playerForward, portalPlaneB, rendererB);
                }
                isCameraBRendering = portalAVisible;
                isCameraARendering = portalBVisible;
                if (cameraA != null) cameraA.enabled = enableSebRecursiveRendering && recursiveForceManualCamerasDisabled ? false : isCameraARendering;
                if (cameraB != null) cameraB.enabled = enableSebRecursiveRendering && recursiveForceManualCamerasDisabled ? false : isCameraBRendering;
            }
            else
            {
                isCameraARendering = true;
                isCameraBRendering = true;
                portalInvisibleChecksA = 0;
                portalInvisibleChecksB = 0;
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
            // FOV：syncFOV 在运行中基本不变（VR固定vrTargetFOV，Desktop手动改currentFOV），值未变化就跳过跨域 setter
            if (Mathf.Abs(cameraA.fieldOfView - syncFOV) > 0.001f) cameraA.fieldOfView = syncFOV;
            // nearClip：递归模式下由递归渲染函数自己管理 SyncRecursiveNearClipToPortalPlane 并在退出时 ResetProjectionMatrix+SafeNearClip，
            // 主路径重复设一次是冗余；非递归自动渲染模式下再兜底设置，保持行为一致。
            if (!enableSebRecursiveRendering) cameraA.nearClipPlane = SafeCameraNearClip();
        }
        if (cameraB != null)
        {
            if (Mathf.Abs(cameraB.fieldOfView - syncFOV) > 0.001f) cameraB.fieldOfView = syncFOV;
            if (!enableSebRecursiveRendering) cameraB.nearClipPlane = SafeCameraNearClip();
        }

        // 注意：useOcclusionCulling 只在 Start() 设一次即可，运行时不变，不需要每帧调用

        // 材质 _FOV：每帧 shader 都要采样，但值不变时跳过 SetFloat 跨域调用
        if (Mathf.Abs(cachedLastSyncFOV - syncFOV) > 0.001f)
        {
            cachedLastSyncFOV = syncFOV;
            if (portalMatA != null) portalMatA.SetFloat("_FOV", syncFOV);
            if (portalMatB != null) portalMatB.SetFloat("_FOV", syncFOV);
        }

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
                    // 传送发生同帧也要先跑一次clipVolume更新（保持layer切换状态一致，不能因为return跳过导致volume内穿模墙漏切）
                    UpdateClipVolumePassThrough(
                        clipVolumeColliderA, portalPlaneA, playerHead, playerFeet, ResolvePortalShape(true),
                        clipVolumeTrackedCollidersA, clipVolumeOriginalLayersA, ref clipVolumeTrackedCountA
                    );
                    UpdateClipVolumePassThrough(
                        clipVolumeColliderB, portalPlaneB, playerHead, playerFeet, ResolvePortalShape(false),
                        clipVolumeTrackedCollidersB, clipVolumeOriginalLayersB, ref clipVolumeTrackedCountB
                    );

                    // 刚体过门防CPU剔除：传送同帧也要同步一次
                    ReconcileRigidbodyCullingOverrides();
                    // 刚体clone位姿同步（传送同帧也要跑一次，不然本帧clone位置停在老地方）
                    UpdateRigidbodyClonePoses();

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
            if (debugTeleportVerbose && debugLogIntervalFrames > 0 && Time.frameCount % debugLogIntervalFrames == 0)
            {
                TPLog("[传送检测暂停] 直到帧 " + teleportBlockedUntilFrame);
            }
        }

        // ============================================================
        // Clip Volume 批量穿透（解决穿模多墙漏切问题）
        // 注意：必须放在 ProcessPortalTeleport 之后调用！
        // 原因（换句话来说：也就是ProcessPortalTeleport里的markedCollider单物体穿透逻辑会记"这块墙原始layer是啥"，
        // 如果clipVolume先跑把墙切成17，markedCollider看到的就全是17，会错把17当成"原始layer"记住，玩家离开时就"还原"回17=永远不还原。
        // 所以让老逻辑先看到真实的原始layer并正确记录；clipVolume后跑，看到已经是17的（且不是我们track过的）就不接管，让markedCollider负责还原它。
        // 这样clipVolume只管markedCollider没覆盖到的"其他穿模叠在一起的多余墙面"，两边互不干扰。）
        // ============================================================
        UpdateClipVolumePassThrough(
            clipVolumeColliderA, portalPlaneA, playerHead, playerFeet, ResolvePortalShape(true),
            clipVolumeTrackedCollidersA, clipVolumeOriginalLayersA, ref clipVolumeTrackedCountA
        );
        UpdateClipVolumePassThrough(
            clipVolumeColliderB, portalPlaneB, playerHead, playerFeet, ResolvePortalShape(false),
            clipVolumeTrackedCollidersB, clipVolumeOriginalLayersB, ref clipVolumeTrackedCountB
        );

        // ============================================================
        // 刚体过门防CPU剔除：对当前被追踪的源刚体临时扩bounds/关dynamic occlusion
        // ============================================================
        ReconcileRigidbodyCullingOverrides();

        // ============================================================
        // 刚体clone位姿同步：每帧把clone映射到出口侧位置
        // ============================================================
        UpdateRigidbodyClonePoses();

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
        // P2：已原子删除所有调试日志输出，保留空方法避免调用点失效
        return;
    }

    float SafeCameraNearClip()
    {
        // Unity Camera nearClipPlane 不能可靠使用 0。用户 Inspector 里可以保留 0 配置，运行时统一兜底到极小正值。
        return Mathf.Max(cameraNearClip, 0.001f);
    }

    void ApplyPortalCameraOcclusionSettings()
    {
        if (!disablePortalCameraOcclusionCulling) return;
        if (cameraA != null) cameraA.useOcclusionCulling = false;
        if (cameraB != null) cameraB.useOcclusionCulling = false;
    }

    void PlayPlayerPortalExitSound(Transform exitPortal)
    {
        if (playerPortalExitAudioSource == null) return;
        if (playerPortalExitSounds == null || playerPortalExitSounds.Length == 0) return;

        int index = Random.Range(0, playerPortalExitSounds.Length);
        AudioClip clip = playerPortalExitSounds[index];
        if (clip == null) return;

        if (movePlayerExitAudioSourceToExitPortal && exitPortal != null)
        {
            playerPortalExitAudioSource.transform.position = exitPortal.position;
        }
        playerPortalExitAudioSource.PlayOneShot(clip, playerPortalExitSoundVolume);
    }

    // ============================================================
    // 配置快照导出（已停用）：历史上的调试输出已在 P2 清理中原子删除。
    // dumpConfigSnapshotOnStart 字段保留（公共序列化字段，场景里可能存有值，删字段有风险），
    // 对应调用链保留为一个显式的空实现。
    // 原有的 DumpGlobalConfigSnapshot / DumpPortalGunConfigSnapshot / DumpPortalHierarchySnapshot /
    // GetPortalShapeName 四个函数经排查全工程无任何调用点，已作为死代码删除。
    // ============================================================

    void DumpConfigSnapshot()
    {
        // P2：已原子删除配置快照调试输出
        return;
    }

    bool IsBodyInColliderZone(Transform portalPlane, Vector3 playerHead, Vector3 playerFeet, int shapeType)
    {
        // 性能：localHead/localFeet 只算一次，内联 XY 判定，避免以前 IsBodyInPortalXY 重复 InverseTransformPoint
        Vector3 localHead = LocalPointForPortal(portalPlane, playerHead);
        Vector3 localFeet = LocalPointForPortal(portalPlane, playerFeet);

        bool headInXY = LocalPointInPortalRect(localHead, shapeType);
        bool feetInXY = LocalPointInPortalRect(localFeet, shapeType);
        bool bodyInXY = headInXY || feetInXY;

        float headZ = localHead.z;
        float feetZ = localFeet.z;
        float bodyMinZ = Mathf.Min(headZ, feetZ);
        float bodyMaxZ = Mathf.Max(headZ, feetZ);

        // 性能优化：speedBuffer 每帧只在 LateUpdate 里算一次（见 cachedSpeedBufferThisFrame 注释），这里直接复用。
        float colliderThreshold = noClipDepth + colliderDisableBuffer + cachedSpeedBufferThisFrame;

        return bodyInXY &&
               bodyMinZ < colliderThreshold &&
               bodyMaxZ > -colliderThreshold;
    }

    bool IsHeadInsidePortalVisualVolume(Transform portalPlane, Vector3 playerHead, int shapeType)
    {
        if (portalPlane == null) return false;
        Vector3 localHead = LocalPointForPortal(portalPlane, playerHead);
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

    public Vector3 LocalPointForPortal(Transform portal, Vector3 worldPoint)
    {
        if (useScaleFreePortalMatrix)
        {
            Matrix4x4 worldToLocal = Matrix4x4.TRS(portal.position, portal.rotation, Vector3.one).inverse;
            return worldToLocal.MultiplyPoint(worldPoint);
        }
        return portal.InverseTransformPoint(worldPoint);
    }

    public Vector3 WorldPointFromPortal(Transform portal, Vector3 localPoint)
    {
        if (useScaleFreePortalMatrix)
        {
            Matrix4x4 localToWorld = Matrix4x4.TRS(portal.position, portal.rotation, Vector3.one);
            return localToWorld.MultiplyPoint(localPoint);
        }
        return portal.TransformPoint(localPoint);
    }

    public Vector3 LocalDirForPortal(Transform portal, Vector3 worldDir)
    {
        if (useScaleFreePortalMatrix)
        {
            Matrix4x4 worldToLocal = Matrix4x4.TRS(portal.position, portal.rotation, Vector3.one).inverse;
            return worldToLocal.MultiplyVector(worldDir);
        }
        return portal.InverseTransformDirection(worldDir);
    }

    public Vector3 WorldDirFromPortal(Transform portal, Vector3 localDir)
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

    // 十八轮：按粒子半尺寸外扩的门框判定（用户提出"粒子有长宽"语义的便宜实现）。
    // 外扩上限1m防异常startSize；外扩<=1mm时退化为原判定。三角形按相同比例外扩（等比例缩放）。
    bool InflatedPointInPortalRect(Vector3 localPoint, int shapeType, float inflateHalf)
    {
        if (!particleUseSizeInflatedCheck) return LocalPointInPortalRect(localPoint, shapeType);
        if (inflateHalf <= 0.001f) return LocalPointInPortalRect(localPoint, shapeType);

        float hx = portalTriggerWidth * 0.5f + inflateHalf;
        float hy = portalTriggerHeight * 0.5f + inflateHalf;
        if (hx <= 0.0001f || hy <= 0.0001f) return false;

        if (shapeType == PORTAL_SHAPE_TRIANGLE)
        {
            return PointInPortalTriangle(localPoint.x, localPoint.y, hx, hy);
        }
        if (shapeType == PORTAL_SHAPE_CIRCLE)
        {
            float nx = localPoint.x / hx;
            float ny = localPoint.y / hy;
            return nx * nx + ny * ny <= 1f;
        }
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

    /// "朝上门"判定（仅供内部使用）：法线有明显竖直分量（地板/天花板/45°斜坡等）。
    /// 重要：本函数只用于 TeleportSebStyle 里选择【传送落点映射配方】，完全不参与触发判定
    /// （触发检测点永远是 head，见 TravellerLocalForPortal），所以不需要也不提供可调阈值。
    /// 0.5 常量（坡度约≤60°算朝上门）即使分类有偏差，也只影响落点微调的配方选择，
    /// 出口侧保险(enableExitSideCorrection)会兜底，不会造成漏传/方向错误。
    bool IsUpwardFacingPortal(Transform portal)
    {
        if (portal == null) return false;
        return Mathf.Abs(Vector3.Dot(portal.forward, Vector3.up)) > 0.5f;
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

        // 统一检测规则（与门朝向无关）：穿越检测点恒为【head】——头穿过门平面即传送，
        // 与玩家视角一致（SebLague 原版也是相机过平面触发）。地板/天花板/斜坡/墙面同一标准，
        // 不存在"按门的角度决定提前/延后传送"的机制。
        // 刻意不做 root XY + head Z 之类的混搭：斜向门（如45°斜坡）上 head 与 root 的
        // 门平面内 XY 会相差"玩家竖直身高在门平面上的投影"（45°约1.1米）；混搭点不在身体上，
        // 直直下落穿斜门时检测 XY 会偏离头部实际穿平面位置约1.1米 → 门框检查失败 → 穿模漏检。
        // 纯 head 点在任何朝向下都无歧义：XY=头穿平面的位置，Z=头的深度。
        // 实际传送落点由 TeleportPointLocalForPortal(root) 单独计算：头判定穿越、根骨算落点。
        return headLocal;
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
            // 如果传送门重新打到了新物体，先尽量把旧物体恢复，避免旧物体永久停在穿透层。
            if (alreadyActive && oldCollider != null && oldCollider != markedCollider)
            {
                GameObject oldObj = oldCollider.gameObject;
                // rememberedLayer 万一被污染成穿透层本身，优先用 clip volume 追踪表里的真原始值兜底
                int restoreTo = rememberedLayer;
                if (restoreTo == playerPassThroughLayer)
                {
                    int clipOriginal = FindClipVolumeOriginalLayer(oldCollider);
                    if (clipOriginal >= 0 && clipOriginal != playerPassThroughLayer) restoreTo = clipOriginal;
                }
                if (oldObj != null && restoreTo >= 0 && oldObj.layer == playerPassThroughLayer)
                {
                    oldObj.layer = restoreTo;
                }
            }

            int original = obj.layer;
            // 看到的已经是穿透层：真正的原始 layer 在"当初切它的那个系统"手里，按可信度依次取回：
            // 1) 另一侧 markedCollider 的记录（共享 Collider 场景：A/B 打在同一个碰撞体上）；
            // 2) Clip Volume 追踪表（剪刀穿模场景：本门 markedCollider 被对面门的 clipVolume 先切了，
            //    典型：A门clipVolume包住穿模过来的、B门所在的斜面；传送同帧的 afterTeleport
            //    调用发生在 clipVolume 还原之前，必然读到穿透层）。
            // 都取不到才退而记录穿透层本身（此时场景里它大概率本来就是穿透层）。
            // 历史教训：把穿透层误记成"原始layer"，离开时"还原"成穿透层，物体永远回不到默认层。
            if (original == playerPassThroughLayer)
            {
                if (isPortalA && layerOverrideBActive && layerOverrideColliderB == markedCollider && originalLayerB >= 0 && originalLayerB != playerPassThroughLayer)
                {
                    original = originalLayerB;
                }
                else if (!isPortalA && layerOverrideAActive && layerOverrideColliderA == markedCollider && originalLayerA >= 0 && originalLayerA != playerPassThroughLayer)
                {
                    original = originalLayerA;
                }

                if (original == playerPassThroughLayer)
                {
                    int clipOriginal = FindClipVolumeOriginalLayer(markedCollider);
                    if (clipOriginal >= 0 && clipOriginal != playerPassThroughLayer) original = clipOriginal;
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

        // 防御兜底：记录值万一被污染成穿透层本身（"还原"等于没还原、物体永远卡在穿透层），
        // 再查一次 clip volume 追踪表里的真原始值。正常路径下记录时已修正，这里防的是残余竞态。
        if (restoreLayer == playerPassThroughLayer)
        {
            int clipOriginal = FindClipVolumeOriginalLayer(markedCollider);
            if (clipOriginal >= 0 && clipOriginal != playerPassThroughLayer) restoreLayer = clipOriginal;
        }

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

        Vector3 localHeadForTrigger = LocalPointForPortal(portalPlane, playerHead);
        Vector3 localFeetForTrigger = LocalPointForPortal(portalPlane, playerFeet);

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

        if (debugTeleportVerbose && debugLogIntervalFrames > 0 && Time.frameCount % debugLogIntervalFrames == 0)
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
                        else if (debugTeleportVerbose && debugLogIntervalFrames > 0 && Time.frameCount % debugLogIntervalFrames == 0)
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
                // 2. 扫掠补救：线段与 z=±triggerOffset 平面求交，防止高速隧穿漏检。
                // 除了"从平面外侧进入"的经典情况，还覆盖【死区穿越完成】情况：
                // 上一帧 traveller 恰好落在死区(|z|<=offset，side=0)时，经典路径(oldSide!=0)
                // 和"必须从外侧进入"的旧扫掠门槛都哑火，玩家能整段穿过门而不触发（下落穿过
                // 斜向门时高发：穿越点XY在门框外、滑进死区后XY才进门框）。此时只要 lastBodySide
                // 明确记录了来向，就承认这次"从死区穿出触发平面"是一次完整穿越。
                // lastBodySide 门槛同时防误触发：传送出口恰好落在触发平面边界时，
                // TeleportSebStyle 会把 lastBodySide 种成出口侧方向，朝远离门的方向运动
                // 不会满足"来向相反"，不会立刻反向重传。
                float prevZ = previousTravellerLocal.z;
                float currZ = currentTravellerLocal.z;
                float dz = currZ - prevZ;
                if (Mathf.Abs(dz) > 0.0001f)
                {
                    // 检查 z = +triggerOffset：a) 从正侧外侧进入；b) 死区内穿出且来向是负侧(lastBodySide==-1)
                    bool prevInDeadZone = prevZ > -teleportTriggerOffset && prevZ < teleportTriggerOffset;
                    bool plusFromOutside = prevZ > teleportTriggerOffset;
                    bool plusFromDeadZone = prevInDeadZone && lastBodySide == -1;
                    float tPlus = (teleportTriggerOffset - prevZ) / dz;
                    if (tPlus >= 0f && tPlus <= 1f && (plusFromOutside || plusFromDeadZone))
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
                    // 检查 z = -triggerOffset：a) 从负侧外侧进入；b) 死区内穿出且来向是正侧(lastBodySide==1)
                    if (!crossedPlane)
                    {
                        bool minusFromOutside = prevZ < -teleportTriggerOffset;
                        bool minusFromDeadZone = prevInDeadZone && lastBodySide == 1;
                        float tMinus = (-teleportTriggerOffset - prevZ) / dz;
                        if (tMinus >= 0f && tMinus <= 1f && (minusFromOutside || minusFromDeadZone))
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

                // 注意：入口 traveller 的移除和出口 traveller 的加入都在 TeleportSebStyle 里统一处理，这里不重复 SetTravellerTracking(false)。

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

        bool flatHybridTraveller = useRootAsTraveller && useHybridRootXYHeadZTraveller && IsUpwardFacingPortal(fromPlane);

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
                if (IsUpwardFacingPortal(toPlane))
                {
                    // 出口也是朝上门（地板/天花板/斜坡）：沿出口法线把深度从 head 调整到 root。
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
        if (portalGun != null) portalGun.HandlePlayerTeleportedThroughPortal(fromPlane, toPlane, useClassicHalfTurn);
        PlayPlayerPortalExitSound(toPlane);
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
            // 出口修正可能把落点精确推到触发平面边界上，SideFromLocalZ 边界返回 0；
            // 此时必须用预期出口侧种子兜底，否则下一帧经典检测 oldSide==0 哑火，
            // 玩家可能直接穿过出口门所在平面而不触发（下落/低速场景高发）。
            int spawnSideB = SideFromLocalZ(localToB_afterTeleport.z);
            lastBodySideB = spawnSideB != 0 ? spawnSideB : (entryOldSide == 0 ? 1 : entryOldSide);
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
            // 同 fromAtoB 分支：边界落点 side==0 时用预期出口侧种子兜底。
            int spawnSideA = SideFromLocalZ(localToA_afterTeleport.z);
            lastBodySideA = spawnSideA != 0 ? spawnSideA : (entryOldSide == 0 ? 1 : entryOldSide);
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
            Vector3 localToB = LocalPointForPortal(portalParentB, newPlayerHeadPos);
            Quaternion localRotToB = Quaternion.Inverse(portalParentB.rotation) * newPlayerRot;
            if (useClassicHalfTurn)
            {
                localToB = cameraHalfTurn * localToB;
                localRotToB = cameraHalfTurn * localRotToB;
            }
            cameraA.transform.position = WorldPointFromPortal(portalParentA, localToB);
            cameraA.transform.rotation = portalParentA.rotation * localRotToB;
        }

        if (cameraB != null)
        {
            Vector3 localToA = LocalPointForPortal(portalParentA, newPlayerHeadPos);
            Quaternion localRotToA = Quaternion.Inverse(portalParentA.rotation) * newPlayerRot;
            if (useClassicHalfTurn)
            {
                localToA = cameraHalfTurn * localToA;
                localRotToA = cameraHalfTurn * localRotToA;
            }
            cameraB.transform.position = WorldPointFromPortal(portalParentB, localToA);
            cameraB.transform.rotation = portalParentB.rotation * localRotToA;
        }
    }

    // ============================================================
    // 近距离门面置顶：头部进入门框且贴近时，临时 ZTest Always。
    // ============================================================

    void UpdatePortalOverlayZTest(Vector3 playerHead)
    {
        float targetA, targetB;
        if (!enablePortalOverlayWhenHeadNear)
        {
            targetA = 4f;
            targetB = 4f;
        }
        else
        {
            bool nearA = IsHeadInPortalOverlayZone(portalPlaneA, playerHead, ResolvePortalShape(true));
            bool nearB = IsHeadInPortalOverlayZone(portalPlaneB, playerHead, ResolvePortalShape(false));
            targetA = nearA ? 8f : 4f;
            targetB = nearB ? 8f : 4f;
        }
        // 仅当 ZTest 值实际发生切换时才调用 SetFloat，避免每帧跨域写入材质
        if (Mathf.Abs(cachedLastZTestA - targetA) > 0.001f)
        {
            cachedLastZTestA = targetA;
            SetPortalZTest(portalMatA, targetA);
        }
        if (Mathf.Abs(cachedLastZTestB - targetB) > 0.001f)
        {
            cachedLastZTestB = targetB;
            SetPortalZTest(portalMatB, targetB);
        }
    }

    bool IsHeadInPortalOverlayZone(Transform portalPlane, Vector3 playerHead, int shapeType)
    {
        if (portalPlane == null) return false;
        Vector3 localHead = LocalPointForPortal(portalPlane, playerHead);
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

        // 头在传送门体积内时：跳过 oblique 斜裁剪（历史补丁：避免贴近裁剪面时法线翻转导致
        // 反向裁切/画面消失）。已知并接受副作用：贴门时门后墙背面区域可能漏进传送门画面；
        // 实测(2026-08-15)贴门强制斜裁剪的画面更糟，故锁定跳过行为，不再提供开关。
        // teleportTriggerOffset 保持 ≥0.1（默认0.3）可让正常游玩不进入贴面退化区（见交接文档）。
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
            recursiveRotationsA,
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

        if (debugRecursiveRenderLog && debugRecursiveLogIntervalFrames > 0 && Time.frameCount % debugRecursiveLogIntervalFrames == 0)
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
        portalCam.nearClipPlane = SafeCameraNearClip();
        portalCam.ResetProjectionMatrix();
        if (recursiveForceManualCamerasDisabled) portalCam.enabled = false;

        // Seb 原逻辑：从 player camera 的 localToWorldMatrix 开始，重复乘 this * linked^-1。
        // 注意这里 linkedParent 是玩家正在看的门，thisParent 是门后出口。
        Matrix4x4 localToWorldMatrix = Matrix4x4.TRS(viewerPos, viewerRot, Vector3.one);
        Matrix4x4 linkedWorldToLocal = Matrix4x4.TRS(linkedParent.position, linkedParent.rotation, Vector3.one).inverse;
        Matrix4x4 thisLocalToWorld = Matrix4x4.TRS(thisParent.position, thisParent.rotation, Vector3.one);
        Matrix4x4 halfTurnMatrix = Matrix4x4.Rotate(Quaternion.AngleAxis(180f, Vector3.up));
        bool renderHalfTurn = useClassicHalfTurn || recursiveRenderUseClassicHalfTurn;

        bool useMask = enablePortalMaskCulling && recursiveEarlyStop && !skipAllOblique
            && PortalMaterialSupportsRenderMask(linkedMat) && PortalMaterialSupportsRenderMask(thisMat);
        if (useMask)
        {
            ResetPortalRenderMask(0.025f);
            // A near viewer always gets the first image, but deeper invisible layers can still stop.
            if (!IsNearPortalForRendering(linkedPlane, viewerPos))
            {
                if (!IntersectPortalRenderMask(linkedPlane, viewerPos, viewerRot, syncFOV, portalCam.aspect))
                {
                    // Root visibility has a closing grace frame; never bypass it here.
                    ResetPortalRenderMask(0.025f);
                }
            }
        }
        Matrix4x4 portalMapping = renderHalfTurn
            ? thisLocalToWorld * halfTurnMatrix * linkedWorldToLocal : thisLocalToWorld * linkedWorldToLocal;
        int startIndex = limit;
        int count = 0;

        for (int i = 0; i < limit; i++)
        {
            if (i > 0 && recursiveEarlyStop && !skipAllOblique)
            {
                // Keep the existing near-plane exception; nested masks share the portal texture UV space.
                if (useMask)
                {
                    if (!IntersectPortalRenderMask(linkedPlane, portalCam.transform.position, portalCam.transform.rotation, syncFOV, portalCam.aspect)) break;
                }
                else if (!PortalBoundsOverlapCameraView(portalCam, linkedPlane)) break;
            }

            localToWorldMatrix = portalMapping * localToWorldMatrix;

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
        portalCam.nearClipPlane = SafeCameraNearClip();
        portalCam.ResetProjectionMatrix();

        return count;
    }

    void SyncRecursiveNearClipToPortalPlane(Camera cam, Transform clipPlane, int renderIndex, int startIndex)
    {
        if (cam == null) return;

        if (!recursiveSyncNearClipToPortalPlane || clipPlane == null)
        {
            cam.nearClipPlane = SafeCameraNearClip();
            cam.ResetProjectionMatrix();
            return;
        }

        // 普通 Camera.nearClipPlane 是垂直于 cam.forward 的平面。
        // 这里先把 near 推到“沿相机 forward 到传送门平面”的距离之后，
        // 再叠加 oblique clip，把真正裁剪面贴到 portal plane。
        // 这能修复 VRChat/透明门面/递归 RT 下第一层 near 仍停在 0.01 导致看到门背面或下一层画面的情况。
        float forwardDst = Vector3.Dot(clipPlane.position - cam.transform.position, cam.transform.forward);
        float safeNear = SafeCameraNearClip();
        float newNear = safeNear;

        if (forwardDst > safeNear)
        {
            newNear = forwardDst + Mathf.Abs(recursiveDynamicNearClipPadding);
            if (newNear < safeNear) newNear = safeNear;
            if (newNear > recursiveDynamicNearClipMax) newNear = recursiveDynamicNearClipMax;
        }

        cam.nearClipPlane = newNear;
        cam.ResetProjectionMatrix();

        if (debugRecursiveClipLog && debugRecursiveLogIntervalFrames > 0 && Time.frameCount % debugRecursiveLogIntervalFrames == 0)
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
            // 只设 GPU oblique 投影，不碰 cullingMatrix。递归动态 near 下 CPU culling frustum 会被污染，
            // 导致转头时地板/递归门/玩家整片消失；直接让 CPU 使用默认 frustum 最稳。
            cam.ResetProjectionMatrix();
            cam.projectionMatrix = cam.CalculateObliqueMatrix(clipPlaneCameraSpace);
        }
        else
        {
            cam.ResetProjectionMatrix();
        }
    }

    bool IsNearPortalForRendering(Transform portal, Vector3 viewer)
    {
        if (portal == null) return false;
        Vector3 local = LocalPointForPortal(portal, viewer);
        Vector3 nearest = new Vector3(Mathf.Clamp(local.x, -portalTriggerWidth * 0.5f, portalTriggerWidth * 0.5f),
            Mathf.Clamp(local.y, -portalTriggerHeight * 0.5f, portalTriggerHeight * 0.5f), 0f);
        float distance = Mathf.Max(0f, portalRenderNearDistance);
        return (WorldPointFromPortal(portal, nearest) - viewer).sqrMagnitude <= distance * distance;
    }

    bool IsPortalInViewerMask(Transform portal, Vector3 viewer, Quaternion rotation, float fov, float aspect)
    {
        if (portal == null || !portal.gameObject.activeInHierarchy) return false;
        if (IsNearPortalForRendering(portal, viewer)) return true;
        float extent = new Vector2(portalTriggerWidth, portalTriggerHeight).magnitude * 0.5f;
        if (!useScaleFreePortalMatrix) extent *= portal.lossyScale.magnitude;
        float distance = Mathf.Max(0f, maxRenderDistance) + extent;
        if ((portal.position - viewer).sqrMagnitude > distance * distance) return false;
        // The shader's distorted background need not be confined to the ordinary aperture.
        Material mat = portal == portalPlaneA ? portalMatA : portalMatB;
        if (!PortalMaterialSupportsRenderMask(mat)) return true;
        ResetPortalRenderMask(isVRPlayer ? 0.1f : 0.025f);
        return IntersectPortalRenderMask(portal, viewer, rotation, fov, aspect);
    }

    bool PortalMaterialSupportsRenderMask(Material mat)
    {
        if (mat == null || !mat.HasProperty("_Transition") || !mat.HasProperty("_OffsetY")) return false;
        return mat.GetFloat("_Transition") >= 0.999f && Mathf.Abs(mat.GetFloat("_OffsetY")) < 0.0001f;
    }

    void ResetPortalRenderMask(float padding)
    {
        if (portalRenderMask == null || portalRenderMask.Length < 64
            || portalRenderMaskScratch == null || portalRenderMaskScratch.Length < 64
            || portalProjectedQuad == null || portalProjectedQuad.Length < 4
            || portalViewQuad == null || portalViewQuad.Length < 4)
        {
            // Udon proxy reloads can turn null arrays into empty arrays.
            // Eight intersections of convex quads need at most 36 vertices.
            portalRenderMask = new Vector2[64];
            portalRenderMaskScratch = new Vector2[64];
            portalProjectedQuad = new Vector2[4];
            portalViewQuad = new Vector3[4];
        }
        portalRenderMask[0] = new Vector2(-padding, -padding);
        portalRenderMask[1] = new Vector2(1f + padding, -padding);
        portalRenderMask[2] = new Vector2(1f + padding, 1f + padding);
        portalRenderMask[3] = new Vector2(-padding, 1f + padding);
        portalRenderMaskCount = 4;
    }

    float PortalMaskCross(Vector2 a, Vector2 b)
    {
        return a.x * b.y - a.y * b.x;
    }

    bool IntersectPortalRenderMask(Transform portal, Vector3 viewer, Quaternion rotation, float fov, float aspect)
    {
        if (portal == null) return true;
        float padding = isVRPlayer ? 0.12f : 0.02f;
        float hx = portalTriggerWidth * 0.5f + padding;
        float hy = portalTriggerHeight * 0.5f + padding;
        Quaternion inverseView = Quaternion.Inverse(rotation);
        int front = 0;
        int behind = 0;
        for (int i = 0; i < 4; i++)
        {
            Vector3 corner = new Vector3(i == 0 || i == 3 ? -hx : hx, i < 2 ? -hy : hy, 0f);
            Vector3 view = inverseView * (WorldPointFromPortal(portal, corner) - viewer);
            portalViewQuad[i] = view;
            if (view.z > 0.001f) front++;
            if (view.z < -0.001f) behind++;
        }
        if (behind == 4) return false;
        // Near-plane straddling is deliberately fail-open, never project negative depths.
        if (front < 4) return true;
        float tanHalf = Mathf.Tan(Mathf.Clamp(fov, 1f, 179f) * Mathf.Deg2Rad * 0.5f);
        if (aspect <= 0f || tanHalf <= 0f) return true;
        for (int i = 0; i < 4; i++)
        {
            Vector3 view = portalViewQuad[i];
            Vector2 point = new Vector2(0.5f + view.x / (view.z * tanHalf * aspect * 2f),
                0.5f + view.y / (view.z * tanHalf * 2f));
            if (float.IsNaN(point.x) || float.IsNaN(point.y) || float.IsInfinity(point.x) || float.IsInfinity(point.y)) return true;
            portalProjectedQuad[i] = point;
        }
        float area = 0f;
        for (int i = 0; i < 4; i++) area += PortalMaskCross(portalProjectedQuad[i], portalProjectedQuad[(i + 1) % 4]);
        if (Mathf.Abs(area) < 0.00000001f) return true;
        float winding = area > 0f ? 1f : -1f;
        for (int edgeIndex = 0; edgeIndex < 4; edgeIndex++)
        {
            Vector2 a = portalProjectedQuad[edgeIndex];
            Vector2 edge = portalProjectedQuad[(edgeIndex + 1) % 4] - a;
            float tolerance = 0.00001f * edge.magnitude;
            int outputCount = 0;
            Vector2 previous = portalRenderMask[portalRenderMaskCount - 1];
            float previousDistance = winding * PortalMaskCross(edge, previous - a) + tolerance;
            for (int i = 0; i < portalRenderMaskCount; i++)
            {
                Vector2 current = portalRenderMask[i];
                float currentDistance = winding * PortalMaskCross(edge, current - a) + tolerance;
                if ((previousDistance >= 0f) != (currentDistance >= 0f))
                {
                    if (outputCount >= portalRenderMaskScratch.Length) return true;
                    float t = previousDistance / (previousDistance - currentDistance);
                    portalRenderMaskScratch[outputCount++] = previous + (current - previous) * t;
                }
                if (currentDistance >= 0f)
                {
                    if (outputCount >= portalRenderMaskScratch.Length) return true;
                    portalRenderMaskScratch[outputCount++] = current;
                }
                previous = current;
                previousDistance = currentDistance;
            }
            if (outputCount == 0) return false;
            Vector2[] swap = portalRenderMask;
            portalRenderMask = portalRenderMaskScratch;
            portalRenderMaskScratch = swap;
            portalRenderMaskCount = outputCount;
        }
        return true;
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

        Vector3 p0 = WorldPointFromPortal(portalPlane, new Vector3(-hx, -hy, 0f));
        Vector3 p1 = WorldPointFromPortal(portalPlane, new Vector3(-hx,  hy, 0f));
        Vector3 p2 = WorldPointFromPortal(portalPlane, new Vector3( hx, -hy, 0f));
        Vector3 p3 = WorldPointFromPortal(portalPlane, new Vector3( hx,  hy, 0f));

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
        // 驱动 shader 的 _TexWidth/_TexHeight：aspect 计算依赖此值，VR 单眼 RT 通常不是 16:9
        if (portalMatA != null && texA != null && (texA.width != cachedPortalTexWidthA || texA.height != cachedPortalTexHeightA))
        {
            portalMatA.SetFloat("_TexWidth", texA.width);
            portalMatA.SetFloat("_TexHeight", texA.height);
            cachedPortalTexWidthA = texA.width;
            cachedPortalTexHeightA = texA.height;
        }

        if (portalMatB != null && texB != null && cachedPortalTextureB != texB)
        {
            portalMatB.SetTexture("_MainTex", texB);
            cachedPortalTextureB = texB;
        }
        if (portalMatB != null && texB != null && (texB.width != cachedPortalTexWidthB || texB.height != cachedPortalTexHeightB))
        {
            portalMatB.SetFloat("_TexWidth", texB.width);
            portalMatB.SetFloat("_TexHeight", texB.height);
            cachedPortalTexWidthB = texB.width;
            cachedPortalTexHeightB = texB.height;
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
        Vector3 localCamPos = LocalPointForPortal(portalPlane, cam.transform.position);
        return Mathf.Abs(localCamPos.z) < noClipDepth;
    }

    void ApplyObliqueClipping(Camera cam, Transform portalPlane)
    {
        // 注意：递归路径使用 ApplyObliqueClippingSebStyle（正值偏移 + 专门公式），
        // 本函数只在非递归/头在门内时兜底使用。flipSide 参数从未被以 true 调用过，已移除冗余重载。
        Vector3 camPos = cam.transform.position;
        Vector3 planeNormal = portalPlane.forward;

        float side = Mathf.Sign(Vector3.Dot(planeNormal, portalPlane.position - camPos));
        if (Mathf.Abs(side) < 0.5f) side = 1f;

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

        // 只设 GPU oblique 投影，不碰 cullingMatrix（CPU frustum 保持默认以避免视锥畸形导致物体消失）。
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
    // 刚体追踪图层控制：进入时 renderQueue 3001（高于传送门 Transparent=3000），离开还原 3000
    // ============================================================

    private void ApplyPortalOverlayToGameObject(GameObject go)
    {
        if (go == null) return;
        Renderer[] renderers = go.GetComponentsInChildren<Renderer>(true);
        for (int i = 0; i < renderers.Length; i++)
        {
            Renderer r = renderers[i];
            if (r == null) continue;
            Material mat = r.sharedMaterial;
            if (mat != null)
            {
                mat.renderQueue = 3001;
            }
        }
    }

    private void RemovePortalOverlayFromGameObject(GameObject go)
    {
        if (go == null) return;
        Renderer[] renderers = go.GetComponentsInChildren<Renderer>(true);
        for (int i = 0; i < renderers.Length; i++)
        {
            Renderer r = renderers[i];
            if (r == null) continue;
            Material mat = r.sharedMaterial;
            if (mat != null)
            {
                mat.renderQueue = 3000;
            }
        }
    }

    // 刚体彻底离开 A/B 两侧追踪时，把材质 renderQueue 还原回 3000（兑现本小节"离开还原"的注释承诺）。
    // 之前只进不出：刚体离开门区域后 renderQueue 永远停在 3001，直到世界重载。
    // 另一扇门仍在追踪时（比如刚体刚穿门、对面门已接管）不能还原，否则遮罩效果断裂。
    private void RestorePortalOverlayIfUntracked(Rigidbody rb)
    {
        if (rb == null) return;
        if (IsRigidbodyTrackedByEitherPortal(rb)) return;
        RemovePortalOverlayFromGameObject(rb.gameObject);
    }

    // ============================================================
    // 刚体传送核心：SebLague traveller 逻辑的 Udon 固定数组版
    // ============================================================

    public void ProcessHeldRigidbodyPhysics()
    {
        if (!enabled || !enableRigidbodyTeleport || !allowHeldRigidbodyTeleport || portalGun == null || portalGun.GetHeldRigidbody() == null) return;
        if (portalPlaneA == null || portalPlaneB == null || !portalPlaneA.gameObject.activeInHierarchy || !portalPlaneB.gameObject.activeInHierarchy) return;
        ProcessRigidbodyForPortal(true, true);
        if (portalGun.GetHeldRigidbody() != null) ProcessRigidbodyForPortal(false, true);
    }

    public bool HeldBoundsFitPortal(Transform portal, Bounds bounds, Vector3 displacement, float padding)
    {
        if (portal == null || (portal != portalPlaneA && portal != portalPlaneB)) return false;
        Collider[] colliders = portalGun == null ? null : portalGun.GetHeldBodyColliders();
        if (colliders == null || colliders.Length == 0) return false;
        int shape = ResolvePortalShape(portal == portalPlaneA);
        int exitShape = ResolvePortalShape(portal != portalPlaneA);
        for (int c = 0; c < colliders.Length; c++)
        {
            Collider collider = colliders[c];
            if (collider == null || !collider.enabled || collider.isTrigger || collider.attachedRigidbody != portalGun.GetHeldRigidbody()) continue;
            if (collider.GetType() == typeof(BoxCollider))
            {
                BoxCollider box = (BoxCollider)collider;
                Vector3 scale = box.transform.lossyScale;
                Vector3 localPadding = new Vector3(padding / Mathf.Max(Mathf.Abs(scale.x), 0.0001f),
                    padding / Mathf.Max(Mathf.Abs(scale.y), 0.0001f), padding / Mathf.Max(Mathf.Abs(scale.z), 0.0001f));
                Vector3 half = box.size * 0.5f + localPadding;
                for (int i = 0; i < 8; i++)
                {
                    Vector3 localCorner = box.center + new Vector3((i & 1) == 0 ? -half.x : half.x, (i & 2) == 0 ? -half.y : half.y, (i & 4) == 0 ? -half.z : half.z);
                    Vector3 portalLocal = LocalPointForPortal(portal, collider.transform.TransformPoint(localCorner) + displacement);
                    if (!LocalPointInPortalRect(portalLocal, shape) || !LocalPointInPortalRect(portalLocal, exitShape)) return false;
                }
            }
            else
            {
                Bounds colliderBounds = collider.bounds;
                Vector3 extents = colliderBounds.extents + Vector3.one * padding;
                for (int i = 0; i < 8; i++)
                {
                    Vector3 corner = colliderBounds.center + displacement + new Vector3((i & 1) == 0 ? -extents.x : extents.x, (i & 2) == 0 ? -extents.y : extents.y, (i & 4) == 0 ? -extents.z : extents.z);
                    Vector3 portalLocal = LocalPointForPortal(portal, corner);
                    if (!LocalPointInPortalRect(portalLocal, shape) || !LocalPointInPortalRect(portalLocal, exitShape)) return false;
                }
            }
        }
        return true;
    }

    public void ReleaseHeldPortalPhysics(Rigidbody rb)
    {
        if (rb != null) RestoreRigidbodyPortalWallIgnore(rb);
    }

    private Collider GetPortalWallCollider(bool isPortalA)
    {
        Collider marked = portalGun == null ? null : (isPortalA ? portalGun.GetMarkedColliderA() : portalGun.GetMarkedColliderB());
        return marked != null ? marked : (isPortalA ? portalWallColliderA : portalWallColliderB);
    }

    private void ApplyRigidbodyPortalWallIgnore(Rigidbody rb, bool isPortalA)
    {
        if (rb == null) return;
        for (int i = 0; i < rbWallIgnorePairCount; i++)
        {
            if (rbWallIgnoreRigidbodies[i] == rb && IsRigidbodyPortalWallOwner(i, isPortalA)) rbWallIgnoreSeen[i] = false;
        }

        Collider[] bodies = rb.GetComponentsInChildren<Collider>(true);
        BoxCollider clipVolume = isPortalA ? clipVolumeColliderA : clipVolumeColliderB;
        if (clipVolume != null && clipVolume.enabled)
        {
            Transform clipT = clipVolume.transform;
            Vector3 worldCenter = clipT.TransformPoint(clipVolume.center);
            Vector3 halfExtents = Vector3.Scale(clipVolume.size * 0.5f, clipT.lossyScale);
            int overlapCount = Physics.OverlapBoxNonAlloc(worldCenter, halfExtents, clipVolumeOverlapBuffer,
                clipT.rotation, ~0, QueryTriggerInteraction.Ignore);
            Transform portalRoot = isPortalA ? portalParentA : portalParentB;
            for (int i = 0; i < overlapCount; i++)
            {
                Collider wall = clipVolumeOverlapBuffer[i];
                if (wall == null || wall == clipVolume || wall.isTrigger || wall.attachedRigidbody != null
                    || IsColliderUnderPortalHierarchy(wall, portalRoot)) continue;
                AddRigidbodyPortalWallIgnorePair(rb, bodies, wall, isPortalA);
            }
        }
        else
        {
            AddRigidbodyPortalWallIgnorePair(rb, bodies, GetPortalWallCollider(isPortalA), isPortalA);
        }

        for (int i = rbWallIgnorePairCount - 1; i >= 0; i--)
        {
            if (rbWallIgnoreRigidbodies[i] == rb && IsRigidbodyPortalWallOwner(i, isPortalA) && !rbWallIgnoreSeen[i])
                ReleaseRigidbodyPortalWallOwnerAt(i, isPortalA);
        }
    }

    private void AddRigidbodyPortalWallIgnorePair(Rigidbody rb, Collider[] bodies, Collider wall, bool isPortalA)
    {
        if (rb == null || wall == null || wall.attachedRigidbody != null || !wall.enabled || wall.isTrigger) return;
        if (bodies == null) return;
        for (int i = 0; i < bodies.Length; i++)
        {
            Collider body = bodies[i];
            if (body == null || !body.enabled || body.isTrigger || body.attachedRigidbody != rb) continue;
            int existing = -1;
            for (int p = 0; p < rbWallIgnorePairCount; p++)
            {
                if (rbWallIgnoreRigidbodies[p] == rb && rbWallIgnoreBodies[p] == body && rbWallIgnoreWalls[p] == wall)
                { existing = p; break; }
            }
            if (existing >= 0)
            {
                SetRigidbodyPortalWallOwner(existing, isPortalA, true);
                rbWallIgnoreSeen[existing] = true;
                continue;
            }
            if (rbWallIgnorePairCount >= MAX_RB_WALL_IGNORE_PAIRS) return;
            int slot = rbWallIgnorePairCount++;
            rbWallIgnoreRigidbodies[slot] = rb; rbWallIgnoreBodies[slot] = body; rbWallIgnoreWalls[slot] = wall;
            rbWallIgnoreOriginalStates[slot] = Physics.GetIgnoreCollision(body, wall);
            rbWallIgnorePortalA[slot] = isPortalA; rbWallIgnorePortalB[slot] = !isPortalA; rbWallIgnoreSeen[slot] = true;
            Physics.IgnoreCollision(body, wall, true);
        }
    }

    private void RestoreRigidbodyPortalWallIgnore(Rigidbody rb)
    {
        for (int i = rbWallIgnorePairCount - 1; i >= 0; i--)
        {
            if (rb != null && rbWallIgnoreRigidbodies[i] != rb) continue;
            RestoreRigidbodyPortalWallIgnoreAt(i);
        }
    }

    private void RestoreRigidbodyPortalWallIgnoreForPortal(Rigidbody rb, bool isPortalA)
    {
        for (int i = rbWallIgnorePairCount - 1; i >= 0; i--)
        {
            if (rbWallIgnoreRigidbodies[i] != rb || !IsRigidbodyPortalWallOwner(i, isPortalA)) continue;
            ReleaseRigidbodyPortalWallOwnerAt(i, isPortalA);
        }
    }

    private bool IsRigidbodyPortalWallOwner(int index, bool isPortalA)
    {
        return isPortalA ? rbWallIgnorePortalA[index] : rbWallIgnorePortalB[index];
    }

    private void SetRigidbodyPortalWallOwner(int index, bool isPortalA, bool value)
    {
        if (isPortalA) rbWallIgnorePortalA[index] = value;
        else rbWallIgnorePortalB[index] = value;
    }

    private void ReleaseRigidbodyPortalWallOwnerAt(int index, bool isPortalA)
    {
        SetRigidbodyPortalWallOwner(index, isPortalA, false);
        if (!rbWallIgnorePortalA[index] && !rbWallIgnorePortalB[index]) RestoreRigidbodyPortalWallIgnoreAt(index);
    }

    private void RestoreRigidbodyPortalWallIgnoreAt(int index)
    {
        Collider body = rbWallIgnoreBodies[index]; Collider wall = rbWallIgnoreWalls[index];
        if (body != null && wall != null) Physics.IgnoreCollision(body, wall, rbWallIgnoreOriginalStates[index]);
        int last = --rbWallIgnorePairCount;
        rbWallIgnoreRigidbodies[index] = rbWallIgnoreRigidbodies[last]; rbWallIgnoreBodies[index] = rbWallIgnoreBodies[last];
        rbWallIgnoreWalls[index] = rbWallIgnoreWalls[last]; rbWallIgnoreOriginalStates[index] = rbWallIgnoreOriginalStates[last];
        rbWallIgnorePortalA[index] = rbWallIgnorePortalA[last]; rbWallIgnorePortalB[index] = rbWallIgnorePortalB[last]; rbWallIgnoreSeen[index] = rbWallIgnoreSeen[last];
        rbWallIgnoreRigidbodies[last] = null; rbWallIgnoreBodies[last] = null; rbWallIgnoreWalls[last] = null; rbWallIgnorePortalA[last] = false; rbWallIgnorePortalB[last] = false; rbWallIgnoreSeen[last] = false;
    }

    private void CleanupRigidbodyPortalWallIgnores()
    {
        for (int i = rbWallIgnorePairCount - 1; i >= 0; i--)
        {
            Rigidbody rb = rbWallIgnoreRigidbodies[i];
            if (rb == null)
            {
                RestoreRigidbodyPortalWallIgnoreAt(i);
                continue;
            }
            if (rbWallIgnorePortalA[i] && !IsRigidbodyTrackedByPortal(true, rb)) ReleaseRigidbodyPortalWallOwnerAt(i, true);
            if (i < rbWallIgnorePairCount && rbWallIgnoreRigidbodies[i] == rb && rbWallIgnorePortalB[i]
                && !IsRigidbodyTrackedByPortal(false, rb)) ReleaseRigidbodyPortalWallOwnerAt(i, false);
        }
    }

    private void ProcessRigidbodyTravellers()
    {
        if (portalPlaneA == null || portalPlaneB == null) return;
        CleanupRigidbodyPortalWallIgnores();

        // 等价于 Seb 原版每个 Portal 在 LateUpdate 里 HandleTravellers。
        ProcessRigidbodyForPortal(true, false);
        ProcessRigidbodyForPortal(false, false);
    }

    // ============================================================
    // 粒子传送：白名单系统里穿过门平面的粒子，位置+速度按门映射传到另一侧。
    // 设计要点（六轮，window=0 零漏检架构）：
    // - 身份配对：randomSeed（出生时分配、终生不变的稳定ID）按槽位认同"同一颗粒子"；
    //   GetParticles 槽位顺序在粒子死亡时会洗牌（Unity官方论坛实证），配对失败即走"首见"路径。
    // - 零漏检四条规则：
    //   规则1 本帧穿越：配对实测位移（或出生反推）线段与检测面符号翻转求交——任意速度必抓；
    //         符号距离插值对退化线段也成立（门移动扫过静止粒子照样抓）；双向都收。
    //   规则2 门洞捕获（二十一轮起无限深度）：已在检测面后侧且【门框内】→ 补抓。
    //         深度不限（particleTeleportRetroWindow<=0 时）：框内+门后=进了门洞，任意速度
    //         零漏——漂移入框（框外穿面、之后横向飘进框）无论漂到多深都被收走。
    //         retroWindow>0 时退化为老式深度上限（旧场景兼容）。
    //         （六轮修正：旧版窗符号写反，抓的是门前侧靠近的粒子，后侧漏检从来抓不到。）
    //   规则3 首见后侧兜底：未配对粒子当前位置已在检测面后侧且在门框内 → 立即传送——
    //         覆盖一切"穿越那一刻无记录"的情况（出生即穿越/出生在墙后/注册晚了/槽位洗牌/缓冲扩容帧）。
    //   规则4 反弹捕获v2（十七轮重构，带碰撞粒子专用）：配对粒子门法向速度一帧内反转
    //         （朝门→离门）→ 用前帧位置/前帧速度/本帧速度沿门法线反解【反弹发生时刻与
    //         反弹点】（一元一次方程，数值模拟验证误差≈机器精度1e-14），验证反弹点落在
    //         门框内 → 它被门面所在墙体弹回=到过门口的铁证。旧版检查"帧末位置在门面前
    //         1.05m窗口内"，高速粒子（速度≥约40时）帧内反弹后帧末已弹回数米远，窗口
    //         漏捕高达90%（数值模拟实测）——v2与速度无关，用反弹前速度映射，
    //         锚点投影到检测面防乒乓。继承 Unity 粒子碰撞的防隧穿。
    // - 缓冲自动扩容：活粒子顶满缓冲 → 自动翻倍补读，超出部分不再被永久漏掉。
    // - 一帧至多一次穿越：两门都命中取"门框内实穿越里 t 最早的"——先碰先进（九轮修复：
    //   旧"取更晚"在剪刀重叠门+高速长线段下把粒子按错误的门映射，是高速滞留隧穿真凶）。
    // - 矩阵每帧只构造4次（A/B各一套worldToLocal/localToWorld，与 useScaleFreePortalMatrix
    //   的数学完全一致），粒子循环里只有 MultiplyPoint/MultiplyVector，无 TRS/inverse。
    // - 性能闸门：白名单/收集根自动收集 + 距离闸门 + 缓冲初始大小（自动扩容）。
    // - 检测面沿法线外推 particleTeleportPlaneOffset：贴墙/地板门 + 带碰撞粒子的场景，
    //   粒子会被墙体在门平面处弹走永远穿不过平面，外推检测面让粒子撞墙前就传送。
    // - 空间语义（自动分流）：每系统处理时读主模块 simulationSpace，枚举数值按 Unity
    //   参考源码（ParticleSystemEnums.cs）：Local=0 经系统Transform局部↔世界转换；
    //   World=1 直通世界坐标；Custom=2 按Local处理（假设customSimulationSpace=自身
    //   Transform，警告一次）。
    // ============================================================

    private void ProcessParticleTeleports()
    {
        // 十六轮：手动白名单 portalParticleSystems 已按用户裁决移除，注册只剩自动收集
        // （root扫描/放置发现/收集根）。两路注册源全空才跳过。
        bool hasDiscovered = discoveredParticleSystems != null && discoveredParticleSystems.Length > 0;
        bool hasPlaced = placedDiscoverySystems != null && placedDiscoveryCount > 0;
        if (!hasDiscovered && !hasPlaced) return;
        if (portalPlaneA == null || portalPlaneB == null) return;

        int bufferSize = Mathf.Max(32, particleTeleportBufferSize);
        if (particleTeleportBuffer == null || particleTeleportBuffer.Length < bufferSize)
        {
            GrowParticleArrays(bufferSize);
        }

        float dt = Time.deltaTime;
        if (dt <= 0f) return;
        // 引擎推进粒子用的是【上一帧】的 dt（粒子模拟在 PostLateUpdate，实测已证）。
        // 帧时一抖，用本帧 dt 去校验就会误杀有效身份证 —— 取两帧较大者做上界。
        float stepDt = (particleLastFrameDt > dt) ? particleLastFrameDt : dt;
        particleLastFrameDt = dt;
        UpdateWallProbe(dt);   // 射线量墙 → 决定本帧实际使用的检测面外扩量

        // 二十轮：帧内去重——同一系统可能同时出现在 root扫描列表 和 放置发现列表
        // （定时刷新重填 root 列表时不查放置列表），同帧处理两次会让检测计数翻倍、
        // 且第二遍重复写配对记录。每帧重置已处理列表。
        processedSystemsThisFrameCount = 0;

        // 本帧门户矩阵缓存（scale-free，与 LocalPointForPortal 的默认数学一致）
        Matrix4x4 worldToLocalA = Matrix4x4.TRS(portalPlaneA.position, portalPlaneA.rotation, Vector3.one).inverse;
        Matrix4x4 localToWorldA = Matrix4x4.TRS(portalPlaneA.position, portalPlaneA.rotation, Vector3.one);
        Matrix4x4 worldToLocalB = Matrix4x4.TRS(portalPlaneB.position, portalPlaneB.rotation, Vector3.one).inverse;
        Matrix4x4 localToWorldB = Matrix4x4.TRS(portalPlaneB.position, portalPlaneB.rotation, Vector3.one);
        int shapeA = ResolvePortalShape(true);
        int shapeB = ResolvePortalShape(false);
        float maxDistSqr = particleTeleportMaxDistance * particleTeleportMaxDistance;

        // root扫描+收集根 合并列表
        if (discoveredParticleSystems != null)
        {
            for (int s = 0; s < discoveredParticleSystems.Length; s++)
            {
                ProcessSingleParticleSystem(discoveredParticleSystems[s], worldToLocalA, localToWorldA, worldToLocalB, localToWorldB, shapeA, shapeB, dt, stepDt, maxDistSqr);
            }
        }
        // 放置时发现注册表（第三路）
        if (placedDiscoverySystems != null)
        {
            for (int s = 0; s < placedDiscoveryCount; s++)
            {
                ProcessSingleParticleSystem(placedDiscoverySystems[s], worldToLocalA, localToWorldA, worldToLocalB, localToWorldB, shapeA, shapeB, dt, stepDt, maxDistSqr);
            }
        }

        // 诊断日志：每60帧汇总一次，定位"隧穿不传送"卡在哪一环
        if (debugParticleTeleportLog)
        {
            particleDebugFrameCounter++;
            if (particleDebugFrameCounter >= 60)
            {
                particleDebugFrameCounter = 0;
                int rootListCount = discoveredParticleSystems != null ? discoveredParticleSystems.Length : 0;
                Debug.Log("[粒子传送] 最近60帧：检测 " + particleDebugTestedCount + " 个粒子次，传送 " + particleDebugTeleportCount + " 次（其中反弹捕获 " + particleDebugBounceCount + "），门后框内滞留 " + particleDebugStuckCount + " 颗次，缓冲扩容 " + particleDebugTruncatedSystems + " 次（已注册：root扫描" + rootListCount + " + 放置发现" + placedDiscoveryCount + "，缓冲初始" + particleTeleportBufferSize + "自动扩容；事件日志共" + particleDebugEventLogged + "条，限流省略" + particleDebugEventSkipped + "条）"
                    + "｜身份证 命中" + debugPassportUsed + " 新生" + debugPassportMissing
                    + " 闸门误杀" + debugGateRejected
                    + "｜预测捕获 " + debugPredictiveHits + " / 规则5 " + debugRule5
                    + "｜B门后真漏 " + debugTrueLeakB
                    + "｜背离门拒收 " + debugRejectedOutbound
                    + "｜检测面 用户" + particleTeleportPlaneOffset.ToString("F3") + " → 实际" + effPlaneOffset.ToString("F3") + "m");
                particleDebugTestedCount = 0;
                particleDebugTeleportCount = 0;
                particleDebugBounceCount = 0;
                particleDebugStuckCount = 0;
                particleDebugStuckSamples = 0;
                particleDebugTruncatedSystems = 0;
                particleDebugEventLogged = 0;
                particleDebugEventSkipped = 0;
                debugPassportUsed = 0;
                debugPassportMissing = 0;
                debugGateRejected = 0;
                debugPredictiveHits = 0;
                debugRule5 = 0;
                debugTrueLeakA = 0;
                debugTrueLeakB = 0;
                debugTrueLeakDepthA = 0f;
                debugTrueLeakDepthB = 0f;
                debugRejectedOutbound = 0;
                debugBounceOutOfFrame = 0;
                debugNewbornTotal = 0;
            }
        }
    }

    // 单个粒子系统一帧的穿越检测与传送。
    private void ProcessSingleParticleSystem(ParticleSystem ps, Matrix4x4 worldToLocalA, Matrix4x4 localToWorldA, Matrix4x4 worldToLocalB, Matrix4x4 localToWorldB, int shapeA, int shapeB, float dt, float stepDt, float maxDistSqr)
    {
        if (ps == null) return;

        // 二十轮：帧内去重（同一系统跨列表重复注册时只处理一次）
        // ⚠ 合并时必须保留：独立脚本把去重放在 ProcessParticleTeleports，主管理器放在这里。
        for (int q = 0; q < processedSystemsThisFrameCount; q++)
        {
            if (processedSystemsThisFrame[q] == ps) return;
        }
        if (processedSystemsThisFrameCount < processedSystemsThisFrame.Length)
        {
            processedSystemsThisFrame[processedSystemsThisFrameCount] = ps;
            processedSystemsThisFrameCount++;
        }

        // 自动空间识别（枚举值: Local=0, World=1, Custom=2）
        int simSpace = (int)ps.main.simulationSpace;
        bool localMode = (simSpace == 0 || simSpace == 2);
        if (simSpace == 2) WarnCustomSpaceOnce(ps);

        // 十一轮：排除根拦截（黑名单）—— 枪载【World空间】系统（枪口特效）自动排除防鬼畜；
        // 枪载【Local空间】系统（激光，用户需求核心）放行。手动排除根对一切生效。
        // ⚠ 合并时必须保留：独立脚本没有这条，丢了会让传送枪的激光特效重新被传送鬼畜。
        if (IsParticleSystemExcluded(ps, localMode)) return;

        // 身份证通道可用性：粒子系统若启用了旋转模块，axisOfRotation 会被引擎当作真实旋转轴使用，
        // 此时占用它会造成视觉异常 —— 自动回退到旧的下标配对，并且只警告一次。
        bool passportOK = useParticlePassport;
        if (passportOK && (ps.rotationOverLifetime.enabled || ps.rotationBySpeed.enabled))
        {
            passportOK = false;
            WarnPassportDisabledOnce(ps);
        }

        // 距离闸门
        Vector3 sysPos = ps.transform.position;
        if ((sysPos - portalPlaneA.position).sqrMagnitude > maxDistSqr &&
            (sysPos - portalPlaneB.position).sqrMagnitude > maxDistSqr)
        {
            return;
        }

        int aliveCount = ps.GetParticles(particleTeleportBuffer);
        if (aliveCount <= 0) return;
        if (aliveCount >= particleTeleportBuffer.Length)
        {
            if (debugParticleTeleportLog) particleDebugTruncatedSystems++;
            GrowParticleArrays(aliveCount * 2);
            aliveCount = ps.GetParticles(particleTeleportBuffer);
            if (aliveCount <= 0) return;
        }

        int sysSlot = GetOrAssignPairingSlot(ps);

        bool changed = false;
        Transform sysT = localMode ? ps.transform : null;

        for (int i = 0; i < aliveCount; i++)
        {
            ParticleSystem.Particle p = particleTeleportBuffer[i];
            uint seed = p.randomSeed;

            Vector3 curPos = localMode ? sysT.TransformPoint(p.position) : p.position;
            Vector3 worldVel = localMode ? sysT.TransformVector(p.velocity) : p.velocity;

            int pairIdx = (sysSlot >= 0 && i < PARTICLE_PAIRING_STRIDE) ? sysSlot * PARTICLE_PAIRING_STRIDE + i : -1;
            bool paired = pairIdx >= 0 && particlePrevValid[pairIdx] && particlePrevSeeds[pairIdx] == seed;

            // 用【此刻的真实尺寸】而不是 startSize：GetCurrentSize 含随机大小与 Size over Lifetime
            float curSize = p.GetCurrentSize(ps);
            if (curSize <= 0f) curSize = p.startSize;
            float halfSize = Mathf.Min(curSize * 0.5f, 1f);

            // ---- 身份证通道：上一帧位置直接存在粒子自己身上 ----
            // 新生判据用年龄（不可伪造）：首次被观测到的粒子 age <= dt，此时通道里装的是
            // 上一颗死粒子的残留值，绝不能采信。
            float particleAge = p.startLifetime - p.remainingLifetime;
            bool isNewborn = particleAge <= stepDt * PASSPORT_NEWBORN_AGE_SLACK;

            Vector3 passportRaw = p.axisOfRotation;
            // 有效性 = 标记位（确定性；出生必清零）
            float markerNow = p.angularVelocity;
            bool passportValid = passportOK && (markerNow == PASSPORT_MARKER
                                             || markerNow == PASSPORT_PENDING_A
                                             || markerNow == PASSPORT_PENDING_B);
            // 0=无 1=待从A浮出 2=待从B浮出
            int pendingDoor = (markerNow == PASSPORT_PENDING_A) ? 1 : ((markerNow == PASSPORT_PENDING_B) ? 2 : 0);
            Vector3 passportWorld = curPos;
            if (passportValid)
            {
                passportWorld = localMode ? sysT.TransformPoint(passportRaw) : passportRaw;
                float lim = worldVel.magnitude * stepDt * PASSPORT_SANITY_FACTOR + PASSPORT_SANITY_BASE;
                if ((curPos - passportWorld).sqrMagnitude > lim * lim)
                {
                    passportValid = false;
                    if (debugParticleTeleportLog) debugGateRejected++;
                }
            }
            if (debugParticleTeleportLog)
            {
                if (passportValid) debugPassportUsed++;
                else if (passportOK) debugPassportMissing++;

                // 插桩：新生粒子数（应≈发射率*dt）与被年龄判据拦下的残留值距离
                if (isNewborn)
                {
                    debugNewbornTotal++;
                    Vector3 stW = localMode ? sysT.TransformPoint(passportRaw) : passportRaw;
                    float sd = (curPos - stW).magnitude;
                    if (sd > 0.001f) { debugNewbornStale++; debugStaleDist += sd; }
                    else debugNewbornSentinel++;
                }
            }

            Vector3 segStart;
            if (passportValid)
            {
                // 精确：这就是该粒子上一帧真实所在（含上一帧被传送后的落点），不受下标洗牌影响
                segStart = passportWorld;
            }
            else if (paired)
            {
                segStart = particlePrevPositions[pairIdx];
            }
            else if (isNewborn && particleAge > 0f)
            {
                // 精确出生点：引擎在出生帧已把粒子推进了 age 秒，反推 age 秒即真实出生位置
                segStart = curPos - worldVel * particleAge;
            }
            else
            {
                float span = (particleAge >= 0f && particleAge <= 0.5f) ? Mathf.Max(particleAge, stepDt) : stepDt;
                segStart = curPos - worldVel * span;
            }

            if (debugParticleTeleportLog) particleDebugTestedCount++;

            float tA; Vector3 hitA; int kindA;
            bool crossA = ParticleSegmentCrossesPortal(segStart, curPos, worldVel, portalPlaneA, worldToLocalA, shapeA, halfSize, out tA, out hitA, out kindA);
            float tB; Vector3 hitB; int kindB;
            bool crossB = ParticleSegmentCrossesPortal(segStart, curPos, worldVel, portalPlaneB, worldToLocalB, shapeB, halfSize, out tB, out hitB, out kindB);

            // ===== 规则0：预测式捕获（对"引擎即将走的那一步"做扫掠）=====
            // 依据：实测粒子模拟在 LateUpdate 之后执行，我们写回后引擎必定再推进 v*stepDt。
            float predictLag = 0f;   // 负数 = 距离穿面还有多久
            bool predicted = false;
            // 实体墙场景（exitMinNormal>0）下禁用预测式：预测式必须把粒子放到出口面【之前】，
            // 而那个位置在墙里 —— 钳出来会把亚帧铺开压成一个点，条纹反而回来。
            // 有墙时改走回溯式 + 向外亚帧补偿：出射点自然铺开在 [墙面外, 墙面外+v*dt]，既不进墙也不成条纹。
            if (usePredictiveCapture && exitMinNormal <= 0f)
            {
                Vector3 segFuture = curPos + worldVel * stepDt;
                float tpA; Vector3 hpA; int kpA;
                float tpB; Vector3 hpB; int kpB;
                bool pA = ParticleSegmentCrossesPortal(curPos, segFuture, worldVel, portalPlaneA, worldToLocalA, shapeA, halfSize, out tpA, out hpA, out kpA);
                bool pB = ParticleSegmentCrossesPortal(curPos, segFuture, worldVel, portalPlaneB, worldToLocalB, shapeB, halfSize, out tpB, out hpB, out kpB);
                // 只认真正的符号翻转（kind==1），不认"终点已在门后"那种回溯语义，避免抢跑过头
                bool okA = pA && kpA == 1 && pendingDoor != 1;
                bool okB = pB && kpB == 1 && pendingDoor != 2;
                if (okA || okB)
                {
                    bool pickA = okA && (!okB || tpA <= tpB);
                    predicted = true;
                    if (pickA) { crossA = true; kindA = 1; tA = tpA; hitA = hpA; crossB = false; predictLag = -tpA * stepDt; }
                    else       { crossB = true; kindB = 1; tB = tpB; hitB = hpB; crossA = false; predictLag = -tpB * stepDt; }
                    if (debugParticleTeleportLog) debugPredictiveHits++;
                }
            }

            int ruleHit = 0;
            bool doA = false, doB = false;
            if (crossA || crossB)
            {
                bool solidA = crossA && kindA == 1;
                bool solidB = crossB && kindB == 1;
                if (solidA || solidB)
                {
                    doA = solidA && (!solidB || tA <= tB);
                    doB = solidB && !doA;
                }
                else
                {
                    doA = crossA && (!crossB || tA <= tB);
                    doB = crossB && !doA;
                }
                ruleHit = doA ? kindA : kindB;
            }

            // ★ 入射方向守卫：只有【朝门里走】的粒子才会被吸进去。
            //   开碰撞后，撞墙弹回的粒子速度已经反向；若被规则1/2/3 抓走，
            //   半转映射会把出口速度指回门里，粒子从错误的一侧射出（实测出射面距出现 -8.5m）。
            //   规则4 不受此限——它专门处理反弹，用的是反弹【前】的速度。
            if (doA && Vector3.Dot(worldVel, portalPlaneA.forward) > 0f)
            { doA = false; ruleHit = 0; if (debugParticleTeleportLog) debugRejectedOutbound++; }
            if (doB && Vector3.Dot(worldVel, portalPlaneB.forward) > 0f)
            { doB = false; ruleHit = 0; if (debugParticleTeleportLog) debugRejectedOutbound++; }

            Vector3 localCurA = worldToLocalA.MultiplyPoint(curPos);
            Vector3 localCurB = worldToLocalB.MultiplyPoint(curPos);
            float capturedDepth = 0f;

            // 待浮出守卫：欠哪扇门就对哪扇门失明，直到它探出该门的检测面
            if (pendingDoor == 1 && doA) { doA = false; ruleHit = 0; }
            if (pendingDoor == 2 && doB) { doB = false; ruleHit = 0; }

            // 规则3 首见后侧兜底
            if (!doA && !doB && !paired && !passportValid)
            {
                bool behindA = localCurA.z <= effPlaneOffset && ParticleRectCheck(localCurA, shapeA, halfSize);
                bool behindB = localCurB.z <= effPlaneOffset && ParticleRectCheck(localCurB, shapeB, halfSize);
                if (behindA && (!behindB || localCurA.z <= localCurB.z))
                {
                    doA = true;
                    capturedDepth = effPlaneOffset - localCurA.z;
                    float vn3 = Vector3.Dot(worldVel, portalPlaneA.forward);
                    if (vn3 < -0.01f || vn3 > 0.01f)
                    {
                        hitA = curPos - worldVel * ((localCurA.z - effPlaneOffset) / vn3);
                    }
                    else
                    {
                        hitA = curPos + portalPlaneA.forward * capturedDepth;
                    }
                    tA = 1f;
                    ruleHit = 3;
                }
                else if (behindB)
                {
                    doB = true;
                    capturedDepth = effPlaneOffset - localCurB.z;
                    float vn3 = Vector3.Dot(worldVel, portalPlaneB.forward);
                    if (vn3 < -0.01f || vn3 > 0.01f)
                    {
                        hitB = curPos - worldVel * ((localCurB.z - effPlaneOffset) / vn3);
                    }
                    else
                    {
                        hitB = curPos + portalPlaneB.forward * capturedDepth;
                    }
                    tB = 1f;
                    ruleHit = 3;
                }
            }

            if (pendingDoor == 1 && doA) { doA = false; ruleHit = 0; }
            if (pendingDoor == 2 && doB) { doB = false; ruleHit = 0; }

            if (doA && ruleHit == 3 && Vector3.Dot(worldVel, portalPlaneA.forward) > 0f)
            { doA = false; ruleHit = 0; if (debugParticleTeleportLog) debugRejectedOutbound++; }
            if (doB && ruleHit == 3 && Vector3.Dot(worldVel, portalPlaneB.forward) > 0f)
            { doB = false; ruleHit = 0; if (debugParticleTeleportLog) debugRejectedOutbound++; }

            if (doA && ruleHit == 2) capturedDepth = effPlaneOffset - localCurA.z;
            else if (doB && ruleHit == 2) capturedDepth = effPlaneOffset - localCurB.z;

            // 规则4 反弹捕获
            Vector3 mappingVel = worldVel;
            if (!doA && !doB && paired)
            {
                Vector3 anchorA = Vector3.zero, anchorB = Vector3.zero;
                Vector3 mapA = worldVel, mapB = worldVel;
                float strengthA = 0f, strengthB = 0f;
                bool bounceA = TryBounceCapturePortal(portalPlaneA, worldToLocalA, shapeA, halfSize,
                    particlePrevPositions[pairIdx], particlePrevVelocities[pairIdx],
                    curPos, worldVel, dt,
                    out anchorA, out mapA, out strengthA);
                bool bounceB = TryBounceCapturePortal(portalPlaneB, worldToLocalB, shapeB, halfSize,
                    particlePrevPositions[pairIdx], particlePrevVelocities[pairIdx],
                    curPos, worldVel, dt,
                    out anchorB, out mapB, out strengthB);
                if (bounceA || bounceB)
                {
                    bool pickA = bounceA && (!bounceB || strengthA >= strengthB);
                    mappingVel = pickA ? mapA : mapB;
                    if (pickA) { doA = true; hitA = anchorA; tA = 1f; }
                    else { doB = true; hitB = anchorB; tB = 1f; }
                    ruleHit = 4;
                    if (debugParticleTeleportLog) particleDebugBounceCount++;
                }
            }

            bool wasTeleported = false;

            // ===== 规则5：反向追溯捕获（撞击发生在我们两次采样【之间】）=====
            // 实测（速度1000 + 实体墙）：粒子在同一个引擎步里完成"出生→飞10.7m→撞墙→弹回22m"，
            // 我们第一次看到它时已经是弹回后的状态 —— 规则4 等的那个"法向速度翻转"永远等不到。
            // 解法：不等翻转，直接把【当前速度】反向延长打回门平面，交点就是撞击点。
            //   条件：① 在门正面且正在远离门；② 反推的撞击时刻在它出生之后、且就在最近一步之内；
            //         ③ 撞击点落在门框内。映射速度用【入射速度】= 当前速度对门法线做反射。
            if (!doA && !doB)
            {
                for (int door = 0; door < 2; door++)
                {
                    Transform pl = (door == 0) ? portalPlaneA : portalPlaneB;
                    if (pl == null) continue;
                    if (pendingDoor == door + 1) continue;   // 刚从这扇门出射的，别抓
                    Vector3 n5 = pl.forward;
                    float vn5 = Vector3.Dot(worldVel, n5);
                    if (vn5 <= 0.01f) continue;              // 必须是"正在远离门"
                    Vector3 planePt5 = pl.position + n5 * effPlaneOffset;
                    float z5 = Vector3.Dot(curPos - planePt5, n5);
                    if (z5 <= 0f) continue;                  // 必须已经在检测面正面
                    float tBack = z5 / vn5;                  // 多久之前离开这个面
                    if (tBack > stepDt * 1.5f) continue;     // 只认最近一步之内发生的撞击
                    if (tBack > particleAge + 0.0001f) continue; // 不可能早于它出生
                    Vector3 hit5 = curPos - worldVel * tBack;
                    Vector3 local5 = ((door == 0) ? worldToLocalA : worldToLocalB).MultiplyPoint(hit5);
                    if (!ParticleRectCheck(local5, (door == 0) ? shapeA : shapeB, halfSize)) continue;
                    // 入射速度 = 当前速度对门法线反射
                    mappingVel = worldVel - 2f * vn5 * n5;
                    if (door == 0) { doA = true; hitA = hit5; tA = 1f; }
                    else { doB = true; hitB = hit5; tB = 1f; }
                    ruleHit = 5;
                    if (debugParticleTeleportLog) debugRule5++;
                    break;
                }
            }

            // 穿面后已流逝的时间：锚点到本帧位置的距离 ÷ 速度（匀速下精确；对四条规则统一成立）
            float subFrameLag = 0f;
            if (predicted)
            {
                subFrameLag = predictLag;   // 负值：提前放置，让引擎那一步把它推到正确位置
                if (debugParticleTeleportLog && (doA || doB))
                {
                    float ovp = -subFrameLag * mappingVel.magnitude;
                    debugOvershootSum += ovp; debugOvershootN++;
                    if (ovp > debugOvershootMax) debugOvershootMax = ovp;
                }
            }
            else if (useSubFrameCompensation)
            {
                float spd = mappingVel.magnitude;
                if (spd > 0.001f)
                {
                    Vector3 anchorUsed = doA ? hitA : hitB;
                    subFrameLag = (curPos - anchorUsed).magnitude / spd;
                    float lagCap = stepDt;               // 物理上界：规则1 的 t∈[0,1]，一步之内
                    if (subFrameLag > lagCap) subFrameLag = lagCap;
                }
                if (debugParticleTeleportLog && (doA || doB))
                {
                    float ov = subFrameLag * mappingVel.magnitude;
                    debugOvershootSum += ov; debugOvershootN++;
                    if (ov > debugOvershootMax) debugOvershootMax = ov;
                }
            }

            if (doA)
            {
                TeleportParticleThroughPortal(ref p, hitA, mappingVel, portalPlaneA, worldToLocalA, localToWorldB, subFrameLag);
                wasTeleported = true;
                if (debugParticleTeleportLog)
                {
                    particleDebugTeleportCount++;
                    float exitPlaneDistA = Vector3.Dot(p.position - portalPlaneB.position, portalPlaneB.forward);
                    RecordTeleportStat(true, exitPlaneDistA, -subFrameLag * mappingVel.magnitude, seed, curPos, worldVel, ruleHit, predicted);
                    if (debugVerboseEvents)
                    {
                        particleDebugEventLogged++;
                        Debug.Log("[粒子传送][事件] 规则" + ruleHit + " A->B 种子=" + seed
                            + " 出射面距=" + exitPlaneDistA.ToString("F2")
                            + " 预测=" + predicted + " 亚帧=" + subFrameLag.ToString("F4") + "s");
                    }
                }
            }
            else if (doB)
            {
                TeleportParticleThroughPortal(ref p, hitB, mappingVel, portalPlaneB, worldToLocalB, localToWorldA, subFrameLag);
                wasTeleported = true;
                if (debugParticleTeleportLog)
                {
                    particleDebugTeleportCount++;
                    float exitPlaneDistB = Vector3.Dot(p.position - portalPlaneA.position, portalPlaneA.forward);
                    RecordTeleportStat(false, exitPlaneDistB, -subFrameLag * mappingVel.magnitude, seed, curPos, worldVel, ruleHit, predicted);
                    if (debugVerboseEvents)
                    {
                        particleDebugEventLogged++;
                        Debug.Log("[粒子传送][事件] 规则" + ruleHit + " B->A 种子=" + seed
                            + " 出射面距=" + exitPlaneDistB.ToString("F2")
                            + " 预测=" + predicted + " 亚帧=" + subFrameLag.ToString("F4") + "s");
                    }
                }
            }

            if (pendingDoor == 1 && doA) { doA = false; }
            if (pendingDoor == 2 && doB) { doB = false; }

            Vector3 finalWorldPos = curPos;
            Vector3 finalWorldVel = worldVel;
            if (wasTeleported)
            {
                finalWorldPos = p.position;
                finalWorldVel = p.velocity;
                if (localMode)
                {
                    p.position = sysT.InverseTransformPoint(p.position);
                    p.velocity = sysT.InverseTransformVector(p.velocity);
                }
                particleTeleportBuffer[i] = p;
                changed = true;
            }

            // ---- 写入身份证通道：本帧最终位置（传送过就是出口落点），供下一帧当精确线段起点 ----
            if (passportOK)
            {
                Vector3 passportStore = wasTeleported ? lastExitFrontAnchor : finalWorldPos;
                p.axisOfRotation = localMode ? sysT.InverseTransformPoint(passportStore) : passportStore;

                float markerNext = PASSPORT_MARKER;
                if (wasTeleported)
                {
                    // 出口门 = 与捕获门相对的那扇：在 A 捕获 -> 从 B 出；在 B 捕获 -> 从 A 出
                    markerNext = doA ? PASSPORT_PENDING_B : PASSPORT_PENDING_A;
                }
                else if (pendingDoor != 0)
                {
                    // 只有真正探出该门的检测面，才解除待浮出状态
                    // 必须飞出规则5 的追溯窗口（速度×stepDt×2）才解除，否则自家出射粒子会被规则5 当成"撞墙弹回"抓回去
                    float zSelf = (pendingDoor == 1) ? localCurA.z : localCurB.z;
                    float clearZ = effPlaneOffset + worldVel.magnitude * stepDt * 2f;
                    markerNext = (zSelf >= clearZ) ? PASSPORT_MARKER
                               : ((pendingDoor == 1) ? PASSPORT_PENDING_A : PASSPORT_PENDING_B);
                }
                p.angularVelocity = markerNext;   // 盖章：身份证已写（并携带待浮出状态）
                particleTeleportBuffer[i] = p;
                changed = true;
            }

            // 漏检嫌疑日志
            if (debugParticleTeleportLog && !wasTeleported)
            {
                float zA = localCurA.z;
                float zB = localCurB.z;
                bool inRectA = ParticleRectCheck(localCurA, shapeA, halfSize);
                bool inRectB = ParticleRectCheck(localCurB, shapeB, halfSize);
                if ((zA < -0.01f && inRectA) || (zB < -0.01f && inRectB)) particleDebugStuckCount++;

                // 真漏检测（无深度上限）：粒子已在门后任意深处、横向仍在门框内 = 它穿过了门却没被传送
                bool deepLeakA = localCurA.z < -0.5f && ParticleRectNoDepth(localCurA, shapeA, halfSize);
                bool deepLeakB = localCurB.z < -0.5f && ParticleRectNoDepth(localCurB, shapeB, halfSize);
                if (deepLeakA) { debugTrueLeakA++; debugTrueLeakDepthA += -localCurA.z; }
                if (deepLeakB) { debugTrueLeakB++; debugTrueLeakDepthB += -localCurB.z; }

                bool suspectA = zA <= effPlaneOffset && zA > effPlaneOffset - 0.5f;
                bool suspectB = zB <= effPlaneOffset && zB > effPlaneOffset - 0.5f;
                if (debugVerboseEvents && (suspectA || suspectB) && particleDebugStuckSamples < 6)
                {
                    particleDebugStuckSamples++;
                    Debug.Log("[粒子传送][漏检嫌疑] 系统=" + ps.name + " zA=" + zA.ToString("F3") + " zB=" + zB.ToString("F3") + " 框内A=" + inRectA + " 框内B=" + inRectB + " 配对=" + paired + " 已传过=" + (paired && particlePrevTeleported[pairIdx]) + " 速度=" + worldVel.magnitude.ToString("F1") + " 种子=" + seed.ToString());
                }
            }

            // 记录本帧
            if (pairIdx >= 0)
            {
                particlePrevPositions[pairIdx] = finalWorldPos;
                particlePrevSeeds[pairIdx] = seed;
                particlePrevVelocities[pairIdx] = finalWorldVel;
                particlePrevValid[pairIdx] = true;
                particlePrevTeleported[pairIdx] = wasTeleported;
            }
        }

        if (changed) ps.SetParticles(particleTeleportBuffer, aliveCount);
    }

    // 规则4v2 反弹点重建（十七轮）：配对粒子的门法向速度一帧内反转（朝门→离门）时，
    // 假设本帧内先以 prevVel 直线飞到墙面反弹、再以 curVel 飞完剩余时间，沿门法线反解
    // 反弹时刻 tb 与反弹点 p = prevPos + prevVel*tb：
    //   p1 = p0 + v0*tb + v1*(dt-tb)  →  tb = (z1 - z0 - vn1*dt) / (vn0 - vn1)
    // （vn0<0 朝门、vn1>0 离门 → 分母恒负、数值稳定）。数值模拟验证（5万随机样本，
    // 速度5~3000、恢复系数0.1~0.9）：重构误差≈机器精度1e-14m，零越界零假阳性；
    // 旧版"帧末位置窗口"对高速反弹漏捕90.2%。
    // 判定链：速度反转 → tb∈(0,dt) → 反弹点在门框内且 z∈容差带 → 命中。
    // 命中后：锚点=反弹点沿法线投影到检测面（落点恒在出口检测面前侧防乒乓），
    // 映射速度=反弹【前】速度（当作没反弹直接穿过去，出口粒子才会离开门飞）。
    // 帧内顺序：粒子模拟在所有脚本 Update 之后，本帧读到的反转是最近一次模拟产生的
    // 反弹，粒子在墙边最多可见一帧即被收走。
    private bool TryBounceCapturePortal(Transform portalPlane, Matrix4x4 worldToLocal, int shapeType, float inflateHalf, Vector3 prevPos, Vector3 prevVel, Vector3 curPos, Vector3 curVel, float dt, out Vector3 anchorWorld, out Vector3 mappingVel, out float flipStrength)
    {
        anchorWorld = curPos;
        mappingVel = curVel;
        flipStrength = 0f;
        if (portalPlane == null) return false;

        Vector3 n = portalPlane.forward;
        float vn0 = Vector3.Dot(prevVel, n);
        float vn1 = Vector3.Dot(curVel, n);
        if (!(vn0 < -0.1f && vn1 > 0.1f)) { if (debugParticleTeleportLog) debugBounceNoFlip++; return false; }

        float z0 = Vector3.Dot(prevPos - portalPlane.position, n);
        float z1 = Vector3.Dot(curPos - portalPlane.position, n);
        float tb = (z1 - z0 - vn1 * dt) / (vn0 - vn1);
        if (tb <= 0f || tb >= dt) { if (debugParticleTeleportLog) debugBounceBadT++; return false; }

        Vector3 bouncePoint = prevPos + prevVel * tb;
        Vector3 local = worldToLocal.MultiplyPoint(bouncePoint);
        if (local.z < PARTICLE_BOUNCE_Z_MIN || local.z > PARTICLE_BOUNCE_Z_MAX) { if (debugParticleTeleportLog) debugBounceBadZ++; return false; }
        if (!ParticleRectCheck(local, shapeType, inflateHalf))
        {
            // 反弹点重建成功、但落在门框之外 —— 粒子是撞在门框边缘/墙面上被弹飞的，属于合法漏网，不是漏检
            if (debugParticleTeleportLog) debugBounceOutOfFrame++;
            return false;
        }

        anchorWorld = bouncePoint + n * (effPlaneOffset - local.z);
        mappingVel = prevVel;
        flipStrength = vn1 - vn0;
        return true;
    }

    // 读缓冲扩容（十六轮：配对历史已改为固定槽位池，不再跟随扩容）。
    // 扩容后补读；超出 STRIDE 的粒子走"未配对"路径（反推线段），零漏检不依赖配对。
    private void GrowParticleArrays(int newSize)
    {
        int size = Mathf.Max(32, newSize);
        particleTeleportBuffer = new ParticleSystem.Particle[size];
    }

    // 自动收集：从两类根收集粒子系统，保留原点离任一门 particleDiscoveryRadius 以内的：
    //   1) transform.root 子树（零配置来源）：预制件落地后，自带特效以及与门户挂在同一根
    //      节点下的所有粒子系统自动纳入。Udon 沙箱禁止全场景枚举API（FindObjectsOfType、
    //      Scene.GetRootGameObjects 均不在白名单——后者在 VRChat 官方反馈板有创作者请求，
    //      至今未开放），沿层级树 GetComponentsInChildren 是白名单内的最优路径。
    //   2) particleDiscoveryRoots（用户指定的收集根，可选）：特效挂在别的根节点下时手动拖入。
    // Start 与每 particleDiscoveryRefreshInterval 秒各跑一次（门可被传送枪移动，需要定期重扫）。
    private void DiscoverParticleSystems()
    {
        if (portalPlaneA == null || portalPlaneB == null) return;

        float radiusSqr = particleDiscoveryRadius * particleDiscoveryRadius;

        // transform.root 子树（零配置来源）
        Transform selfRoot = transform.root;
        ParticleSystem[] rootSystems = null;
        if (selfRoot != null)
        {
            rootSystems = selfRoot.GetComponentsInChildren<ParticleSystem>(true);
        }

        // 两遍式（Udon无动态数组）：第一遍数总数，第二遍填充
        int total = rootSystems != null ? rootSystems.Length : 0;
        int manualRoots = particleDiscoveryRoots != null ? particleDiscoveryRoots.Length : 0;
        for (int r = 0; r < manualRoots; r++)
        {
            GameObject root = particleDiscoveryRoots[r];
            if (root == null) continue;
            ParticleSystem[] under = root.GetComponentsInChildren<ParticleSystem>(true);
            if (under == null) continue;
            total += under.Length;
        }
        if (total == 0)
        {
            discoveredParticleSystems = null;
            return;
        }

        ParticleSystem[] collected = new ParticleSystem[total];
        int idx = CollectParticleSystemsNearPortals(rootSystems, collected, 0, radiusSqr);
        for (int r = 0; r < manualRoots; r++)
        {
            GameObject root = particleDiscoveryRoots[r];
            if (root == null) continue;
            ParticleSystem[] under = root.GetComponentsInChildren<ParticleSystem>(true);
            if (under == null) continue;
            idx = CollectParticleSystemsNearPortals(under, collected, idx, radiusSqr);
        }
        // 尾部可能有null（距离过滤掉的），遍历端已有null检查；
        // 与手动白名单重复的系统被二次处理时已是传送后状态，无二次传送。
        discoveredParticleSystems = collected;
    }

    // 把系统中离任一门足够近的粒子系统填入 dest，返回新的写入下标。
    private int CollectParticleSystemsNearPortals(ParticleSystem[] systems, ParticleSystem[] dest, int idx, float radiusSqr)
    {
        if (systems == null) return idx;
        for (int i = 0; i < systems.Length; i++)
        {
            ParticleSystem ps = systems[i];
            if (ps == null) continue;
            Vector3 sysPos = ps.transform.position;
            if ((sysPos - portalPlaneA.position).sqrMagnitude <= radiusSqr ||
                (sysPos - portalPlaneB.position).sqrMagnitude <= radiusSqr)
            {
                if (idx < dest.Length)
                {
                    dest[idx] = ps;
                    idx++;
                }
            }
        }
        return idx;
    }

    // ============================================================
    // 放置时发现：传送枪放门成功后调用（Start时也按初始门位置各扫一次）。
    // OverlapSphereNonAlloc 枚举半径内碰撞体 → 对每个碰撞体向上爬层级(找祖先的粒子系统)、
    // 向下 GetComponentsInChildren(找子物体里的粒子系统) → 去重注册。
    // 为什么不用触发器：粒子不触发 OnTriggerEnter（已查证/实测），且这里要找的是
    // "哪些系统存在"而不是"哪颗粒子到达"，物理查询+层级遍历才是对的工具。
    // 诚实边界：没有碰撞体的独立物体无法被枚举到，那部分靠 root 扫描/收集根/白名单。
    // ============================================================

    public void DiscoverParticleSystemsAround(Vector3 center)
    {
        if (!enableParticleTeleport) { Debug.Log("[粒子传送][放置发现] 跳过：粒子传送未启用"); return; }
        if (!autoDiscoverParticleSystems) { Debug.Log("[粒子传送][放置发现] 跳过：自动收集开关关闭"); return; }
        if (portalPlaneA == null || portalPlaneB == null) { Debug.Log("[粒子传送][放置发现] 跳过：门Transform未就绪"); return; }

        if (discoveryOverlapBuffer == null) discoveryOverlapBuffer = new Collider[512];
        if (placedDiscoverySystems == null) placedDiscoverySystems = new ParticleSystem[64];

        int hitCount = Physics.OverlapSphereNonAlloc(center, particleDiscoveryRadius, discoveryOverlapBuffer, ~0, QueryTriggerInteraction.Collide);
        // 截断告警：半径内碰撞体顶满缓冲时，超出的碰撞体携带的粒子系统扫不到（NonAlloc语义无法枚举完整集合）
        if (hitCount >= discoveryOverlapBuffer.Length)
        {
            Debug.LogWarning("[粒子传送][放置发现] 半径" + particleDiscoveryRadius + "m内碰撞体超过" + discoveryOverlapBuffer.Length + "个被截断，可能有粒子系统漏注册");
        }

        int registeredThisCall = 0;
        for (int i = 0; i < hitCount; i++)
        {
            Collider col = discoveryOverlapBuffer[i];
            if (col == null) continue;

            // 十一轮结构盲区修复：先向上爬到最高祖先（最多8层防长链），再从祖先整体向下扫。
            // 旧版只扫"碰撞体自身子树+祖先链上的单点"——粒子系统挂在碰撞体的【兄弟节点】
            // （常见预制件结构：根/兄弟挂ParticleSystem，碰撞挂在另一个子物体上）时完全扫不到，
            // 这正是"放置时自动检测没工作"的头号嫌疑。从最高祖先向下扫 = 祖先+兄弟+全部后代一次覆盖。
            Transform top = col.transform;
            for (int up = 0; up < 8 && top.parent != null; up++) top = top.parent;

            ParticleSystem[] under = top.gameObject.GetComponentsInChildren<ParticleSystem>(true);
            if (under == null) continue;
            for (int d = 0; d < under.Length; d++)
            {
                if (under[d] != null && RegisterDiscoveredParticleSystem(under[d])) registeredThisCall++;
            }
        }
        Debug.Log("[粒子传送][放置发现] 扫描完成：半径" + particleDiscoveryRadius + "m内命中" + hitCount + "个碰撞体，新注册" + registeredThisCall + "个粒子系统（当前注册总数" + placedDiscoveryCount + "）");
    }

    // 粒子系统是否在排除根子树下（传送枪特效等不参与传送）。
    // 配对槽位分配（十六轮）：先查已有槽位，再占空槽。池满返回 -1（该系统按未配对处理）。
    // 槽位一旦分配保持稳定，除非场景重载；系统从注册列表消失后槽位自然废弃（重新扫描时被复用）。
    private int GetOrAssignPairingSlot(ParticleSystem ps)
    {
        for (int s = 0; s < PARTICLE_PAIRING_SLOTS; s++)
        {
            if (particlePairingSlotSystems[s] == ps) return s;
        }
        for (int s = 0; s < PARTICLE_PAIRING_SLOTS; s++)
        {
            if (particlePairingSlotSystems[s] == null)
            {
                particlePairingSlotSystems[s] = ps;
                return s;
            }
        }
        return -1;
    }

    // Custom 空间警告（每系统一次，最多8个，防止刷屏）
    private void WarnCustomSpaceOnce(ParticleSystem ps)
    {
        for (int i = 0; i < customSpaceWarnedCount; i++)
        {
            if (customSpaceWarnedSystems[i] == ps) return;
        }
        if (customSpaceWarnedCount < customSpaceWarnedSystems.Length)
        {
            customSpaceWarnedSystems[customSpaceWarnedCount] = ps;
            customSpaceWarnedCount++;
        }
        Debug.LogWarning("[粒子传送] 系统 \"" + ps.name + "\" Simulation Space=Custom：按Local处理（假设customSimulationSpace=系统自身Transform）。若传送位置不对，请改用World或Local空间。");
    }

    // 粒子系统是否在黑名单/自动排除内。
    // 十六轮语义：枪载【World空间】系统（枪口火花等）维持自动排除防鬼畜；
    // 枪载【Local空间】系统（激光等，用户需求核心）放行参与传送；手动排除根对一切生效。
    private bool IsParticleSystemExcluded(ParticleSystem ps, bool localMode)
    {
        if (ps == null) return true;
        Transform t = ps.transform;
        if (portalGun != null && !localMode && IsTransformUnder(t, portalGun.transform)) return true;
        if (particleTeleportExclusionRoots != null)
        {
            for (int i = 0; i < particleTeleportExclusionRoots.Length; i++)
            {
                if (particleTeleportExclusionRoots[i] != null && IsTransformUnder(t, particleTeleportExclusionRoots[i])) return true;
            }
        }
        return false;
    }

    // t 是否在 root 子树下（等价 Transform.IsChildOf；手搓父链遍历，零API白名单风险）
    private bool IsTransformUnder(Transform t, Transform root)
    {
        if (t == null || root == null) return false;
        while (t != null)
        {
            if (t == root) return true;
            t = t.parent;
        }
        return false;
    }

    // 去重注册：已存在于 放置注册表/手动白名单/root扫描列表 的系统不重复登记。
    // 返回 true = 本次确实新注册（供放置发现统计）。
    private bool RegisterDiscoveredParticleSystem(ParticleSystem ps)
    {
        if (ps == null) return false;
        if (placedDiscoverySystems == null) return false;
        // 注册时同样按空间分流排除（Local枪载系统放行；World枪载系统不注册）。
        // 枚举数值：Local=0 World=1 Custom=2（Unity 参考源码 ParticleSystemEnums.cs）。
        int regSimSpace = (int)ps.main.simulationSpace;
        bool localMode = (regSimSpace == 0 || regSimSpace == 2);
        if (IsParticleSystemExcluded(ps, localMode)) return false;
        // 注册表满了自动翻倍扩容（旧行为是静默丢弃——密集世界里第65个之后的系统会无声漏注册）
        if (placedDiscoveryCount >= placedDiscoverySystems.Length)
        {
            ParticleSystem[] bigger = new ParticleSystem[placedDiscoverySystems.Length * 2];
            for (int c = 0; c < placedDiscoveryCount; c++) bigger[c] = placedDiscoverySystems[c];
            placedDiscoverySystems = bigger;
        }

        for (int i = 0; i < placedDiscoveryCount; i++)
        {
            if (placedDiscoverySystems[i] == ps) return false;
        }
        if (discoveredParticleSystems != null)
        {
            for (int i = 0; i < discoveredParticleSystems.Length; i++)
            {
                if (discoveredParticleSystems[i] == ps) return false;
            }
        }

        placedDiscoverySystems[placedDiscoveryCount] = ps;
        placedDiscoveryCount++;
        Debug.Log("[粒子传送] 放置时发现自动注册: " + ps.name);
        return true;
    }

    // 粒子线段与门检测面求交（六轮重写：符号距离插值 + 后侧窗符号修正）。
    // 符号距离 z = dot(点 - 检测面点, 门法线)：>0 = 前侧（房间侧），<=0 = 后侧。
    // 规则1 本帧穿越：线段两端跨检测面符号翻转 → 插值求穿越点，双向都收：
    //   - 前→后：正常进门；后→前：墙后发射器朝门外喷（与一轮"双向穿越都收"决议一致）；
    //   - 用 z 插值而不是 denom 除法：退化线段（粒子静止、门被传送枪移动扫过粒子）照样能抓到。
    //   - 穿越点在门框内算穿越；穿越点在框外但终点已飘进门框（斜粒子）也算（三轮补判，保留）。
    // 规则2 门洞捕获：已在检测面后侧且【门框内】→ 补抓（深度不限，retroWindow>0 时设上限）。
    //   六轮关键修正：旧版 withinWindow 的符号写反了——dot(segEnd-planePoint, forward)>=0 抓的其实是
    //   检测面【前侧】正在靠近的粒子（门前窗值米内会被提前吸走），而真正过平面后的漏检粒子
    //   forwardPastPlane<0 永远进不了窗——这就是"窗=2 仍残留 1~2 颗隧穿"的病根（窗从没兜住入口侧漏检）。
    //   现改为真正的"后侧深度<=窗值"。窗=0 时本分支自然关闭，零漏检由规则1+调用方规则3保证。
    // hitKind: 1=交点在门框内的实穿越（物理事件，选择时最先发生者优先）；
    //          2=兜底类命中（框外穿越+终点飘入框内 / 后侧追补窗，属状态补救，优先级低于实穿越）。
    private bool ParticleSegmentCrossesPortal(Vector3 segStart, Vector3 segEnd, Vector3 curVel, Transform portalPlane, Matrix4x4 worldToLocal, int shapeType, float inflateHalf, out float t, out Vector3 hitPoint, out int hitKind)
    {
        t = 0f; hitPoint = Vector3.zero; hitKind = 0;
        if (portalPlane == null) return false;

        Vector3 planePoint = portalPlane.position + portalPlane.forward * effPlaneOffset;
        Vector3 normal = portalPlane.forward;
        float zStart = Vector3.Dot(segStart - planePoint, normal);
        float zEnd = Vector3.Dot(segEnd - planePoint, normal);

        bool flipped = (zStart > 0f && zEnd <= 0f) || (zStart <= 0f && zEnd > 0f);
        if (flipped)
        {
            t = zStart / (zStart - zEnd);
            hitPoint = segStart + (segEnd - segStart) * t;
            Vector3 localHit = worldToLocal.MultiplyPoint(hitPoint);
            if (ParticleRectCheck(localHit, shapeType, inflateHalf))
            {
                hitKind = 1;
                return true;
            }
            Vector3 localEnd = worldToLocal.MultiplyPoint(segEnd);
            if (ParticleRectCheck(localEnd, shapeType, inflateHalf))
            {
                hitKind = 2;
                return true;
            }
            return false;
        }

        if (zEnd <= 0f && (particleTeleportRetroWindow <= 0f || zEnd >= -particleTeleportRetroWindow))
        {
            t = 1f;
            float vnCur = Vector3.Dot(curVel, normal);
            if (vnCur < -0.01f || vnCur > 0.01f)
            {
                hitPoint = segEnd - curVel * (zEnd / vnCur);
            }
            else
            {
                hitPoint = segEnd - normal * zEnd;
            }
            Vector3 localEnd = worldToLocal.MultiplyPoint(segEnd);
            if (ParticleRectCheck(localEnd, shapeType, inflateHalf))
            {
                hitKind = 2;
                return true;
            }
        }
        return false;
    }

    // 把单颗粒子映射到另一侧：穿越点 from→to+经典半转，速度同映射。
    // 二十四轮出射连续性重构（用户实测"水中折射/断层/散射"的根治）：
    // 1) 锚点移到【真实门平面】(z=0)：调用方传入的 hitPoint 在检测面(z=+offset)上，
    //    斜射时其横向位置比真实过门点偏 offset×tanθ（角度越大偏越多）——统一回移 offset；
    // 2) 前推改为沿【出口光束方向】0.2m：旧版沿出口【法线】推 landingPush，斜射时把
    //    整条出口光束横移 landingPush×cosθ=横向断层；沿光束推则出口光束与入口光束的
    //    映射像严格是同一条直线（零横向断层）。切线飞行时兜底沿出口侧法线补推到0.05m；
    // 3) 移除 remainingTime 帧内积分：旧版把出射粒子沿光束推出最多 速度×dt 米（1000m/s
    //    低帧率下可达50米），门面上留下一段无粒子缝隙=断层，速度越快缝隙越大
    //    （用户实测：速度10无感、100略明显、1000断层）。代价：粒子在传送帧损失
    //    (1-t)×dt 的运动（次帧配对从出口继续，视觉不可见）。
    // 防回穿乒乓：出口恒在出口侧≥0.05m（frontZ 兜底），规则1/2/3/4 不会回抓。
    private void TeleportParticleThroughPortal(ref ParticleSystem.Particle p, Vector3 hitPoint, Vector3 vel, Transform fromPlane, Matrix4x4 worldToLocalFrom, Matrix4x4 localToWorldTo, float subFrameLag)
    {
        Vector3 realHit = hitPoint - fromPlane.forward * effPlaneOffset;
        Vector3 localHit = worldToLocalFrom.MultiplyPoint(realHit);
        Vector3 localVel = worldToLocalFrom.MultiplyVector(vel);
        if (useClassicHalfTurn)
        {
            localHit = LocalHalfTurn(localHit);
            localVel = LocalHalfTurn(localVel);
        }
        Vector3 exitPos = localToWorldTo.MultiplyPoint(localHit);
        Vector3 exitVel = localToWorldTo.MultiplyVector(localVel);
        float speed = exitVel.magnitude;
        Vector3 exitNormal = localToWorldTo.MultiplyVector(Vector3.forward);

        if (speed > 0.001f)
        {
            float cosComp = localVel.z / speed;
            float targetZ = effPlaneOffset + 0.01f;
            float side = cosComp >= 0f ? 1f : -1f;
            float absCos = cosComp < 0f ? -cosComp : cosComp;
            float push = 0f;
            if (absCos > 0.01f)
            {
                push = targetZ / absCos;
                if (push > 2f) push = 2f;
            }
            // 亚帧补偿：沿出口光束补上"穿面到现在"已经走过的距离。
            // 取两者较大值 —— 既保证越过出口检测面（防回抓），又让同帧多颗粒子按各自
            // 穿面时刻铺开成连续直线，而不是全挤在同一个锚点上形成条纹。
            float lead = speed * subFrameLag;
            if (subFrameLag < 0f)
            {
                // 预测式：故意放到出口面之前（负 push），引擎下一步会把它精确送到门面外侧。
                // 防回抓不靠位置，靠写回身份证时用正面虚拟锚点（见 lastExitFrontAnchor）。
                push = lead;
            }
            else if (lead > push) push = lead;
            p.position = exitPos + (exitVel / speed) * push;
            if (subFrameLag >= 0f)
            {
                // 回溯式捕获：必须保证越过出口检测面，否则下一帧线段会重穿被规则1回抓（乒乓）
                float zNow = Vector3.Dot(p.position - exitPos, exitNormal);
                if (side * zNow < targetZ)
                {
                    p.position += exitNormal * (side * (targetZ - side * zNow));
                }
            }
            else if (exitMinNormal > 0f)
            {
                // 实体墙场景：不允许把粒子放到墙里/墙后。钳到墙面 + 粒子碰撞半径 + 余量之外。
                // 注意参考面：exitPos 已被回移到【门原点平面 z=0】（见 realHit 的 -offset），
                // 所以法向分量本身就是"相对门原点的距离"，不能再加 offset（加了会把判据抬高一个 offset，钳制永不触发）。
                float zWall = Vector3.Dot(p.position - exitPos, exitNormal);
                if (side * zWall < exitMinNormal)
                {
                    p.position += exitNormal * (side * (exitMinNormal - side * zWall));
                }
            }
            // 预测式捕获（subFrameLag<0）故意落在门面之前，不做此钳制；
            // 防回抓由 lastExitFrontAnchor（写回身份证时用的正面虚拟锚点）负责。
        }
        else
        {
            p.position = exitPos + exitNormal * (effPlaneOffset + 0.01f);
        }
        p.velocity = exitVel;
        // 正面虚拟锚点：永远落在出口检测面的正面，供身份证写回使用。
        // 这样即便本帧把粒子放到了门面之前，下一帧的线段起点也不会跨越出口检测面 => 不会被规则1回抓。
        float sideN = (speed > 0.001f && Vector3.Dot(exitVel, exitNormal) < 0f) ? -1f : 1f;
        lastExitFrontAnchor = exitPos + exitNormal * (sideN * (effPlaneOffset + 0.01f));
    }

    private bool MeasureWallForPortal(Transform portal, Collider own, out float frontOffset, out float backOffset)
    {
        frontOffset = 0f; backOffset = 0f;
        if (portal == null) return false;
        Collider target = own;
        if (target == null) target = portal.GetComponent<Collider>();
        if (target == null) return false;

        Vector3 n = portal.forward;
        Vector3 P = portal.position;
        float depth = (wallProbeMaxDepth > 0.05f) ? wallProbeMaxDepth : 0.05f;

        RaycastHit hitF;
        bool okF = false;
        if (Physics.Raycast(P + n * depth, -n, out hitF, depth * 2f, wallProbeMask, QueryTriggerInteraction.Collide))
        {
            if (hitF.collider == target)
            {
                // 命中点沿 +n 相对门原点的偏移
                frontOffset = Vector3.Dot(hitF.point - P, n);
                okF = true;
            }
        }

        RaycastHit hitB;
        bool okB = false;
        if (Physics.Raycast(P - n * depth, n, out hitB, depth * 2f, wallProbeMask, QueryTriggerInteraction.Collide))
        {
            if (hitB.collider == target)
            {
                backOffset = Vector3.Dot(hitB.point - P, n);
                okB = true;
            }
        }

        // 两面都要打到才算数（只打到一面说明射线被别的东西挡了，或门没贴在面上）
        return okF && okB && (frontOffset > backOffset);
    }
    private float GetSystemWorstStartSize(ParticleSystem ps)
    {
        var c = ps.main.startSize;
        float v = c.constant;
        float vmax = c.constantMax;
        if (vmax > v) v = vmax;
        float mult = c.curveMultiplier;
        if (v <= 0.0001f && mult > 0f) v = mult;
        else if (mult > 1f) v = v * mult;
        return v;
    }
    private void UpdateWallProbe(float dt)
    {
        if (!autoMeasureWall)
        {
            effPlaneOffset = particleTeleportPlaneOffset;
            return;
        }
        wallTimer -= dt;
        bool first = !wallLoggedOnce;
        if (wallTimer <= 0f || first)
        {
            wallTimer = (wallRemeasureInterval > 0.05f) ? wallRemeasureInterval : 999999f;
            wallOkA = MeasureWallForPortal(portalPlaneA, portalWallColliderA, out wallFrontA, out wallBackA);
            wallOkB = MeasureWallForPortal(portalPlaneB, portalWallColliderB, out wallFrontB, out wallBackB);
        }

        float need = particleTeleportPlaneOffset;
        float radius = GetMaxParticleCollisionRadius();
        if (wallOkA)
        {
            float nA = wallFrontA + radius + wallExitMargin;
            if (nA > need) need = nA;
        }
        if (wallOkB)
        {
            float nB = wallFrontB + radius + wallExitMargin;
            if (nB > need) need = nB;
        }
        effPlaneOffset = need;

        // 只有"确实量到墙"且"粒子确实开了碰撞"时才设硬下界
        bool anyCollision = false;
        if (discoveredParticleSystems != null)
        {
            for (int i = 0; i < discoveredParticleSystems.Length; i++)
            {
                var psi = discoveredParticleSystems[i];
                if (psi != null && psi.collision.enabled) { anyCollision = true; break; }
            }
        }
        if (!anyCollision && placedDiscoverySystems != null)
        {
            for (int i = 0; i < placedDiscoveryCount; i++)
            {
                var psi = placedDiscoverySystems[i];
                if (psi != null && psi.collision.enabled) { anyCollision = true; break; }
            }
        }
        if (anyCollision && (wallOkA || wallOkB))
        {
            float wf = wallOkA ? wallFrontA : wallFrontB;
            if (wallOkA && wallOkB && wallFrontB > wf) wf = wallFrontB;
            exitMinNormal = wf + radius + wallExitMargin;
        }
        else
        {
            exitMinNormal = -1f;
        }

        if (debugParticleTeleportLog && !wallLoggedOnce)
        {
            wallLoggedOnce = true;
            Debug.Log("[粒子传送][量墙] A " + (wallOkA ? ("正面=" + wallFrontA.ToString("F3") + "m 背面=" + wallBackA.ToString("F3") + "m 厚=" + (wallFrontA - wallBackA).ToString("F3") + "m") : "未命中(不外扩)")
                + " ｜B " + (wallOkB ? ("正面=" + wallFrontB.ToString("F3") + "m 背面=" + wallBackB.ToString("F3") + "m 厚=" + (wallFrontB - wallBackB).ToString("F3") + "m") : "未命中(不外扩)")
                + " ｜粒子碰撞半径=" + radius.ToString("F3") + "m"
                + " ｜检测面 用户" + particleTeleportPlaneOffset.ToString("F3") + " → 实际" + effPlaneOffset.ToString("F3") + "m");
        }
    }
    private void WarnPassportDisabledOnce(ParticleSystem ps)
    {
        for (int i = 0; i < passportDisabledCount; i++)
            if (passportDisabledSystems[i] == ps) return;
        if (passportDisabledCount < passportDisabledSystems.Length)
        {
            passportDisabledSystems[passportDisabledCount++] = ps;
            Debug.LogWarning("[粒子传送] 系统 " + ps.name + " 启用了旋转模块（rotationOverLifetime / rotationBySpeed），" +
                             "axisOfRotation 被引擎占用，已自动回退到下标配对（该系统会保留约 2%/帧 的配对失败率）。");
        }
    }
    private void WarnSizeOverLifetimeOnce(ParticleSystem ps)
    {
        for (int i = 0; i < sizeWarnedCount; i++) if (sizeWarnedSystems[i] == ps) return;
        if (sizeWarnedCount < sizeWarnedSystems.Length)
        {
            sizeWarnedSystems[sizeWarnedCount++] = ps;
            Debug.LogWarning("[粒子传送] 系统 " + ps.name + " 启用了 Size over Lifetime：碰撞半径会在存活期内变化，" +
                             "自动外扩只按起始尺寸的随机上限计算。若出现粒子撞墙才被传送，请用 particleRadiusOverride 手填半径上限。");
        }
    }
    private void RecordTeleportStat(bool toB, float exitPlaneDist, float overshoot, uint seed,
                                    Vector3 pos, Vector3 vel, int rule, bool predicted)
    {
        if (toB) debugAtoB++; else debugBtoA++;
        // 主流向 = 本窗口里较多的那个方向；反向计数在汇总时按较小者算
        if (!debugExitAny) { debugExitMin = exitPlaneDist; debugExitMax = exitPlaneDist; debugExitAny = true; }
        else
        {
            if (exitPlaneDist < debugExitMin) debugExitMin = exitPlaneDist;
            if (exitPlaneDist > debugExitMax) debugExitMax = exitPlaneDist;
        }
        int bin;
        if (exitPlaneDist < -8f) bin = 0;
        else if (exitPlaneDist < -4f) bin = 1;
        else if (exitPlaneDist < -1f) bin = 2;
        else if (exitPlaneDist < 0f) bin = 3;
        else if (exitPlaneDist < 1f) bin = 4;
        else if (exitPlaneDist < 4f) bin = 5;
        else if (exitPlaneDist < 8f) bin = 6;
        else bin = 7;
        debugExitHist[bin]++;

        if (overshoot > debugWorstOvershoot)
        {
            debugWorstOvershoot = overshoot;
            debugWorstSeed = seed;
            debugWorstPos = pos;
            debugWorstVel = vel;
            debugWorstExitDist = exitPlaneDist;
            debugWorstRule = rule;
            debugWorstPredicted = predicted;
        }
        debugReverseTeleports = (debugAtoB < debugBtoA) ? debugAtoB : debugBtoA;
    }

    // 当前所有已注册粒子系统里最大的碰撞半径（radiusScale 实测可读；尺寸取随机上限）
    private float GetMaxParticleCollisionRadius()
    {
        if (particleRadiusOverride > 0f) return particleRadiusOverride;
        float r = 0f;
        if (discoveredParticleSystems != null)
        {
            for (int i = 0; i < discoveredParticleSystems.Length; i++)
            {
                var ps = discoveredParticleSystems[i];
                if (ps == null || !ps.collision.enabled) continue;
                if (ps.sizeOverLifetime.enabled) WarnSizeOverLifetimeOnce(ps);
                float rr = GetSystemWorstStartSize(ps) * ps.collision.radiusScale * 0.5f;
                if (rr > r) r = rr;
            }
        }
        if (placedDiscoverySystems != null)
        {
            for (int i = 0; i < placedDiscoveryCount; i++)
            {
                var ps = placedDiscoverySystems[i];
                if (ps == null || !ps.collision.enabled) continue;
                if (ps.sizeOverLifetime.enabled) WarnSizeOverLifetimeOnce(ps);
                float rr = GetSystemWorstStartSize(ps) * ps.collision.radiusScale * 0.5f;
                if (rr > r) r = rr;
            }
        }
        return r;
    }

    // 粒子专用门框判定：在主管理器自带的 InflatedPointInPortalRect 之上加一层【深度钳制】。
    // 主管理器的版本不看 z，等价于"门后无限深都算门洞内"——实测会把从出口门背后穿过的
    // 入射光束整条抓走（本项目第三轮实测：入射光束在出口门背后 10.85m 处被误判）。
    // 玩家/刚体那条线继续用原版，不受影响。
    private bool ParticleRectCheck(Vector3 localPoint, int shapeType, float inflateHalf)
    {
        if (localPoint.z < -PARTICLE_BOUNCE_Z_MAX || localPoint.z > PARTICLE_BOUNCE_Z_MAX) return false;
        return InflatedPointInPortalRect(localPoint, shapeType, inflateHalf);
    }

    // 漏检统计专用：只看横向，不做任何深度钳制
    private bool ParticleRectNoDepth(Vector3 localPoint, int shapeType, float inflateHalf)
    {
        return InflatedPointInPortalRect(localPoint, shapeType, inflateHalf);
    }

    private void ProcessRigidbodyForPortal(bool isPortalA, bool heldOnly)
    {
        Transform thisPlane = isPortalA ? portalPlaneA : portalPlaneB;
        Transform otherPlane = isPortalA ? portalPlaneB : portalPlaneA;
        int thisShape = ResolvePortalShape(isPortalA);

        if (thisPlane == null || otherPlane == null) return;

        Rigidbody[] trackers = isPortalA ? trackedRigidbodiesA : trackedRigidbodiesB;
        Vector3[] previousOffsets = isPortalA ? rbPreviousOffsetFromPortalA : rbPreviousOffsetFromPortalB;
        int[] originalLayers = isPortalA ? rbOriginalLayerA : rbOriginalLayerB;
        int[] lastSides = isPortalA ? rbLastPortalSideA : rbLastPortalSideB;
        int count = isPortalA ? trackedRBCountA : trackedRBCountB;

        float hx = portalTriggerWidth * 0.5f;
        float hy = portalTriggerHeight * 0.5f;
        float baseThresholdDepth = noClipDepth + rbTriggerDepthExtension;
        if (baseThresholdDepth < 0.01f) baseThresholdDepth = 0.01f;
        float heldThresholdDepth = noClipDepth + heldRigidbodyTriggerDepthExtension;
        if (heldThresholdDepth < 0.01f) heldThresholdDepth = 0.01f;
        if (portalGun != null) heldThresholdDepth = Mathf.Max(heldThresholdDepth, portalGun.GetHeldPortalTrackingDepth(thisPlane));

        // 查询体积可以比基础阈值大：目的是让高速刚体“有机会被加入追踪”。
        // 是否真正加入追踪，下面还会按该刚体自己的法线速度算 rbThresholdDepth。
        float queryDepth = baseThresholdDepth;
        if (enableRigidbodyDynamicTrackingDepth)
        {
            queryDepth = Mathf.Max(baseThresholdDepth, rbMaxDynamicTrackingDepth);
        }

        // 1) OnTravellerEnterPortal 替代：用 OverlapBox 找到进入门阈值体积的刚体，并加入本门追踪。
        // 注意：这里只“加入追踪”，绝不因为进入体积就直接传送；真正传送必须等下面的平面穿越判断。
        int overlapCount;
        if (heldOnly)
        {
            rbOverlapBuffer[0] = portalGun.GetHeldBodyCollider();
            overlapCount = 1;
        }
        else
        {
            overlapCount = Physics.OverlapBoxNonAlloc(
                thisPlane.position,
                new Vector3(hx, hy, queryDepth),
                rbOverlapBuffer,
                thisPlane.rotation,
                ~0,
                QueryTriggerInteraction.Collide
            );
        }

        for (int c = 0; c < overlapCount; c++)
        {
            Collider col = rbOverlapBuffer[c];
            if (col == null) continue;

            Rigidbody rb = col.attachedRigidbody;
            if (rb == null) continue;

            // 跳过我们自己创建的clone刚体：clone是没有Rigidbody的（Strip时Destroy掉），
            // 但Destroy是帧末延迟生效的，本帧/下一帧clone身上仍残留Rigidbody组件引用，
            // 如果不跳过，OverlapBox会扫到clone→又VRCInstantiate一份→clone的clone
            if (IsGameObjectOurClone(rb.gameObject)) continue;

            bool heldByGun = portalGun != null && portalGun.GetHeldRigidbody() == rb;
            if (heldOnly && !heldByGun) continue;
            if (rb.isKinematic && !heldByGun) continue;
            if (heldByGun && !allowHeldRigidbodyTeleport) continue;

            Vector3 rbWorldPos = GetRigidbodyTravellerPosition(rb, heldByGun);
            Vector3 localPos = LocalPointForPortal(thisPlane, rbWorldPos);
            if (!LocalPointInPortalRect(localPos, thisShape)) continue;

            float rbThresholdDepth = heldByGun ? heldThresholdDepth : baseThresholdDepth;
            if (!heldByGun && enableRigidbodyDynamicTrackingDepth)
            {
                float normalSpeed = Mathf.Abs(Vector3.Dot(GetRigidbodyTravellerVelocity(rb, false), thisPlane.forward));
                rbThresholdDepth += normalSpeed * Time.deltaTime * 2f;
                float maxDepth = Mathf.Max(baseThresholdDepth, rbMaxDynamicTrackingDepth);
                if (rbThresholdDepth > maxDepth) rbThresholdDepth = maxDepth;
            }
            if (Mathf.Abs(localPos.z) > rbThresholdDepth) continue;

            int originalLayer = FindTrackedRigidbodyOriginalLayer(rb);
            if (originalLayer < 0 && heldByGun && portalGun != null)
            {
                originalLayer = portalGun.GetHeldRigidbodyOriginalLayer();
            }
            if (originalLayer < 0) originalLayer = rb.gameObject.layer;

            Vector3 rbOffsetFromPortal = rbWorldPos - thisPlane.position;
            int initialSide = RBSideFromSignedDistance(Vector3.Dot(rbOffsetFromPortal, thisPlane.forward));
            if (initialSide == 0)
            {
                // 如果首次加入追踪时已经贴在门平面死区内，用法线速度推断“来自哪一侧”。
                // 速度朝 -forward：从 +侧进入；速度朝 +forward：从 -侧进入。
                float normalSpeed = Vector3.Dot(GetRigidbodyTravellerVelocity(rb, heldByGun), thisPlane.forward);
                if (normalSpeed < -rbPostCrossNormalSpeedEpsilon) initialSide = 1;
                else if (normalSpeed > rbPostCrossNormalSpeedEpsilon) initialSide = -1;
            }

            int countBeforeAdd = count;
            count = AddRigidbodyTrackerToArrays(
                rb,
                rbOffsetFromPortal,
                originalLayer,
                initialSide,
                trackers,
                previousOffsets,
                originalLayers,
                lastSides,
                count,
                false
            );

            // 遮罩叠加：被追踪的刚体临时应用透明遮罩，渲染在传送门画面之上。
            // 必须只在刚体【新加入追踪】那一帧执行：AddRigidbodyTrackerToArrays 对已在追踪的刚体原样返回，
            // 不加这道门会导致每个被追踪刚体每帧都跑一次 GetComponentsInChildren<Renderer>（纯浪费+GC）。
            // 离开追踪后由下方 RestorePortalOverlayIfUntracked 还原 renderQueue。
            if (count != countBeforeAdd)
            {
                ApplyPortalOverlayToGameObject(rb.gameObject);
            }

            // 抓取与未抓取刚体共用同一套按门Clip Volume的碰撞关系，避免叠墙只忽略一面。
            ApplyRigidbodyPortalWallIgnore(rb, isPortalA);

            // 刚体进入门体积 → 创建出口侧clone虚影（仅本地）
            if (enableRigidbodyPortalClones)
            {
                EnsureRigidbodyClone(rb, thisPlane, otherPlane);
            }

            // 遮罩叠加应用到刚体本体（已在上方 Add 后应用，这里确保 clone 也应用）
            // （已在 EnsureRigidbodyClone 后处理）
        }

        // 2) Seb HandleTravellers：对本门 trackedTravellers 做“上一帧 offset”和“当前 offset”的侧面比较。
        int i = 0;
        while (i < count)
        {
            Rigidbody rb = trackers[i];
            if (rb == null)
            {
                count = RemoveRigidbodyTrackerAt(i, trackers, previousOffsets, originalLayers, lastSides, count);
                continue;
            }

            bool heldByGun = portalGun != null && portalGun.GetHeldRigidbody() == rb;
            if (heldOnly && !heldByGun)
            {
                i++;
                continue;
            }
            if (rb.isKinematic && !heldByGun)
            {
                // kinematic且没被枪抓着（比如传送枪被丢出去变kinematic）：不再追踪，同时销毁clone防止泄漏
                RestoreRigidbodyCollisionIfSafe(rb, !isPortalA);
                DestroyRigidbodyClone(rb);
                count = RemoveRigidbodyTrackerAt(i, trackers, previousOffsets, originalLayers, lastSides, count);
                RestorePortalOverlayIfUntracked(rb);
                continue;
            }
            if (heldByGun && !allowHeldRigidbodyTeleport)
            {
                RestoreRigidbodyCollisionIfSafe(rb, !isPortalA);
                DestroyRigidbodyClone(rb);
                count = RemoveRigidbodyTrackerAt(i, trackers, previousOffsets, originalLayers, lastSides, count);
                RestorePortalOverlayIfUntracked(rb);
                continue;
            }

            // 追踪期间持续重扫Clip Volume，及时加入新叠墙并恢复已离开范围的旧关系。
            ApplyRigidbodyPortalWallIgnore(rb, isPortalA);

            // 注意：不使用帧冷却。真正的保护是下方的 crossedInsidePortal 判定：
            // 穿越必须满足"速度方向指向从门的一侧穿到另一侧"——刚被传送到出口侧的刚体，速度方向是离开门的，
            // previousSide/currentSide 不会产生符合穿越条件的换边，自然不会立刻传回。

            Vector3 rbWorldPos = GetRigidbodyTravellerPosition(rb, heldByGun);
            Vector3 currentOffset = rbWorldPos - thisPlane.position;
            Vector3 previousOffset = previousOffsets[i];

            float previousDot = Vector3.Dot(previousOffset, thisPlane.forward);
            float currentDot = Vector3.Dot(currentOffset, thisPlane.forward);

            int previousSide = lastSides[i];
            if (previousSide == 0) previousSide = RBSideFromSignedDistance(previousDot);
            int currentSide = RBSideFromSignedDistance(currentDot);

            bool crossedPortalPlane = false;
            if (previousSide != 0 && currentSide != 0 && previousSide != currentSide)
            {
                crossedPortalPlane = true;
            }
            else
            {
                // 兜底：上一帧/首次追踪点贴在 z≈0 死区内，但本帧已经明确到了另一侧。
                // 这正是低速穿越时“没有触发然后掉虚空”的常见路径。
                if (previousSide != 0 && currentSide == -previousSide)
                {
                    crossedPortalPlane = true;
                }
            }

            bool crossedInsidePortal = false;
            Vector3 crossingWorldPosForTeleport = rbWorldPos;
            float crossingT = 1f;
            if (crossedPortalPlane)
            {
                Vector3 previousWorldPos = thisPlane.position + previousOffset;
                float denom = previousDot - currentDot;
                if (Mathf.Abs(denom) > 0.0001f)
                {
                    crossingT = Mathf.Clamp01(previousDot / denom);
                }

                crossingWorldPosForTeleport = Vector3.Lerp(previousWorldPos, rbWorldPos, crossingT);
                Vector3 crossingLocal = LocalPointForPortal(thisPlane, crossingWorldPosForTeleport);
                crossedInsidePortal = LocalPointInPortalRect(crossingLocal, thisShape);
                if (heldByGun) crossedInsidePortal = crossedInsidePortal && portalGun.CanHeldFitPortal(thisPlane, crossingWorldPosForTeleport - rbWorldPos);
            }

            if (crossedInsidePortal)
            {
                int originalLayer = originalLayers[i];

                // ★核心修复：不销毁clone再重建——而是直接翻转已有clone的fromPortal引用。
                // 原因：销毁旧clone(SetActive(false)但Destroy延迟)+VRCInstantiate新clone，这个过程会产生一帧空档。
                // 翻转方案：clone对象保持不变，只把cloneTargetPortals从"入口门"改成"出口门"。
                if (enableRigidbodyPortalClones)
                {
                    FlipClonePortalAfterTeleport(rb, otherPlane);
                }

                // Seb 原版：traveller.Teleport(from, to, m.GetColumn(3), m.rotation)
                TeleportRigidbodySebStyle(rb, thisPlane, otherPlane, isPortalA, crossingWorldPosForTeleport, crossingT, heldByGun);

                RestoreRigidbodyPortalWallIgnore(rb);
                AddRigidbodyTracker(!isPortalA, rb, GetRigidbodyTravellerPosition(rb, heldByGun) - otherPlane.position, originalLayer, RBSideFromSignedDistance(Vector3.Dot(GetRigidbodyTravellerPosition(rb, heldByGun) - otherPlane.position, otherPlane.forward)));

                // 兜底：如果因为某种原因clone不存在（刚进入volume同帧就穿越），补建一个。
                if (enableRigidbodyPortalClones && FindCloneIndexForRigidbody(rb) < 0)
                {
                    EnsureRigidbodyClone(rb, otherPlane, thisPlane);
                }

                count = RemoveRigidbodyTrackerAt(i, trackers, previousOffsets, originalLayers, lastSides, count);
                // 穿门交接路径：上面已把刚体加入对面门的追踪，RestorePortalOverlayIfUntracked 内部会
                // 因"仍被追踪"而跳过还原——这里调用只为保持所有移除路径的统一不变式。
                RestorePortalOverlayIfUntracked(rb);
                continue;
            }

            Vector3 currentLocal = LocalPointForPortal(thisPlane, rbWorldPos);
            float currentTrackDepth = heldByGun ? heldThresholdDepth : baseThresholdDepth;
            bool stillInsideThreshold = LocalPointInPortalRect(currentLocal, thisShape) && Mathf.Abs(currentLocal.z) <= currentTrackDepth;
            if (!stillInsideThreshold)
            {
                // Seb OnTriggerExit 替代：离开门阈值后移除 traveller；先释放本门的临时碰撞忽略。
                // 另一扇门仍持有同一刚体时，保留它的临时碰撞忽略关系。
                RestoreRigidbodyCollisionIfSafe(rb, !isPortalA);
                if (heldByGun && portalGun != null && !IsRigidbodyTrackedByPortal(!isPortalA, rb))
                {
                    portalGun.RestoreHeldRigidbodyLayerAfterPortal();
                }
                DestroyRigidbodyClone(rb);
                count = RemoveRigidbodyTrackerAt(i, trackers, previousOffsets, originalLayers, lastSides, count);
                RestorePortalOverlayIfUntracked(rb);
                continue;
            }

            // Seb 原版未穿越时：traveller.previousOffsetFromPortal = offsetFromPortal。
            previousOffsets[i] = currentOffset;
            if (currentSide != 0) lastSides[i] = currentSide;
            i++;
        }

        if (isPortalA) trackedRBCountA = count;
        else trackedRBCountB = count;
    }

    public bool TryMapRayThroughPortal(Vector3 rayOrigin, Vector3 rayDirection, float maxDistance, out Vector3 mappedOrigin, out Vector3 mappedDirection, out float remainingDistance, out Transform fromPortal, out Transform toPortal)
    {
        mappedOrigin = Vector3.zero;
        mappedDirection = Vector3.forward;
        remainingDistance = 0f;
        fromPortal = null;
        toPortal = null;

        if (portalPlaneA == null || portalPlaneB == null) return false;
        if (rayDirection.sqrMagnitude < 0.0001f) return false;
        rayDirection.Normalize();

        float bestT = maxDistance + 1f;
        bool found = false;
        Transform bestFrom = null;
        Transform bestTo = null;
        Vector3 bestHit = Vector3.zero;

        float tA;
        Vector3 hitA;
        if (TryRayPortalIntersection(rayOrigin, rayDirection, maxDistance, portalPlaneA, ResolvePortalShape(true), out tA, out hitA))
        {
            if (tA < bestT)
            {
                bestT = tA;
                bestHit = hitA;
                bestFrom = portalPlaneA;
                bestTo = portalPlaneB;
                found = true;
            }
        }

        float tB;
        Vector3 hitB;
        if (TryRayPortalIntersection(rayOrigin, rayDirection, maxDistance, portalPlaneB, ResolvePortalShape(false), out tB, out hitB))
        {
            if (tB < bestT)
            {
                bestT = tB;
                bestHit = hitB;
                bestFrom = portalPlaneB;
                bestTo = portalPlaneA;
                found = true;
            }
        }

        if (!found) return false;

        Vector3 localPoint = LocalPointForPortal(bestFrom, bestHit);
        Vector3 localDir = LocalDirForPortal(bestFrom, rayDirection);
        if (useClassicHalfTurn)
        {
            Quaternion halfTurn = LocalHalfTurn();
            localPoint = halfTurn * localPoint;
            localDir = halfTurn * localDir;
        }

        mappedDirection = WorldDirFromPortal(bestTo, localDir);
        if (mappedDirection.sqrMagnitude < 0.0001f) return false;
        mappedDirection.Normalize();

        // 稍微推出出口门面，避免第二段 Raycast 立刻打回门面/边框自己。
        mappedOrigin = WorldPointFromPortal(bestTo, localPoint) + mappedDirection * 0.02f;
        remainingDistance = Mathf.Max(0f, maxDistance - bestT);
        fromPortal = bestFrom;
        toPortal = bestTo;
        return remainingDistance > 0.01f;
    }

    bool TryRayPortalIntersection(Vector3 rayOrigin, Vector3 rayDirection, float maxDistance, Transform portalPlane, int shapeType, out float t, out Vector3 hitPoint)
    {
        t = 0f;
        hitPoint = Vector3.zero;
        if (portalPlane == null) return false;

        float denom = Vector3.Dot(rayDirection, portalPlane.forward);
        if (Mathf.Abs(denom) < 0.0001f) return false;

        t = Vector3.Dot(portalPlane.position - rayOrigin, portalPlane.forward) / denom;
        if (t <= 0.01f || t >= maxDistance) return false;

        hitPoint = rayOrigin + rayDirection * t;
        Vector3 local = LocalPointForPortal(portalPlane, hitPoint);
        return LocalPointInPortalRect(local, shapeType);
    }

    private int RBSideFromSignedDistance(float signedDistance)
    {
        float eps = Mathf.Max(Mathf.Abs(crossingEpsilon), 0.0001f);
        if (signedDistance > eps) return 1;
        if (signedDistance < -eps) return -1;
        return 0;
    }

    private Vector3 GetRigidbodyTravellerPosition(Rigidbody rb, bool heldByGun)
    {
        if (rb == null) return Vector3.zero;
        return rb.position;
    }

    private Vector3 GetRigidbodyTravellerVelocity(Rigidbody rb, bool heldByGun)
    {
        if (rb == null) return Vector3.zero;
        return rb.velocity;
    }

    private void TeleportRigidbodySebStyle(Rigidbody rb, Transform fromPlane, Transform toPlane, bool fromAtoB, Vector3 crossingWorldPos, float crossingT, bool heldByGun)
    {
        if (rb == null || fromPlane == null || toPlane == null) return;

        float postCrossDt = 0f;
        Vector3 currentTravellerPos = GetRigidbodyTravellerPosition(rb, heldByGun);
        Vector3 currentVelocity = GetRigidbodyTravellerVelocity(rb, heldByGun);
        Vector3 sourcePosForMapping = currentTravellerPos;
        if (enableRigidbodyCrossingTimeCorrection)
        {
            postCrossDt = Mathf.Clamp01(1f - crossingT) * Time.deltaTime;

            if (useRigidbodyDistanceBasedPostCrossTime)
            {
                // 更稳定的刚体版 post-cross 时间：用“当前已经越过门平面的距离 / 当前沿门法线速度”反推。
                // 这样不依赖 LateUpdate 的 Time.deltaTime 是否等于物理步实际推进时间，可减少地板双洞的系统性亏能/增能。
                float currentSignedDistance = Vector3.Dot(currentTravellerPos - fromPlane.position, fromPlane.forward);
                float currentNormalSpeed = Vector3.Dot(currentVelocity, fromPlane.forward);
                float absNormalSpeed = Mathf.Abs(currentNormalSpeed);
                if (absNormalSpeed > Mathf.Abs(rbPostCrossNormalSpeedEpsilon))
                {
                    postCrossDt = Mathf.Abs(currentSignedDistance) / absNormalSpeed;
                    if (postCrossDt < 0f) postCrossDt = 0f;
                    if (postCrossDt > rbPostCrossMaxDt) postCrossDt = rbPostCrossMaxDt;
                }
            }

            sourcePosForMapping = crossingWorldPos;
        }

        Matrix4x4 fromWorldToLocal = Matrix4x4.TRS(fromPlane.position, fromPlane.rotation, Vector3.one).inverse;
        Matrix4x4 toLocalToWorld = Matrix4x4.TRS(toPlane.position, toPlane.rotation, Vector3.one);
        Matrix4x4 rbLocalToWorld = Matrix4x4.TRS(sourcePosForMapping, rb.rotation, rb.transform.lossyScale);

        Matrix4x4 mappedMatrix;
        if (useClassicHalfTurn)
        {
            Matrix4x4 halfTurnMatrix = Matrix4x4.Rotate(LocalHalfTurn());
            mappedMatrix = toLocalToWorld * halfTurnMatrix * fromWorldToLocal * rbLocalToWorld;
        }
        else
        {
            mappedMatrix = toLocalToWorld * fromWorldToLocal * rbLocalToWorld;
        }

        Vector3 mappedPosAtCrossing = mappedMatrix.GetColumn(3);
        Quaternion newRot = mappedMatrix.rotation;

        // Seb PortalPhysicsObject：
        // rigidbody.velocity = toPortal.TransformVector(fromPortal.InverseTransformVector(rigidbody.velocity));
        // rigidbody.angularVelocity = toPortal.TransformVector(fromPortal.InverseTransformVector(rigidbody.angularVelocity));
        Vector3 gravityAccel = Vector3.zero;
        if (!heldByGun && rb.useGravity && !rb.isKinematic)
        {
            gravityAccel = Physics.gravity;
        }

        Vector3 velocityAtCrossing = currentVelocity;
        if (enableRigidbodyCrossingTimeCorrection)
        {
            velocityAtCrossing = currentVelocity - gravityAccel * postCrossDt;
        }

        Vector3 localVelocity = LocalDirForPortal(fromPlane, velocityAtCrossing);
        Vector3 localAngularVelocity = LocalDirForPortal(fromPlane, rb.angularVelocity);
        if (useClassicHalfTurn)
        {
            localVelocity = LocalHalfTurn(localVelocity);
            localAngularVelocity = LocalHalfTurn(localAngularVelocity);
        }
        Vector3 velocityMappedAtCrossing = WorldDirFromPortal(toPlane, localVelocity);
        Vector3 newAngularVelocity = WorldDirFromPortal(toPlane, localAngularVelocity);

        Vector3 newPos = mappedPosAtCrossing;
        Vector3 newVelocity = velocityMappedAtCrossing;
        if (enableRigidbodyCrossingTimeCorrection && postCrossDt > 0f)
        {
            newPos = mappedPosAtCrossing + velocityMappedAtCrossing * postCrossDt + gravityAccel * (0.5f * postCrossDt * postCrossDt);
            newVelocity = velocityMappedAtCrossing + gravityAccel * postCrossDt;
        }

        // 出口侧位置保险（和玩家同逻辑）：
        // 传送后若刚体落在出口门的"错误侧/太贴门"，沿出口法线拉回到触发平面外缘，
        // 防止重力下掉立刻被对面门再次判定为穿门导致AB来回跳（地洞同平面场景的"水面漂浮感"）。
        // 纯位置修正，不改变速度/动能，不做沿法线方向加速推出——那才是"吸铁石"的根源。
        // 换句话来说，也就是刚传送后如果位置算得太贴门，沿法线方向挪出触发平面一点点，给物理一帧缓冲时间自然下落。
        if (enableExitSideCorrection)
        {
            Vector3 localAfter = LocalPointForPortal(toPlane, newPos);
            float exitTargetDist = Mathf.Max(Mathf.Abs(exitSideMinDistance), teleportTriggerOffset);
            // 出口侧期望z为正（沿出口门forward方向是"门外"）
            if (localAfter.z < exitTargetDist)
            {
                float fix = exitTargetDist - localAfter.z;
                newPos += toPlane.forward * fix;
            }
        }

        rb.position = newPos;
        rb.rotation = newRot;
        // ★关键修复：Rigidbody.position/rotation是物理引擎内部值，Unity默认会在晚些时候（FixedUpdate结尾或下一帧）
        // 才把变换同步到Transform上；但我们本帧紧接着的UpdateRigidbodyClonePoses/递归渲染/画面呈现需要
        // transform.position立刻反映新位置，否则会出现：脚本读rb.position已经是出口位，
        // 但画面里本体的transform还停在入口原位→本体和clone视觉重叠一帧，下一帧transform同步完本体才"跳"到出口。
        // 显式同步transform，保证本帧渲染前位置一致。
        // 换句话来说，也就是"物理引擎和transform是两套缓存，我们两边都写，让本帧渲染看到的本体位置和脚本认知一致，不留给同步延迟一帧"。
        rb.transform.position = newPos;
        rb.transform.rotation = newRot;
        rb.velocity = newVelocity;
        rb.angularVelocity = newAngularVelocity;

        if (portalGun != null && portalGun.GetHeldRigidbody() == rb && allowHeldRigidbodyTeleport)
        {
            portalGun.UpdateHeldAfterTeleport(newPos, newRot, fromPlane, toPlane, useClassicHalfTurn);
        }

        // P2：已原子删除刚体传送调试日志
    }

    private int AddRigidbodyTrackerToArrays(Rigidbody rb, Vector3 previousOffset, int originalLayer, int lastSide, Rigidbody[] trackers, Vector3[] previousOffsets, int[] originalLayers, int[] lastSides, int count, bool refreshExisting)
    {
        if (rb == null) return count;

        for (int i = 0; i < count; i++)
        {
            if (trackers[i] == rb)
            {
                if (refreshExisting)
                {
                    // Seb linkedPortal.OnTravellerEnterPortal 会在传送后把 previousOffset 重置为出口门当前 offset。
                    // 但普通 OverlapBox 入口扫描不能刷新已有项，否则 previousOffset 每帧等于当前值，穿越检测会永远失效。
                    previousOffsets[i] = previousOffset;
                    if (originalLayer >= 0) originalLayers[i] = originalLayer;
                    lastSides[i] = lastSide;
                }
                return count;
            }
        }

        if (count >= MAX_TRACKED_RBS) return count;

        trackers[count] = rb;
        previousOffsets[count] = previousOffset;
        originalLayers[count] = originalLayer;
        lastSides[count] = lastSide;
        return count + 1;
    }

    private void AddRigidbodyTracker(bool isPortalA, Rigidbody rb, Vector3 previousOffset, int originalLayer, int lastSide)
    {
        if (isPortalA)
        {
            trackedRBCountA = AddRigidbodyTrackerToArrays(rb, previousOffset, originalLayer, lastSide, trackedRigidbodiesA, rbPreviousOffsetFromPortalA, rbOriginalLayerA, rbLastPortalSideA, trackedRBCountA, true);
        }
        else
        {
            trackedRBCountB = AddRigidbodyTrackerToArrays(rb, previousOffset, originalLayer, lastSide, trackedRigidbodiesB, rbPreviousOffsetFromPortalB, rbOriginalLayerB, rbLastPortalSideB, trackedRBCountB, true);
        }
    }

    private int RemoveRigidbodyTrackerAt(int removeIndex, Rigidbody[] trackers, Vector3[] previousOffsets, int[] originalLayers, int[] lastSides, int count)
    {
        if (removeIndex < 0 || removeIndex >= count) return count;

        for (int j = removeIndex; j < count - 1; j++)
        {
            trackers[j] = trackers[j + 1];
            previousOffsets[j] = previousOffsets[j + 1];
            originalLayers[j] = originalLayers[j + 1];
            lastSides[j] = lastSides[j + 1];
        }

        int last = count - 1;
        trackers[last] = null;
        previousOffsets[last] = Vector3.zero;
        originalLayers[last] = -1;
        lastSides[last] = 0;
        return count - 1;
    }

    private int FindTrackedRigidbodyOriginalLayer(Rigidbody rb)
    {
        if (rb == null) return -1;

        for (int i = 0; i < trackedRBCountA; i++)
        {
            if (trackedRigidbodiesA[i] == rb) return rbOriginalLayerA[i];
        }
        for (int i = 0; i < trackedRBCountB; i++)
        {
            if (trackedRigidbodiesB[i] == rb) return rbOriginalLayerB[i];
        }
        return -1;
    }

    public int GetTrackedRigidbodyOriginalLayer(Rigidbody rb)
    {
        return FindTrackedRigidbodyOriginalLayer(rb);
    }

    public bool IsRigidbodyTrackedByAnyPortal(Rigidbody rb)
    {
        return IsRigidbodyTrackedByPortal(true, rb) || IsRigidbodyTrackedByPortal(false, rb);
    }

    private bool IsRigidbodyTrackedByPortal(bool isPortalA, Rigidbody rb)
    {
        if (rb == null) return false;

        Rigidbody[] trackers = isPortalA ? trackedRigidbodiesA : trackedRigidbodiesB;
        int count = isPortalA ? trackedRBCountA : trackedRBCountB;
        for (int i = 0; i < count; i++)
        {
            if (trackers[i] == rb) return true;
        }
        return false;
    }

    // ============================================================
    // Clip Volume 批量穿透：用 BoxCollider 做 OverlapBoxNonAlloc，
    // 玩家靠近时把体积内所有非刚体、非Trigger、非门自身的静态Collider切到 playerPassThroughLayer，
    // 离开/不在附近时全部还原。设计为每帧"全量重扫+重切+还原不在体积内"，不做引用计数，代码最简、行为最鲁棒。
    // ============================================================
    private void UpdateClipVolumePassThrough(
        BoxCollider clipVolume, Transform portalPlane,
        Vector3 playerHead, Vector3 playerFeet, int shapeType,
        Collider[] tracked, int[] originalLayers, ref int trackedCount)
    {
        if (!enableClipVolumePassThrough)
        {
            RestoreAllClipVolumeColliders(tracked, originalLayers, ref trackedCount);
            return;
        }
        if (clipVolume == null || !clipVolume.enabled)
        {
            RestoreAllClipVolumeColliders(tracked, originalLayers, ref trackedCount);
            return;
        }

        // 判断玩家是否在门附近（复用现有bodyInColliderZone逻辑：XY在门框内+深度在穿透范围内）
        bool playerNear = IsBodyInColliderZone(portalPlane, playerHead, playerFeet, shapeType);

        if (!playerNear)
        {
            RestoreAllClipVolumeColliders(tracked, originalLayers, ref trackedCount);
            return;
        }

        // 玩家在门附近：扫描clipVolume内所有碰撞体，切符合条件的到 playerPassThroughLayer
        // 注意：Physics.OverlapBox 的 halfExtents 是"沿传入rotation的本地轴"的半长度，
        // 不是 bounds.extents（bounds是世界AABB，旋转后会比实际大一圈）。正确做法是用
        // collider.size * lossyScale 作为本地halfExtents，center是collider的世界transformPoint(center)，
        // rotation是collider的世界旋转。
        Transform clipT = clipVolume.transform;
        Vector3 worldCenter = clipT.TransformPoint(clipVolume.center);
        Vector3 halfExtents = Vector3.Scale(clipVolume.size * 0.5f, clipT.lossyScale);
        Quaternion rot = clipT.rotation;

        int overlapCount = Physics.OverlapBoxNonAlloc(
            worldCenter, halfExtents, clipVolumeOverlapBuffer, rot,
            ~0, QueryTriggerInteraction.Ignore);

        for (int i = 0; i < overlapCount; i++)
        {
            Collider col = clipVolumeOverlapBuffer[i];
            if (col == null) continue;
            if (col == clipVolume) continue;
            if (col.isTrigger) continue;
            // 动态物体由刚体专用的逐Collider忽略逻辑处理，这里只处理静态墙/地板。
            if (col.attachedRigidbody != null) continue;
            // 排除整个传送门根节点下的碰撞体，包括与 portalPlane 同级的 Pipe 边框。
            Transform portalRoot = isPortalA ? portalParentA : portalParentB;
            if (IsColliderUnderPortalHierarchy(col, portalRoot)) continue;

            GameObject obj = col.gameObject;
            // 已经是目标 layer：
            if (obj.layer == playerPassThroughLayer)
            {
                // 只有我们之前 track 过的才保留（originalLayers[i] 是真原始层）；
                // 如果不是我们 track 的（被 markedCollider 逻辑或另一扇门 clipVolume 先切的），
                // 不接管：让"谁切的谁还原"，否则 original 会错记成17导致永远还原不回来。
                if (IsColliderTrackedByUs(col, tracked, trackedCount))
                {
                    // 保留在 tracked 列表，write 阶段由下面的 stillInVolume 逻辑处理
                }
                continue;
            }

            int original = obj.layer;
            obj.layer = playerPassThroughLayer;
            AddColliderToTracked(col, original, tracked, originalLayers, ref trackedCount);
        }

        // 还原这一帧扫描后"已不在volume内"的已追踪collider（它们可能被物体挪走，或者我们本帧扫描范围没覆盖到）
        // 简化策略：遍历tracked列表，如果该collider当前不在扫描结果里，则还原
        // 因为我们本帧刚扫完 clipVolumeOverlapBuffer 包含所有当前在内的collider，直接比对即可
        int write = 0;
        for (int i = 0; i < trackedCount; i++)
        {
            Collider tc = tracked[i];
            if (tc == null) continue;
            bool stillInVolume = false;
            for (int j = 0; j < overlapCount; j++)
            {
                if (clipVolumeOverlapBuffer[j] == tc) { stillInVolume = true; break; }
            }
            if (stillInVolume && tc.gameObject.layer == playerPassThroughLayer)
            {
                // 仍在内，仍在目标layer，保留
                if (write != i)
                {
                    tracked[write] = tracked[i];
                    originalLayers[write] = originalLayers[i];
                }
                write++;
            }
            else
            {
                // 不在内，或已不在目标layer（被其他逻辑改过）：还原原始layer
                if (tc.gameObject.layer == playerPassThroughLayer)
                {
                    tc.gameObject.layer = originalLayers[i];
                }
            }
        }
        // 清掉尾部
        for (int i = write; i < trackedCount; i++)
        {
            tracked[i] = null;
            originalLayers[i] = -1;
        }
        trackedCount = write;
    }

    private bool IsColliderUnderPortalHierarchy(Collider col, Transform portalPlane)
    {
        if (col == null || portalPlane == null) return false;
        Transform t = col.transform;
        // 向上最多查16层父物体，防死循环/长链
        for (int i = 0; i < 16 && t != null; i++)
        {
            if (t == portalPlane) return true;
            t = t.parent;
        }
        return false;
    }

    private bool IsColliderTrackedByUs(Collider col, Collider[] tracked, int count)
    {
        for (int i = 0; i < count; i++)
        {
            if (tracked[i] == col) return true;
        }
        return false;
    }

    // 查 Clip Volume 追踪表：col 若正被 A/B 任一侧 clip volume 追踪，返回当时记录的原始 layer；否则 -1。
    // 语义："谁切的谁记录了真原始值"——clip volume 只在 layer != 穿透层时才接管并记录，
    // 所以表里的值一定是切换前的真实 layer，可作为 markedCollider 系统被污染时的真值来源。
    // 剪刀穿模场景：A门clipVolume包住穿模过来的、B门所在的斜面，B的markedCollider逻辑
    // 第一次读到斜面时它已经在穿透层，必须靠这张表找回真原始层。
    private int FindClipVolumeOriginalLayer(Collider col)
    {
        if (col == null) return -1;
        for (int i = 0; i < clipVolumeTrackedCountA; i++)
        {
            if (clipVolumeTrackedCollidersA[i] == col) return clipVolumeOriginalLayersA[i];
        }
        for (int i = 0; i < clipVolumeTrackedCountB; i++)
        {
            if (clipVolumeTrackedCollidersB[i] == col) return clipVolumeOriginalLayersB[i];
        }
        return -1;
    }

    private void AddColliderToTracked(Collider col, int originalLayer, Collider[] tracked, int[] originalLayers, ref int count)
    {
        // 去重
        for (int i = 0; i < count; i++)
        {
            if (tracked[i] == col) return;
        }
        if (count >= MAX_CLIP_VOLUME_COLLIDERS) return; // 满了就丢弃（极端情况）
        tracked[count] = col;
        originalLayers[count] = originalLayer;
        count++;
    }

    private void RestoreAllClipVolumeColliders(Collider[] tracked, int[] originalLayers, ref int count)
    {
        for (int i = 0; i < count; i++)
        {
            Collider tc = tracked[i];
            if (tc != null && tc.gameObject.layer == playerPassThroughLayer && originalLayers[i] >= 0)
            {
                tc.gameObject.layer = originalLayers[i];
            }
            tracked[i] = null;
            originalLayers[i] = -1;
        }
        count = 0;
    }


    // ============================================================
    // 刚体穿门clone虚影（本地非网络同步）
    // 刚体进入门volume时VRCInstantiate一份副本放到出口侧，不带Rigidbody，每帧手动同步位姿；
    // 刚体真正传送或离开volume时Destroy clone。仅本地可见/可碰。
    // ============================================================

    private int FindCloneIndexForRigidbody(Rigidbody rb)
    {
        for (int i = 0; i < cloneCount; i++)
        {
            if (cloneOriginalRigidbodies[i] == rb) return i;
        }
        return -1;
    }

    private void EnsureRigidbodyClone(Rigidbody rb, Transform fromPortal, Transform toPortal)
    {
        if (rb == null) return;
        if (FindCloneIndexForRigidbody(rb) >= 0) return; // 已有clone
        if (cloneCount >= maxRigidbodyClones && cloneCount < MAX_RIGIDBODY_CLONES) return;
        if (cloneCount >= MAX_RIGIDBODY_CLONES) return;

        GameObject original = rb.gameObject;
        if (original == null) return;

        // 先分配slot号：材质跟随缓存（SyncCloneMaterials）要挂在这个slot下，
        // 必须在 VRCInstantiate 可能提前 return 之前确定。
        int idx = cloneCount;

        // VRCInstantiate 本地拷贝（非网络同步）
        GameObject clone = VRCInstantiate(original);
        if (clone == null) return;

        // 先禁用clone，处理完组件再启用
        clone.SetActive(false);
        clone.name = original.name + "_PortalClone";

        // 销毁clone上所有Rigidbody/Pickup/Udon/音效/粒子/动画等组件，只保留 Transform+Renderer+MeshFilter+Collider
        StripCloneComponents(clone);

        // 材质同步 + 跟随缓存：把原物体每个Renderer当前引用的材质实例（含动画控制器正在驱动的
        // 动画值，比如材质变色）赋给clone对应Renderer，并缓存渲染器配对供每帧校正。
        // 注意：这一次赋值可能被帧末的"clone Animator销毁还原"撤销，真正兜底靠 RepointCloneMaterials。
        SyncCloneMaterials(original, clone, idx);

        // layer保持和原物体一致（用户要求），不手动改

        // 记录clone映射（slot号 idx 已在 VRCInstantiate 之前分配，材质跟随缓存依赖它）
        cloneOriginalRigidbodies[idx] = rb;
        cloneGameObjects[idx] = clone;
        cloneTargetPortals[idx] = fromPortal;
        clonePendingActivation[idx] = true;
        cloneCount++;

        // 遮罩叠加：clone 也需要在传送门画面之上渲染
        ApplyPortalOverlayToGameObject(clone);

        // 创建时不立即 SetActive(true)：留到本帧末 UpdateRigidbodyClonePoses 摆好位置后再激活，避免闪在本体位置。
    }

    // 传送瞬间：翻转已有clone的fromPortal引用，避免"销毁+重建clone"造成的一帧位置间隙。
    private void FlipClonePortalAfterTeleport(Rigidbody rb, Transform newFromPortal)
    {
        int idx = FindCloneIndexForRigidbody(rb);
        if (idx < 0) return;
        cloneTargetPortals[idx] = newFromPortal;
    }

    // 材质同步 + 跟随缓存：把本体渲染器当前引用的材质（sharedMaterials 读取不会强制实例化，
    // 不会对本体产生副作用；若本体材质正被动画控制器实例化驱动，这里拿到的就是带当前动画值的实例）
    // 赋给clone对应渲染器，同时把渲染器配对缓存下来，供 UpdateRigidbodyClonePoses 每帧校正。
    private void SyncCloneMaterials(GameObject original, GameObject clone, int cloneIdx)
    {
        if (original == null || clone == null) return;
        Renderer[] origRenderers = original.GetComponentsInChildren<Renderer>(true);
        Renderer[] cloneRenderers = clone.GetComponentsInChildren<Renderer>(true);

        if (cloneIdx >= 0 && cloneIdx < MAX_RIGIDBODY_CLONES)
        {
            cloneMaterialSyncOriginalRenderers[cloneIdx] = origRenderers;
            cloneMaterialSyncCloneRenderers[cloneIdx] = cloneRenderers;
        }

        int count = Mathf.Min(origRenderers.Length, cloneRenderers.Length);
        for (int i = 0; i < count; i++)
        {
            Renderer origR = origRenderers[i];
            Renderer cloneR = cloneRenderers[i];
            if (origR == null || cloneR == null) continue;
            cloneR.sharedMaterials = origR.sharedMaterials;
        }
    }

    // 材质实时跟随（双层校正）：
    // 第一层 - 材质引用校正：把clone渲染器的材质重新指回本体渲染器当前持有的实例，
    //   覆盖"clone的Animator被Destroy(帧末延迟)时材质被还原回资产"、"本体换了材质实例"等场景。
    // 第二层 - MaterialPropertyBlock 同步（动画值真正所在的地方）：联网查证（Unity官方文档/论坛）确认，
    //   Animator 对材质属性的动画不写进材质本身，而是通过渲染器的 MaterialPropertyBlock 应用；
    //   因此只做材质共享/复制永远同步不到动画值（这就是第一版修复无效的根因）。
    //   每帧对本体渲染器 GetPropertyBlock（官方文档：传入的block会被完全覆盖，无残留），
    //   再 SetPropertyBlock 到clone渲染器。GetPropertyBlock/SetPropertyBlock/new MaterialPropertyBlock()
    //   均为官方API，且已联网查证 UdonSharp 支持（有多个真实VRChat世界用例）。
    //   注意：不使用 HasPropertyBlock 做门控——它只认 SetPropertyBlock 写入的块，可能漏掉动画驱动的块。
    private void RepointCloneMaterials(int cloneIdx)
    {
        if (cloneIdx < 0 || cloneIdx >= MAX_RIGIDBODY_CLONES) return;
        Renderer[] origRenderers = cloneMaterialSyncOriginalRenderers[cloneIdx];
        Renderer[] cloneRenderers = cloneMaterialSyncCloneRenderers[cloneIdx];
        if (origRenderers == null || cloneRenderers == null) return;

        if (clonePropertyBlockSyncBuffer == null) clonePropertyBlockSyncBuffer = new MaterialPropertyBlock();

        int count = Mathf.Min(origRenderers.Length, cloneRenderers.Length);
        for (int i = 0; i < count; i++)
        {
            Renderer origR = origRenderers[i];
            Renderer cloneR = cloneRenderers[i];
            if (origR == null || cloneR == null) continue;

            // 第一层：材质引用校正（slot0 sharedMaterial做廉价探针，单引用读取无数组分配；引用真变了才整体重写）
            if (cloneR.sharedMaterial != origR.sharedMaterial)
            {
                cloneR.sharedMaterials = origR.sharedMaterials;
            }

            // 第二层：PropertyBlock 同步。无条件每帧取+写：动画每一帧都可能改block里的值，
            // 且clone首次可见前本函数必定先跑一次（UpdateRigidbodyClonePoses 在激活clone前调用）。
            origR.GetPropertyBlock(clonePropertyBlockSyncBuffer);
            cloneR.SetPropertyBlock(clonePropertyBlockSyncBuffer);
        }
    }

    private void StripCloneComponents(GameObject go)
    {
        // 递归销毁go及其所有子物体上"非视觉/非碰撞"的组件
        if (cloneDestroyTypes == null)
        {
            cloneDestroyTypes = new System.Type[]
            {
                typeof(Rigidbody),
                typeof(VRC.SDKBase.VRC_Pickup),
                typeof(AudioSource),
                typeof(Light),
                typeof(ParticleSystem),
                typeof(Animator),
                typeof(UdonSharpBehaviour),
            };
        }
        StripComponentsOnSingleObject(go);
        foreach (Transform child in go.transform)
        {
            StripCloneComponents(child.gameObject);
        }
    }

    private void StripComponentsOnSingleObject(GameObject go)
    {
        if (cloneDestroyTypes == null) return;
        foreach (var type in cloneDestroyTypes)
        {
            if (type == null) continue;
            Component[] comps = go.GetComponents(type);
            foreach (var c in comps)
            {
                if (c != null) Destroy(c);
            }
        }
    }

    // 手持刚体"松手提交"：握持期间枪独占刚体位置、传送门系统只显示clone镜像；
    // 松手瞬间若刚体伸进了某扇门，一次性把它传送到另一侧（镜像位置）。
    // 只在释放时调用一次、不参与每帧定位，不会与枪的 MovePosition 拉扯（无鬼畜）。
    // 两级判定：
    //   优先级1：有活跃clone → 映射方向明确，本体在该门平面后侧 → 对齐到clone位姿。
    //   优先级2：无clone → 纯几何判定（本体过某门平面且落在门框内 → 镜像传送到另一侧）。
    //     这一级必不可少：深捅（枪几乎贴到/穿过门面）时握持点(枪口前1米)会超过
    //     追踪深度(noClipDepth+rbTriggerDepthExtension≈1.1米)，追踪被移除、clone被销毁；
    //     只靠clone就会在松手时漏提交，刚体留在门后墙体里——正是"松手出现在门后面"的根因。
    // 防拽回闸门：本体已在另一扇门的门前区域内（"已经出来了"的状态）→ 不提交，
    // 防止门位靠近/交叠的场景里把本体错误拽回。
    public void CommitHeldRigidbodyToClone(Rigidbody rb)
    {
        if (rb == null) return;
        if (!enableRigidbodyTeleport) return;

        // 优先级1：有活跃clone
        int idx = FindCloneIndexForRigidbody(rb);
        if (idx >= 0)
        {
            GameObject clone = cloneGameObjects[idx];
            Transform fromPortal = cloneTargetPortals[idx];
            if (clone == null || fromPortal == null) return;
            // 本体不在该门平面后侧（正常握持/已出出口的状态）：不提交
            if (LocalPointForPortal(fromPortal, rb.position).z >= 0f) return;

            Vector3 targetPos = clone.transform.position;
            Quaternion targetRot = clone.transform.rotation;
            rb.position = targetPos;
            rb.rotation = targetRot;
            rb.transform.position = targetPos;
            rb.transform.rotation = targetRot;
            rb.velocity = Vector3.zero;
            rb.angularVelocity = Vector3.zero;
            DestroyRigidbodyClone(rb);
            return;
        }

        // 优先级2：无clone（深捅掉出追踪），几何直判
        if (portalPlaneA != null && TryCommitInsertedHeldBody(rb, portalPlaneA, portalPlaneB)) return;
        if (portalPlaneB != null) TryCommitInsertedHeldBody(rb, portalPlaneB, portalPlaneA);
    }

    // 松手提交优先级2：本体已过 fromPlane 平面且落在门框内 → 按 clone/传送同款数学镜像到 toPlane 侧。
    private bool TryCommitInsertedHeldBody(Rigidbody rb, Transform fromPlane, Transform toPlane)
    {
        if (fromPlane == null || toPlane == null) return false;

        Vector3 localPos = LocalPointForPortal(fromPlane, rb.position);
        if (localPos.z >= 0f) return false;
        if (!LocalPointInPortalRect(localPos, ResolvePortalShape(fromPlane == portalPlaneA))) return false;

        // 防拽回：本体已处于另一扇门的门前追踪区域内（大概率是"已经出来了"的状态）→ 不提交
        Vector3 localAtExit = LocalPointForPortal(toPlane, rb.position);
        float exitTrackDepth = noClipDepth + rbTriggerDepthExtension;
        if (localAtExit.z >= 0f && localAtExit.z <= exitTrackDepth && LocalPointInPortalRect(localAtExit, ResolvePortalShape(toPlane == portalPlaneA))) return false;

        // 镜像位姿：与 clone/TeleportRigidbodySebStyle 完全相同的数学（from→to + 经典半转）
        Vector3 mappedLocal = localPos;
        Quaternion mappedRot = Quaternion.Inverse(fromPlane.rotation) * rb.rotation;
        if (useClassicHalfTurn)
        {
            Quaternion halfTurn = LocalHalfTurn();
            mappedLocal = halfTurn * mappedLocal;
            mappedRot = halfTurn * mappedRot;
        }
        Vector3 targetPos = WorldPointFromPortal(toPlane, mappedLocal);
        Quaternion targetRot = toPlane.rotation * mappedRot;

        // 与 TeleportRigidbodySebStyle 同款双写：rb.position 是物理引擎内部值，
        // transform 不同步的话本帧渲染会看到旧位置。
        rb.position = targetPos;
        rb.rotation = targetRot;
        rb.transform.position = targetPos;
        rb.transform.rotation = targetRot;
        // 手持期间是kinematic，velocity无意义；清零避免释放瞬间带着脏速度飞出去。
        rb.velocity = Vector3.zero;
        rb.angularVelocity = Vector3.zero;
        return true;
    }

    private void DestroyRigidbodyClone(Rigidbody rb)
    {
        int idx = FindCloneIndexForRigidbody(rb);
        if (idx < 0) return;
        GameObject clone = cloneGameObjects[idx];
        if (clone != null)
        {
            // 先SetActive(false)让clone立刻从物理/渲染消失（Destroy是帧末延迟生效）
            clone.SetActive(false);
            Destroy(clone);
        }
        int last = cloneCount - 1;
        if (idx != last)
        {
            cloneOriginalRigidbodies[idx] = cloneOriginalRigidbodies[last];
            cloneGameObjects[idx] = cloneGameObjects[last];
            cloneTargetPortals[idx] = cloneTargetPortals[last];
            clonePendingActivation[idx] = clonePendingActivation[last];
            cloneMaterialSyncOriginalRenderers[idx] = cloneMaterialSyncOriginalRenderers[last];
            cloneMaterialSyncCloneRenderers[idx] = cloneMaterialSyncCloneRenderers[last];
        }
        cloneOriginalRigidbodies[last] = null;
        cloneGameObjects[last] = null;
        cloneTargetPortals[last] = null;
        clonePendingActivation[last] = false;
        cloneMaterialSyncOriginalRenderers[last] = null;
        cloneMaterialSyncCloneRenderers[last] = null;
        cloneCount = last;
    }

    private void DestroyAllRigidbodyClones()
    {
        for (int i = 0; i < cloneCount; i++)
        {
            GameObject clone = cloneGameObjects[i];
            if (clone != null)
            {
                clone.SetActive(false);
                Destroy(clone);
            }
            cloneOriginalRigidbodies[i] = null;
            cloneGameObjects[i] = null;
            cloneTargetPortals[i] = null;
            clonePendingActivation[i] = false;
            cloneMaterialSyncOriginalRenderers[i] = null;
            cloneMaterialSyncCloneRenderers[i] = null;
        }
        cloneCount = 0;
    }

    private void UpdateRigidbodyClonePoses()
    {
        if (!enableRigidbodyPortalClones) return;

        // 反向遍历：orphan 兜底销毁
        int i = cloneCount - 1;
        while (i >= 0)
        {
            Rigidbody rb = cloneOriginalRigidbodies[i];
            GameObject clone = cloneGameObjects[i];
            Transform fromPortal = cloneTargetPortals[i];
            bool orphan = false;
            if (rb == null || clone == null || fromPortal == null)
            {
                orphan = true;
            }
            else if (!IsRigidbodyTrackedByEitherPortal(rb))
            {
                orphan = true;
            }
            if (orphan)
            {
                if (clone != null)
                {
                    // 移除遮罩（简化处理：不还原原材质，依赖场景重置）
                    RemovePortalOverlayFromGameObject(clone);
                    clone.SetActive(false);
                    Destroy(clone);
                }
                int last = cloneCount - 1;
                if (i != last)
                {
                    cloneOriginalRigidbodies[i] = cloneOriginalRigidbodies[last];
                    cloneGameObjects[i] = cloneGameObjects[last];
                    cloneTargetPortals[i] = cloneTargetPortals[last];
                    clonePendingActivation[i] = clonePendingActivation[last];
                    cloneMaterialSyncOriginalRenderers[i] = cloneMaterialSyncOriginalRenderers[last];
                    cloneMaterialSyncCloneRenderers[i] = cloneMaterialSyncCloneRenderers[last];
                }
                cloneOriginalRigidbodies[last] = null;
                cloneGameObjects[last] = null;
                cloneTargetPortals[last] = null;
                clonePendingActivation[last] = false;
                cloneMaterialSyncOriginalRenderers[last] = null;
                cloneMaterialSyncCloneRenderers[last] = null;
                cloneCount = last;
                i--;
                continue;
            }
            i--;
        }

        if (cloneCount == 0) return;

        Quaternion halfTurn = LocalHalfTurn();

        for (int idx = 0; idx < cloneCount; idx++)
        {
            Rigidbody rb = cloneOriginalRigidbodies[idx];
            GameObject clone = cloneGameObjects[idx];
            Transform fromPortal = cloneTargetPortals[idx];
            if (rb == null || clone == null || fromPortal == null) continue;

            Transform toPortal = (fromPortal == portalPlaneA) ? portalPlaneB : portalPlaneA;
            if (toPortal == null) continue;

            Vector3 localPos = LocalPointForPortal(fromPortal, rb.position);
            Quaternion localRot = Quaternion.Inverse(fromPortal.rotation) * rb.rotation;
            if (useClassicHalfTurn)
            {
                localPos = halfTurn * localPos;
                localRot = halfTurn * localRot;
            }

            Vector3 worldPos = WorldPointFromPortal(toPortal, localPos);
            Quaternion worldRot = toPortal.rotation * localRot;

            clone.transform.position = worldPos;
            clone.transform.rotation = worldRot;

            // 材质实时跟随（含动画控制器驱动的变色等）：为什么需要每帧校正见 RepointCloneMaterials 注释。
            // 放在激活判定之前，保证clone首次可见时材质就已经是对的。
            RepointCloneMaterials(idx);

            bool firstFrame = clonePendingActivation[idx];
            if (firstFrame) clonePendingActivation[idx] = false;

            // 近距离裁剪
            float cullDist = Mathf.Abs(cloneNearCullDistance);
            float distSq = (worldPos - rb.position).sqrMagnitude;
            bool shouldBeVisible = distSq > cullDist * cullDist;

            if (firstFrame)
            {
                clone.SetActive(shouldBeVisible);
            }
            else
            {
                if (shouldBeVisible)
                {
                    if (!clone.activeSelf) clone.SetActive(true);
                }
                else
                {
                    if (clone.activeSelf) clone.SetActive(false);
                }
            }
        }
    }

    // 判断某GameObject是不是我们自己创建的clone
    private bool IsGameObjectOurClone(GameObject go)
    {
        if (go == null) return false;
        for (int i = 0; i < cloneCount; i++)
        {
            GameObject root = cloneGameObjects[i];
            if (root == null) continue;
            Transform t = go.transform;
            for (int d = 0; d < 16 && t != null; d++)
            {
                if (t.gameObject == root) return true;
                t = t.parent;
            }
        }
        return false;
    }

    // ============================================================
    // 刚体过门防CPU剔除：追踪中的源刚体临时扩Mesh.bounds+关dynamic occlusion，
    // 离开追踪时还原。避免传送门相机（oblique近裁+特殊视角）把整块刚体误cull。
    // 增量策略：Reconcile 每帧反向扫现有slot移除失追踪的、再扫A/B tracker补加新进入的；
    // 进入时只调用一次 GetComponentsInChildren，稳态零 GC 分配。
    // ============================================================

    private void RestoreAllRigidbodyCullingOverrides()
    {
        // 反向逐 slot 移除：RemoveCullingOverrideSlot 负责 bounds/occlusion 还原 + 尾部补位。
        // 反向遍历对 tail-compact 安全（移除 i 时只会用最后一项覆盖 i，不会影响 < i 的未处理项）。
        for (int i = rbCullingOverrideCount - 1; i >= 0; i--)
        {
            RemoveCullingOverrideSlot(i);
        }
    }

    private void ApplyCullingOverrideForRigidbody(Rigidbody rb)
    {
        if (rb == null) return;
        if (!expandRigidbodyTransitionSourceMeshBounds) return;
        GameObject go = rb.gameObject;
        if (go == null) return;

        float s = Mathf.Max(1f, rigidbodyTransitionExpandedBoundsSize);
        Vector3 expandedSize = new Vector3(s, s, s);

        // 注意：本函数只在 RB "新进入追踪" 时调用一次（增量 Reconcile），不每帧重跑。
        // 所以这里 GetComponentsInChildren + mf.mesh 实例化 + bounds 写入只发生一次，没有每帧 GC 开销。
        // 离开追踪时 Reconcile 会统一还原 bounds / occlusion，再清零 slot。
        Renderer[] renderers = go.GetComponentsInChildren<Renderer>(true);
        for (int i = 0; i < renderers.Length; i++)
        {
            Renderer r = renderers[i];
            if (r == null) continue;
            if (rbCullingOverrideCount >= MAX_RB_CULLING_OVERRIDES) return; // 上限，放弃剩余

            if (r.GetType() == typeof(MeshRenderer))
            {
                MeshRenderer mr = (MeshRenderer)r;
                MeshFilter mf = r.GetComponent<MeshFilter>();
                if (mf != null)
                {
                    Mesh m = mf.mesh; // 关键：.mesh 返回实例，不污染 sharedMesh/原资产
                    if (m != null)
                    {
                        int slot = rbCullingOverrideCount;
                        rbCullingOverrideRigidbodies[slot] = rb;
                        rbCullingOverrideMeshFilters[slot] = mf;
                        rbCullingOverrideOriginalMeshes[slot] = m;
                        rbCullingOverrideOriginalMeshBounds[slot] = m.bounds;
                        rbCullingOverrideMeshRenderers[slot] = mr;
                        rbCullingOverrideOriginalAllowOcclusion[slot] = mr.allowOcclusionWhenDynamic;
                        rbCullingOverrideSMRs[slot] = null;
                        m.bounds = new Bounds(Vector3.zero, expandedSize);
                        mr.allowOcclusionWhenDynamic = false;
                        rbCullingOverrideCount++;
                        continue;
                    }
                }
                // 没有 MeshFilter 或 mesh 为 null 的 MeshRenderer：只关 occlusion
                {
                    int slot2 = rbCullingOverrideCount;
                    rbCullingOverrideRigidbodies[slot2] = rb;
                    rbCullingOverrideMeshFilters[slot2] = null;
                    rbCullingOverrideOriginalMeshes[slot2] = null;
                    rbCullingOverrideMeshRenderers[slot2] = mr;
                    rbCullingOverrideOriginalAllowOcclusion[slot2] = mr.allowOcclusionWhenDynamic;
                    rbCullingOverrideSMRs[slot2] = null;
                    mr.allowOcclusionWhenDynamic = false;
                    rbCullingOverrideCount++;
                    continue;
                }
            }

            if (r.GetType() == typeof(SkinnedMeshRenderer))
            {
                SkinnedMeshRenderer smr = (SkinnedMeshRenderer)r;
                int slot = rbCullingOverrideCount;
                rbCullingOverrideRigidbodies[slot] = rb;
                rbCullingOverrideMeshFilters[slot] = null;
                rbCullingOverrideOriginalMeshes[slot] = null;
                rbCullingOverrideMeshRenderers[slot] = null;
                rbCullingOverrideSMRs[slot] = smr;
                rbCullingOverrideOriginalSMRBounds[slot] = smr.localBounds;
                rbCullingOverrideOriginalSMROffscreen[slot] = smr.updateWhenOffscreen;
                Bounds expanded = smr.localBounds;
                expanded.Expand(s);
                smr.localBounds = expanded;
                smr.updateWhenOffscreen = true;
                rbCullingOverrideCount++;
                continue;
            }

            // 其他 Renderer 子类型（Particle/Trail/Line）：不动。
        }
    }

    // 判断 RB 是否已经在 override 列表里（一个 RB 可能占多个 slot，因为子物体有多个 renderer）
    private bool IsRigidbodyCullingOverridden(Rigidbody rb)
    {
        for (int i = 0; i < rbCullingOverrideCount; i++)
        {
            if (rbCullingOverrideRigidbodies[i] == rb) return true;
        }
        return false;
    }

    // 从 override 列表里移除某个 slot（尾部补位，保持紧凑），同时还原该 slot 的 bounds/occlusion
    private void RemoveCullingOverrideSlot(int slot)
    {
        // 还原 Mesh bounds
        Mesh m = rbCullingOverrideOriginalMeshes[slot];
        if (m != null)
        {
            m.bounds = rbCullingOverrideOriginalMeshBounds[slot];
        }
        // 还原 MeshRenderer.allowOcclusionWhenDynamic
        MeshRenderer mr = rbCullingOverrideMeshRenderers[slot];
        if (mr != null)
        {
            mr.allowOcclusionWhenDynamic = rbCullingOverrideOriginalAllowOcclusion[slot];
        }
        // 还原 SkinnedMeshRenderer
        SkinnedMeshRenderer smr = rbCullingOverrideSMRs[slot];
        if (smr != null)
        {
            smr.localBounds = rbCullingOverrideOriginalSMRBounds[slot];
            smr.updateWhenOffscreen = rbCullingOverrideOriginalSMROffscreen[slot];
        }

        int last = rbCullingOverrideCount - 1;
        if (slot != last)
        {
            rbCullingOverrideRigidbodies[slot] = rbCullingOverrideRigidbodies[last];
            rbCullingOverrideMeshFilters[slot] = rbCullingOverrideMeshFilters[last];
            rbCullingOverrideOriginalMeshes[slot] = rbCullingOverrideOriginalMeshes[last];
            rbCullingOverrideOriginalMeshBounds[slot] = rbCullingOverrideOriginalMeshBounds[last];
            rbCullingOverrideMeshRenderers[slot] = rbCullingOverrideMeshRenderers[last];
            rbCullingOverrideOriginalAllowOcclusion[slot] = rbCullingOverrideOriginalAllowOcclusion[last];
            rbCullingOverrideSMRs[slot] = rbCullingOverrideSMRs[last];
            rbCullingOverrideOriginalSMRBounds[slot] = rbCullingOverrideOriginalSMRBounds[last];
            rbCullingOverrideOriginalSMROffscreen[slot] = rbCullingOverrideOriginalSMROffscreen[last];
        }
        rbCullingOverrideRigidbodies[last] = null;
        rbCullingOverrideMeshFilters[last] = null;
        rbCullingOverrideOriginalMeshes[last] = null;
        rbCullingOverrideOriginalMeshBounds[last] = default(Bounds);
        rbCullingOverrideMeshRenderers[last] = null;
        rbCullingOverrideOriginalAllowOcclusion[last] = false;
        rbCullingOverrideSMRs[last] = null;
        rbCullingOverrideOriginalSMRBounds[last] = default(Bounds);
        rbCullingOverrideOriginalSMROffscreen[last] = false;
        rbCullingOverrideCount = last;
    }

    private void ReconcileRigidbodyCullingOverrides()
    {
        if (!expandRigidbodyTransitionSourceMeshBounds)
        {
            // 开关被关：还原所有已应用的 override
            if (rbCullingOverrideCount > 0) RestoreAllRigidbodyCullingOverrides();
            return;
        }

        // 增量策略：
        // 1) 反向扫现有 override slot：如果 slot 所属 RB 已经不在 A/B 任何一侧追踪中，还原并移除。
        //    （反向遍历因为 RemoveCullingOverrideSlot 会用尾部补位，反向是安全的）
        for (int i = rbCullingOverrideCount - 1; i >= 0; i--)
        {
            Rigidbody rb = rbCullingOverrideRigidbodies[i];
            if (rb == null || !IsRigidbodyTrackedByEitherPortal(rb))
            {
                RemoveCullingOverrideSlot(i);
            }
        }

        // 2) 扫 A/B 当前追踪的 RB，对尚未 override 的 RB 应用一次（内部会自己判断上限）。
        for (int i = 0; i < trackedRBCountA; i++)
        {
            Rigidbody rb = trackedRigidbodiesA[i];
            if (rb != null && !IsRigidbodyCullingOverridden(rb))
            {
                ApplyCullingOverrideForRigidbody(rb);
            }
        }
        for (int i = 0; i < trackedRBCountB; i++)
        {
            Rigidbody rb = trackedRigidbodiesB[i];
            if (rb != null && !IsRigidbodyCullingOverridden(rb))
            {
                ApplyCullingOverrideForRigidbody(rb);
            }
        }
    }

    // 判断某刚体是否被A或B任意一侧门追踪中
    private bool IsRigidbodyTrackedByEitherPortal(Rigidbody rb)
    {
        if (rb == null) return false;
        for (int i = 0; i < trackedRBCountA; i++)
        {
            if (trackedRigidbodiesA[i] == rb) return true;
        }
        for (int i = 0; i < trackedRBCountB; i++)
        {
            if (trackedRigidbodiesB[i] == rb) return true;
        }
        return false;
    }

    private void RestoreRigidbodyCollisionIfSafe(Rigidbody rb, bool otherPortalIsA)
    {
        if (rb == null) return;
        if (IsRigidbodyTrackedByPortal(otherPortalIsA, rb)) return;
        RestoreRigidbodyPortalWallIgnoreForPortal(rb, !otherPortalIsA);
    }

    // Vector3 版本的 halfTurn：用于刚体速度/角速度映射（绕 Y 轴 180°：x,z 取反，y 不变）
    private Vector3 LocalHalfTurn(Vector3 v)
    {
        return new Vector3(-v.x, v.y, -v.z);
    }
}
