using UdonSharp;
using UnityEngine;
using VRC.SDKBase;
using VRC.Udon;

// ============================================================
// 合法放置检测 —— 设计说明（写给以后维护这段代码的人，包括未来的你自己）
// ============================================================
// 背景：墙壁/地板不是理想的90度直角回旋镖，而是几段互相穿模的"十字"结构拼出来的，
// 传送门贴上去时经常出现"贴到了穿模进来的另一段墙"或者"角落悬空在缝隙上方"的情况。
//
// 判定分两步，都在开枪那一帧内用纯物理查询完成，不放置任何真实的检测用 Cube 物体、
// 不依赖 Trigger 的 OnTriggerEnter/Exit（那个有至少一帧的物理延迟，会让"挪动->等结果->再挪动"
// 的重试循环变得很别扭）：
//
// 第一步 - 四角贴合校验（对应你构思里"背面4个角落cube"）：
//   在候选门框的左上/右上/左下/右下四个角（局部 XY 平面，留一点内缩边距）各发一条短射线，
//   起点比候选门面稍微往外(+forward)推一点，往回(-forward)射向墙面，测出"这个角的墙面
//   离候选门面平面有多远"。理想情况下四个角量出来的距离应该一致（墙是平的），我们把
//   偏差换算成一个"往哪边挪一点"的修正量，而不是瞎猜固定步长——这样对不规则墙面收敛更快、
//   也更不容易来回震荡。如果某个角完全没探测到东西，或者探测到的表面法线跟中心点命中的
//   法线差太多（说明八成是撞到了十字建筑穿模进来的另一段墙，不是这堵墙自己的延伸面），
//   直接按"这个角空的/不算数"处理，用一个很大的惩罚值把候选位置推离那个方向。
//   多次迭代后如果四个角都收敛到容差以内，判定贴合成功；迭代次数用完还没收敛，
//   或者中途需要挪动的总距离超过上限，判定不合法。
//
// 第二步 - 正面遮挡校验（对应你构思里"前面1个大薄cube"）：
//   贴合成功后，在最终门框位置前方做一次 OverlapBox 查询，盒子的 XY 尺寸永远按矩形
//   （portalTriggerWidth × portalTriggerHeight）来判定，不管这扇门视觉上是圆形还是三角形——
//   矩形范围内没有遮挡物，圆形/三角形（都是矩形的内切/内接子集）必然也没有遮挡物，
//   这样可以省掉一大堆形状相关的分支，判定逻辑更简单也更不容易出 bug。
//   查询结果里会主动排除"贴合用的那面墙自己"和"传送门本体这条 Transform 链路上的碰撞体"
//   （比如门框自带的实体装饰 Mesh Collider），否则每次放置都会被自己的门框"挡住"自己。
//
// 两步任意一步失败，都走现成的 OnShootFailed 失败反馈（失败音效/失败动画），
// 并且完全不移动传送门，保留它上一次合法的位置。
// ============================================================

[UdonBehaviourSyncMode(BehaviourSyncMode.None)]
public class 传送枪 : UdonSharpBehaviour
{
    [Header("════════════ 传送门 ════════════")]
    [Tooltip("传送门A大级")]
    public Transform portalA;
    
    [Tooltip("传送门B大级")]
    public Transform portalB;

    [Header("════════════ 射线设置 ════════════")]
    [Tooltip("最大射程")]
    public float maxDistance = 50f;

    [Tooltip("射线发射点（空物体，Z轴正方向为发射方向）")]
    public Transform shootPoint;

    [Tooltip("可放置的层（墙壁、地板等）")]
    public LayerMask placementLayers = -1;

    [Tooltip("阻挡层（击中时无法放置传送门）")]
    public LayerMask blockedLayers;

    [Tooltip("传送门离墙面的偏移距离")]
    public float wallOffset = 0.01f;

    [Header("════════════ 冷却设置 ════════════")]
    [Tooltip("发射冷却时间（秒）")]
    public float cooldownTime = 0.5f;
    
    [Tooltip("冷却期间是否播放失败音效。默认关闭：冷却中 Update() 会直接跳过所有输入检测（包括合法放置校验），\n不会调用 TryShootPortal，自然也不会有任何检测失败音效反复触发的问题。\n开启此项仅用于\"我就是想让冷却中的操作也给个提示音\"这种特殊需求，会额外调用一次 PlayFailSound。")]
    public bool playSoundOnCooldown = false;

    [Header("════════════ 动画控制器 ════════════")]
    [Tooltip("枪械动画控制器")]
    public Animator gunAnimator;
    
    [Tooltip("发射A门的Trigger名称")]
    public string shootTriggerA = "ShootA";
    
    [Tooltip("发射B门的Trigger名称")]
    public string shootTriggerB = "ShootB";
    
    [Tooltip("发射失败的Trigger名称")]
    public string shootTriggerFail = "ShootFail";

    [Header("════════════ 音效（可选）════════════")]
    public AudioSource audioSource;
    
    [Tooltip("A门随机音效数组")]
    public AudioClip[] shootSoundsA;
    
    [Tooltip("B门随机音效数组")]
    public AudioClip[] shootSoundsB;
    
    [Tooltip("发射失败音效数组")]
    public AudioClip[] failSounds;

    [Header("════════════ 标记的碰撞体 ════════════")]
    [Tooltip("传送门A射中的碰撞体（自动记录）")]
    public Collider markedColliderA;

    [Tooltip("传送门B射中的碰撞体（自动记录）")]
    public Collider markedColliderB;

    [Header("════════════ 调试 ════════════")]
    public bool showDebugRay = true;
    public Color rayColorA = Color.red;
    public Color rayColorB = Color.blue;
    public Color rayColorFail = Color.gray;

    [Tooltip("旧模式：让传送枪给B门本体额外旋转180度。新经典映射模式必须关闭，否则地板/天花板B门会被翻到背面，导致同门进出。")]
    public bool applyBHalfTurnInGun = false;

    [Tooltip("放置传送门时输出极简日志：按钮A/B、实际移动哪个Transform、是否给B额外旋转180、最终forward/up。")]
    public bool debugPortalGunLog = true;

    [Tooltip("世界启动时输出一次本地玩家物理参数：gravity/jump/walk/run/strafe。")]
    public bool debugPlayerPhysicsOnStart = true;

    [Header("════════════ 手持图层切换 ════════════")]
    [Tooltip("手持时是否临时把传送枪自身 GameObject 的 Layer 切换到 heldLayer，松开后自动还原为拾取前的原始 Layer。\n用途：避免手持枪身时和玩家胶囊体/场景环境发生不必要的物理碰撞卡顿。")]
    public bool switchLayerWhenHeld = true;

    [Tooltip("手持时临时切换到的 Layer。VRChat 默认工程里 13 = Pickup。")]
    public int heldLayer = 13;

    [Header("════════════ 合法放置检测 ════════════")]
    [Tooltip("开启后，放置传送门时会做四角贴合校验 + 正面遮挡校验。不合法则放置失败（走失败音效/动画），传送门保留在上一次的合法位置。")]
    public bool enablePlacementValidation = true;

    [Tooltip("双向传送门管理器引用。用于读取门框真实宽高(portalTriggerWidth/portalTriggerHeight)，保证放置校验用的尺寸和实际传送判定/可视范围完全一致，不需要重复配置一遍。")]
    public 双向传送门管理器 portalManager;

    [Tooltip("四角贴合校验：门框宽高各自向内收缩的比例(0~0.49)。避免四个检测角恰好卡在墙体边缘、几何精度误差导致的抖动判定。0.05代表向内收缩5%。")]
    [Range(0f, 0.49f)]
    public float placementCornerInsetRatio = 0.08f;

    [Tooltip("四角贴合校验：允许的最大间隙误差。四个角与墙面的距离偏差都小于这个值才算贴合成功。")]
    public float placementGapTolerance = 0.02f;

    [Tooltip("四角贴合校验：单次最大纠偏迭代次数。次数越多越能贴合不规则墙面，但每多一次都会多发4条射线，超过还没收敛就判定放置失败。\n采用二分搜索式步长：每次只按“哪一侧偏差更大”决定移动方向，跨过头(方向反转)就自动把步长减半精细收敛，不需要靠增大迭代次数来提高精度。")]
    public int placementMaxIterations = 14;

    [Tooltip("四角贴合校验：初始步长（沿墙面局部XY方向，每次迭代的起始移动距离）。需要大于最坏情况下的墙体凹凸/接缝落差，否则可能跨不过一个真实存在的台阶；后续迭代中一旦移动方向反转会自动减半，不用手动调小。")]
    public float placementInitialStep = 0.3f;

    [Tooltip("四角贴合校验：步长可以减半到的最小值。低于这个值就提前停止迭代（此时通常已经收敛或陷入两侧打平的死局）。")]
    public float placementMinStep = 0.0005f;

    [Tooltip("四角贴合校验：候选门框累计挪动的总距离上限。超过这个值还没贴合成功就直接判定失败，避免在穿模复杂几何体上无止境地来回横跳。")]
    public float placementMaxTotalCorrection = 2f;

    [Tooltip("四角贴合校验：角点探测射线的起点相对候选门面往外(+法线方向)推出的距离，也是这条射线的最大探测深度上限的基准。需要大于最坏情况下的贴合误差，又不能大到穿过墙体测到背面。")]
    public float placementProbeOutDistance = 0.15f;

    [Tooltip("四角贴合校验：允许探测射线命中面的法线，与中心命中法线之间的最大夹角(度)。超过这个角度就认为大概率贴到了十字建筑穿模进来的另一段墙，按“该角没贴合”处理。")]
    public float placementMaxNormalAngle = 35f;

    [Tooltip("正面遮挡校验：检测盒子的厚度(沿门法线方向)。太薄可能穿过薄障碍物检测不到，太厚可能把贴合的墙面本身也算进遮挡。")]
    public float placementObstructionDepth = 0.12f;

    [Tooltip("正面遮挡校验：检测盒子中心相对门面沿法线方向的偏移。默认让盒子略微跨在门面前方，可按 wallOffset 微调。")]
    public float placementObstructionOffset = 0.06f;

    [Tooltip("正面遮挡校验专用层：只有这些层上的物体才会被当成\"挡住门口的障碍物\"。默认全部层(-1)最安全；如果场景里有不该算遮挡的特殊层（比如水面/特效），可以在这里排除。这个层独立于 placementLayers(可放置层)，两者用途不同。")]
    public LayerMask placementObstructionLayers = ~0;

    [Tooltip("A/B互斥校验：不管两扇门有没有挂碰撞体、碰撞体是不是Trigger、图层设置是否正确，都强制保证A、B两扇门不会互相重叠。\n原理：把每扇门近似看成一个包围球(半径按门框对角线的一半算)，候选位置离另一扇门当前位置的距离必须大于两个半径之和(再加下面的安全余量)，否则直接判定不合法。\n这是一道独立于射线/碰撞体检测之外的硬性兜底，专门防止\"A把自己放到了B的位置上\"这类两门重叠的bug，不建议关闭。")]
    public bool enableMutualExclusionCheck = true;

    [Tooltip("A/B互斥校验：在两个包围球半径之和的基础上，再额外增加的安全间距。避免两扇门贴得太近导致视觉穿插、递归渲染画面互相干扰。")]
    public float mutualExclusionMargin = 0.1f;

    [Tooltip("放置校验专用日志：贴合迭代过程、遮挡命中的物体名字。排查“为什么这里放不了传送门”时开启。")]
    public bool debugPlacementValidationLog = false;

    [Header("════════════ 刚体抓取（Portal 原著复刻）════════════")]
    [Tooltip("抓取刚体时的随机音效数组（参考 shootSounds 逻辑）")]
    public AudioClip[] grabSounds;
    [Tooltip("抓取刚体时要设置的 Animator bool 参数名（可自定义，默认 IsGrabbing）")]
    public string grabAnimatorBoolName = "IsGrabbing";
    [Tooltip("抓取射线最大距离")]
    public float grabMaxDistance = 8f;
    [Tooltip("抓取时临时把刚体质量设为这个值（便于操控重物），释放时还原")]
    public float heldMassWhileGrabbed = 1f;
    [Tooltip("抓取刚体后是否自动切换其 Layer 到 heldLayer（25 穿透层）")]
    public bool switchRBLayersWhenGrabbed = true;

    private int originalGunLayerBeforeHeld = -1;
    private bool gunLayerOverrideActive = false;

    private VRCPlayerApi localPlayer;
    private bool isHeld = false;
    private float cooldownTimer = 0f;
    private bool vrTriggerLeftPressed = false;
    private bool vrTriggerRightPressed = false;

    // 冷却期间桌面端滚轮的"已触发过一次"锁存：滚轮是连续轴而不是按键，Input.GetAxis("Mouse ScrollWheel")
    // 在冷却期间会持续给出非零值（尤其是高分辨率滚轮/触控板惯性），如果只判断 scroll>0/<0 会导致冷却结束的
    // 瞬间同一次物理滚动被重复识别成好几次开火。用这两个锁存位做"边缘触发"：必须先回到零位（松手）才能再次触发。
    private bool scrollUpLatched = false;
    private bool scrollDownLatched = false;

    // 刚体抓取状态
    private Rigidbody heldRigidbody;
    private float originalHeldMass = 1f;
    private int originalRBLayer = -1;
    private bool rbLayerOverrideActive = false;
    private Vector3 heldLocalOffset;
    private Quaternion heldLocalRotationOffset;
    private bool isGrabbing = false;

    void Start()
    {
        localPlayer = Networking.LocalPlayer;
        if (debugPlayerPhysicsOnStart && localPlayer != null && localPlayer.IsValid())
        {
            Debug.Log(
                "[玩家物理] gravity=" + localPlayer.GetGravityStrength() +
                " jump=" + localPlayer.GetJumpImpulse() +
                " walk=" + localPlayer.GetWalkSpeed() +
                " run=" + localPlayer.GetRunSpeed() +
                " strafe=" + localPlayer.GetStrafeSpeed()
            );
        }
    }

    public override void OnPickup()
    {
        isHeld = true;

        if (switchLayerWhenHeld && !gunLayerOverrideActive)
        {
            originalGunLayerBeforeHeld = gameObject.layer;
            gameObject.layer = heldLayer;
            gunLayerOverrideActive = true;
        }
    }

    public override void OnDrop()
    {
        isHeld = false;

        if (gunLayerOverrideActive)
        {
            gameObject.layer = originalGunLayerBeforeHeld;
            gunLayerOverrideActive = false;
        }

        // 释放抓取的刚体
        ReleaseHeldRigidbody();
    }

    // ============================================================
    // Pickup Use 事件（VRChat 官方推荐交互方式）
    // 优点：不会被设置界面、Esc菜单、聊天框等 UI 拦截
    // PC 和 VR 都能正常工作
    // ============================================================

    public override void OnPickupUseDown()
    {
        if (heldRigidbody != null)
        {
            // 已有抓取物体 → 释放
            ReleaseHeldRigidbody();
        }
        else
        {
            // 没有抓取 → 尝试抓取
            TryGrabRigidbody();
        }
    }

    public override void OnPickupUseUp()
    {
        // 可选：松开 Use 键时的逻辑（目前留空）
    }

    // ============================================================
    // 刚体抓取核心（Kinematic + 质量临时修改 + Layer 切换）
    // ============================================================

    void TryGrabRigidbody()
    {
        if (heldRigidbody != null) return;
        if (shootPoint == null) return;

        RaycastHit hit;
        if (Physics.Raycast(shootPoint.position, shootPoint.forward, out hit, grabMaxDistance))
        {
            Collider col = hit.collider;
            if (col == null) return;

            // 使用 attachedRigidbody 比 GetComponentInParent 更稳定（Udon 推荐）
            Rigidbody rb = col.attachedRigidbody;
            if (rb == null || rb.isKinematic) return;

            GrabRigidbody(rb);
        }
    }

    void GrabRigidbody(Rigidbody rb)
    {
        if (rb == null || shootPoint == null) return;

        heldRigidbody = rb;

        // 保存并临时修改质量
        originalHeldMass = rb.mass;
        rb.mass = heldMassWhileGrabbed;

        // Kinematic 模式（最稳定，原著手感）
        rb.isKinematic = true;

        // 【Portal 原著严格手感】
        // 相对位置固定为 shootPoint 本地 (0,0,1)
        Vector3 desiredLocalOffset = new Vector3(0f, 0f, 1f);
        Vector3 worldGrabPoint = shootPoint.TransformPoint(desiredLocalOffset);
        heldLocalOffset = rb.transform.InverseTransformPoint(worldGrabPoint);

        // 旋转严格跟随 shootPoint
        heldLocalRotationOffset = Quaternion.Inverse(rb.transform.rotation) * shootPoint.rotation;

        // Layer 切换
        if (switchRBLayersWhenGrabbed)
        {
            originalRBLayer = rb.gameObject.layer;
            rb.gameObject.layer = heldLayer;
            rbLayerOverrideActive = true;
        }

        isGrabbing = true;
        SetGrabAnimator(true);
        PlayGrabSound();

        if (debugPortalGunLog)
        {
            Debug.Log("[传送枪] 抓取刚体: " + rb.name);
        }
    }

    void ReleaseHeldRigidbody()
    {
        if (heldRigidbody == null) return;

        Rigidbody rb = heldRigidbody;
        heldRigidbody = null;

        // 还原质量
        rb.mass = originalHeldMass;

        // 还原 Kinematic（允许物理）
        rb.isKinematic = false;

        // 还原 Layer
        if (rbLayerOverrideActive && originalRBLayer != -1)
        {
            rb.gameObject.layer = originalRBLayer;
            rbLayerOverrideActive = false;
        }

        isGrabbing = false;
        SetGrabAnimator(false);

        if (debugPortalGunLog)
        {
            Debug.Log("[传送枪] 释放刚体: " + rb.name);
        }
    }

    void SetGrabAnimator(bool grabbing)
    {
        if (gunAnimator != null && !string.IsNullOrEmpty(grabAnimatorBoolName))
        {
            gunAnimator.SetBool(grabAnimatorBoolName, grabbing);
        }
    }

    void PlayGrabSound()
    {
        if (audioSource != null && grabSounds != null && grabSounds.Length > 0)
        {
            int idx = Random.Range(0, grabSounds.Length);
            if (grabSounds[idx] != null)
            {
                audioSource.PlayOneShot(grabSounds[idx]);
            }
        }
    }

    // 供管理器调用：无缝跨门保持抓取
    public void UpdateHeldAfterTeleport(Vector3 newWorldPos, Quaternion newWorldRot)
    {
        if (heldRigidbody == null) return;

        // 重新计算偏移（基于新位置）
        heldLocalOffset = heldRigidbody.transform.InverseTransformPoint(newWorldPos);
        heldLocalRotationOffset = Quaternion.Inverse(heldRigidbody.transform.rotation) * newWorldRot;
    }

    public Rigidbody GetHeldRigidbody()
    {
        return heldRigidbody;
    }

    void Update()
    {
        if (!isHeld) return;
        if (localPlayer == null || !localPlayer.IsValid()) return;

        if (cooldownTimer > 0)
        {
            cooldownTimer -= Time.deltaTime;
        }

        if (!localPlayer.IsUserInVR())
        {
            // 抓取逻辑已迁移到 OnPickupUseDown / OnPickupUseUp（Pickup Use 事件）
            // 这样不会被设置界面、Esc菜单、UI 拦截，更符合 VRChat 官方推荐做法

            // 滚轮是连续轴而不是按键，一次物理滚动可能横跨好几帧、且数值会有拖尾，
            // 必须做"边缘触发"锁存：触发一次后必须先回到接近零位，才允许下一次触发。
            // 否则冷却期间/冷却刚结束的瞬间，同一次滚动会被识别成好几次开火，
            // 每次都跑一遍合法放置检测、失败了还都各自触发一次失败反馈，听起来就是连续吵人的失败音效。
            float scroll = Input.GetAxis("Mouse ScrollWheel");
            const float scrollDeadzone = 0.01f;

            if (scroll > scrollDeadzone)
            {
                if (!scrollUpLatched)
                {
                    scrollUpLatched = true;
                    TryShootPortal(true);
                }
            }
            else if (scroll < -scrollDeadzone)
            {
                if (!scrollDownLatched)
                {
                    scrollDownLatched = true;
                    TryShootPortal(false);
                }
            }
            else
            {
                // 回到死区内才解锁，允许下一次滚动触发。
                // 注意：这里不额外判断 cooldownTimer——是否真正触发检测逻辑完全交给 TryShootPortal
                // 开头的冷却检查决定（那里本来就会在冷却中直接 return，不跑任何射线/贴合校验）。
                // 这个锁存只负责"同一次连续滚动手势最多算一次尝试"，避免长手势跨越冷却结束点后
                // 被重复识别成好几次独立开火，这才是"反复触发、音效吵"的真正根因。
                scrollUpLatched = false;
                scrollDownLatched = false;
            }
        }
        else
        {
            if (Input.GetAxisRaw("Oculus_CrossPlatform_PrimaryIndexTrigger") > 0.7f && !vrTriggerLeftPressed)
            {
                vrTriggerLeftPressed = true;
                TryShootPortal(true);
            }

            // VR 左手 Grip 抓取保留作为兜底（主要使用 Pickup Use）
            else if (Input.GetAxisRaw("Oculus_CrossPlatform_PrimaryIndexTrigger") < 0.3f)
            {
                vrTriggerLeftPressed = false;
            }
            
            if (Input.GetAxisRaw("Oculus_CrossPlatform_SecondaryIndexTrigger") > 0.7f && !vrTriggerRightPressed)
            {
                vrTriggerRightPressed = true;
                TryShootPortal(false);
            }
            else if (Input.GetAxisRaw("Oculus_CrossPlatform_SecondaryIndexTrigger") < 0.3f)
            {
                vrTriggerRightPressed = false;
            }
        }

        // 更新抓取的刚体（Portal 原著手感：直接跟随 shootPoint 正前方 1 米）
        if (heldRigidbody != null && isHeld)
        {
            // 更稳定、更符合原著的写法：直接用 shootPoint 的世界坐标 + forward * 1
            Vector3 targetPos = shootPoint.position + shootPoint.forward * 1f;
            Quaternion targetRot = shootPoint.rotation;

            heldRigidbody.MovePosition(targetPos);
            heldRigidbody.MoveRotation(targetRot);
        }

        // 安全兜底
        if (heldRigidbody == null && isGrabbing)
        {
            isGrabbing = false;
            SetGrabAnimator(false);
        }
    }

    void TryShootPortal(bool isPortalA)
    {
        if (cooldownTimer > 0)
        {
            if (playSoundOnCooldown)
            {
                PlayFailSound();
            }
            return;
        }

        Transform portal = isPortalA ? portalA : portalB;
        if (portal == null) return;
        if (shootPoint == null) return;

        Vector3 rayOrigin = shootPoint.position;
        Vector3 rayDirection = shootPoint.forward;

        LayerMask combinedLayers = placementLayers | blockedLayers;

        // Portal 原作手感：传送门本体不是实体障碍物，瞄准射线应该直接穿过"当前正在放置的这一扇门"
        // 自身的碰撞体（含子物体，比如门框装饰用的 Mesh Collider），命中它背后真正的墙面，
        // 这样贴着自己的门站着也能重新对准墙面微调位置，而不会先打在自己门框上就判定失败。
        //
        // 注意：这里只忽略"自己"这一扇门(portal = isPortalA ? portalA : portalB)，
        // 绝不能把另一扇门也一起忽略——A、B 两扇门必须互相视为实体障碍物，
        // 否则瞄A门时激光会直接穿过B门命中B门背后的墙，导致"A把自己放到了B的位置上"这种
        // 两门重叠的bug（这是本轮修复的一个真实回归问题，改这段代码前一定要意识到这一点）。
        // 用 RaycastAll 拿到射线路径上的全部命中，只过滤掉属于"自己"这条 Transform 链路下的
        // 碰撞体，再从剩下的里面手动找最近的一个，等价于"这一扇门对射线不可见，但另一扇门可见"。
        RaycastHit[] allHits = Physics.RaycastAll(rayOrigin, rayDirection, maxDistance, combinedLayers);
        RaycastHit hit = default(RaycastHit);
        bool foundHit = false;
        float closestDistance = float.MaxValue;

        for (int i = 0; i < allHits.Length; i++)
        {
            RaycastHit candidate = allHits[i];
            if (candidate.collider == null) continue;

            Transform hitTransform = candidate.collider.transform;
            if (portal != null && (hitTransform == portal || hitTransform.IsChildOf(portal))) continue;

            if (candidate.distance < closestDistance)
            {
                closestDistance = candidate.distance;
                hit = candidate;
                foundHit = true;
            }
        }

        if (foundHit)
        {
            int hitLayer = hit.collider.gameObject.layer;
            
            if (((1 << hitLayer) & blockedLayers) != 0)
            {
                OnShootFailed(rayOrigin, hit.point);
                return;
            }
            
            VRCPlayerApi.TrackingData headData = localPlayer.GetTrackingData(VRCPlayerApi.TrackingDataType.Head);
            
            Vector3 portalPos = hit.point + hit.normal * wallOffset;
            Quaternion portalRot = Quaternion.LookRotation(hit.normal, Vector3.up);
            
            if (Mathf.Abs(hit.normal.y) > 0.9f)
            {
                Vector3 playerForward = headData.rotation * Vector3.forward;
                playerForward.y = 0;
                playerForward.Normalize();
                
                if (hit.normal.y > 0)
                {
                    portalRot = Quaternion.LookRotation(hit.normal, playerForward);
                }
                else
                {
                    portalRot = Quaternion.LookRotation(hit.normal, -playerForward);
                }
            }
            
            bool appliedBHalfTurn = false;
            if (!isPortalA && applyBHalfTurnInGun)
            {
                portalRot = portalRot * Quaternion.Euler(0, 180f, 0);
                appliedBHalfTurn = true;
            }

            // A/B互斥校验：独立于下面的 enablePlacementValidation 开关，永远保证A、B两扇门不会互相重叠。
            // 这是基本正确性要求（"A放到A自己身上、B放到B自己身上"合法，"A放到B身上或反之"永远不合法），
            // 不是可选的美观校验，所以哪怕用户为了性能关掉了贴合/遮挡校验，这道检查依然生效。
            if (enableMutualExclusionCheck)
            {
                Transform otherPortal = isPortalA ? portalB : portalA;
                string mutualExclusionFailReason;
                bool mutualExclusionOk = CheckMutualExclusion(portalPos, otherPortal, out mutualExclusionFailReason);
                if (!mutualExclusionOk)
                {
                    if (debugPlacementValidationLog)
                    {
                        Debug.Log("[传送枪][放置校验] " + mutualExclusionFailReason);
                    }
                    OnShootFailed(rayOrigin, hit.point);
                    return;
                }
            }

            if (enablePlacementValidation)
            {
                string placementFailReason;
                bool placementOk = ValidateAndCorrectPlacement(ref portalPos, portalRot, hit, out placementFailReason);
                if (!placementOk)
                {
                    if (debugPlacementValidationLog)
                    {
                        Debug.Log("[传送枪][放置校验] 贴合失败：" + placementFailReason);
                    }
                    OnShootFailed(rayOrigin, hit.point);
                    return;
                }

                string obstructionFailReason;
                string blockerName;
                bool obstructionOk = CheckFrontObstruction(portalPos, portalRot, hit.collider, portal, out obstructionFailReason, out blockerName);
                if (!obstructionOk)
                {
                    if (debugPlacementValidationLog)
                    {
                        Debug.Log("[传送枪][放置校验] 遮挡失败：" + obstructionFailReason + " 挡住的物体=" + blockerName);
                    }
                    OnShootFailed(rayOrigin, hit.point);
                    return;
                }

                // 贴合校验可能会在墙面平面内滑动候选位置(ValidateAndCorrectPlacement 里的纠偏)，
                // 滑动之后必须重新确认一次互斥距离仍然满足——理论上滑动幅度通常很小，
                // 但如果两扇门本来就贴得很近，纠偏有可能把候选位置滑向另一扇门，这里做二次兜底。
                if (enableMutualExclusionCheck)
                {
                    Transform otherPortal = isPortalA ? portalB : portalA;
                    string mutualExclusionFailReason2;
                    bool mutualExclusionOk2 = CheckMutualExclusion(portalPos, otherPortal, out mutualExclusionFailReason2);
                    if (!mutualExclusionOk2)
                    {
                        if (debugPlacementValidationLog)
                        {
                            Debug.Log("[传送枪][放置校验] 贴合纠偏后二次检查：" + mutualExclusionFailReason2);
                        }
                        OnShootFailed(rayOrigin, hit.point);
                        return;
                    }
                }
            }

            portal.position = portalPos;
            portal.rotation = portalRot;

            if (debugPortalGunLog)
            {
                Debug.Log(
                    "[传送枪] " + (isPortalA ? "按钮A" : "按钮B") +
                    " -> " + portal.name +
                    " half180=" + appliedBHalfTurn +
                    " hitN=" + hit.normal +
                    " pos=" + portal.position +
                    " rot=" + portal.rotation.eulerAngles +
                    " fwd=" + portal.forward +
                    " up=" + portal.up +
                    " col=" + hit.collider.name +
                    " layer=" + hit.collider.gameObject.layer
                );
            }

            // 记录打中的碰撞体
            if (isPortalA)
            {
                markedColliderA = hit.collider;
            }
            else
            {
                markedColliderB = hit.collider;
            }

            cooldownTimer = cooldownTime;

            Color debugColor = isPortalA ? rayColorA : rayColorB;
            AudioClip[] sounds = isPortalA ? shootSoundsA : shootSoundsB;
            string triggerName = isPortalA ? shootTriggerA : shootTriggerB;

            if (audioSource != null && sounds != null && sounds.Length > 0)
            {
                int randomIndex = Random.Range(0, sounds.Length);
                if (sounds[randomIndex] != null)
                {
                    audioSource.PlayOneShot(sounds[randomIndex]);
                }
            }

            if (gunAnimator != null && !string.IsNullOrEmpty(triggerName))
            {
                gunAnimator.SetTrigger(triggerName);
            }

            if (showDebugRay)
            {
                Debug.DrawLine(rayOrigin, hit.point, debugColor, 1f);
            }
        }
        else
        {
            OnShootFailed(rayOrigin, rayOrigin + rayDirection * maxDistance);
        }
    }

    // ============================================================
    // 合法放置检测 —— 实现
    // ============================================================

    /// 主入口：对候选门框位置做四角贴合校验 + 正面遮挡校验。
    /// 校验通过时会原地修正 portalPos（沿墙面局部XY平面滑动，不改 portalRot，不产生法线方向位移）。
    /// 校验失败时 portalPos 保持传入时的值不变（调用方失败分支不会使用它，但保持"不做多余修改"的干净语义）。
    bool ValidateAndCorrectPlacement(ref Vector3 portalPos, Quaternion portalRot, RaycastHit centerHit, out string failReason)
    {
        failReason = "";

        float halfWidth = 1f;
        float halfHeight = 1f;
        if (portalManager != null)
        {
            halfWidth = Mathf.Max(0.01f, portalManager.portalTriggerWidth * 0.5f);
            halfHeight = Mathf.Max(0.01f, portalManager.portalTriggerHeight * 0.5f);
        }

        float insetX = halfWidth * (1f - placementCornerInsetRatio * 2f);
        float insetY = halfHeight * (1f - placementCornerInsetRatio * 2f);

        Vector3 right = portalRot * Vector3.right;
        Vector3 up = portalRot * Vector3.up;
        Vector3 forward = portalRot * Vector3.forward;

        // 四个角在门局部 XY 平面上的偏移（左上/右上/左下/右下）。
        Vector2[] cornerOffsets = new Vector2[]
        {
            new Vector2(-insetX, insetY),
            new Vector2(insetX, insetY),
            new Vector2(-insetX, -insetY),
            new Vector2(insetX, -insetY)
        };

        // portalPos 本身沿法线方向已经推出了 wallOffset 的距离（贴门时留出的间隙），
        // 所以四个角贴合成功的“理想间隙”不是 0，而是 wallOffset。下面所有判定/修正都以此为基准。
        float targetGap = wallOffset;
        float maxRayDistance = placementProbeOutDistance + Mathf.Abs(targetGap) + placementInitialStep * 2f + placementGapTolerance + 0.05f;

        // 找不到东西 / 法线不匹配时，把这个角当成“探测范围内完全没有合格墙面”来处理，
        // 用一个明显大于任何正常 badness 的哨兵值，保证纠偏时会被优先当作最差的一侧远离它。
        float invalidBadnessSentinel = maxRayDistance + Mathf.Abs(targetGap) + 1f;

        float totalCorrection = 0f;

        // 二分搜索式步长：up/right 两个方向独立维护自己的步长和上一次移动方向。
        // 每次迭代只用"哪一侧偏差(badness)更大"的比较结果决定这一步往哪个方向挪，而不是把 badness
        // 数值直接当位移量——这样即使墙面缺陷是陡峭的台阶（十字穿模建筑的接缝大概率是这种硬边缘），
        // 也能用大步长快速跨过去；一旦某个方向上移动方向反转（说明跨过了理想点），就把该方向的步长
        // 减半，从而精细收敛到容差以内，不需要靠堆迭代次数来提高精度。
        float stepUp = placementInitialStep;
        float stepRight = placementInitialStep;
        int lastDirUp = 0;
        int lastDirRight = 0;

        for (int iteration = 0; iteration < placementMaxIterations; iteration++)
        {
            float[] gaps = new float[4];      // 每个角实际测到的“候选门面到墙面”的间隙（沿法线方向，正值=有空隙，负值=已嵌入）
            float[] badness = new float[4];   // 每个角与理想间隙(targetGap)的偏差绝对值，纠偏和合法性判定都基于这个量
            bool[] cornerValid = new bool[4];

            for (int i = 0; i < 4; i++)
            {
                Vector3 cornerCenter = portalPos + right * cornerOffsets[i].x + up * cornerOffsets[i].y;
                Vector3 probeStart = cornerCenter + forward * placementProbeOutDistance;

                RaycastHit cornerHit;
                bool didHit = Physics.Raycast(
                    probeStart,
                    -forward,
                    out cornerHit,
                    maxRayDistance,
                    placementLayers
                );

                if (!didHit)
                {
                    // 探测范围内完全没有墙面：这个角悬空。
                    gaps[i] = maxRayDistance - placementProbeOutDistance;
                    badness[i] = invalidBadnessSentinel;
                    cornerValid[i] = false;
                    continue;
                }

                float normalAngle = Vector3.Angle(cornerHit.normal, centerHit.normal);
                if (normalAngle > placementMaxNormalAngle)
                {
                    // 法线差异过大：大概率贴到了穿模进来的另一段墙/其他几何体，不是这堵墙自己的延伸面。
                    gaps[i] = cornerHit.distance - placementProbeOutDistance;
                    badness[i] = invalidBadnessSentinel;
                    cornerValid[i] = false;
                    continue;
                }

                // gap：候选门面到这个角实际墙面的间隙，沿门的法线方向测量。
                // 正常贴合时应该约等于 targetGap(=wallOffset)；偏大=这个角有多余空隙(悬空)；偏小甚至为负=这个角嵌入了墙里(被凸起顶住)。
                gaps[i] = cornerHit.distance - placementProbeOutDistance;
                badness[i] = Mathf.Abs(gaps[i] - targetGap);
                cornerValid[i] = true;
            }

            bool allWithinTolerance = true;
            bool anyInvalid = false;
            for (int i = 0; i < 4; i++)
            {
                if (!cornerValid[i]) anyInvalid = true;
                if (badness[i] > placementGapTolerance) allWithinTolerance = false;
            }

            if (debugPlacementValidationLog)
            {
                Debug.Log(
                    "[传送枪][贴合迭代" + iteration + "] pos=" + portalPos.ToString() +
                    " gaps=(" + gaps[0].ToString("F4") + "," + gaps[1].ToString("F4") + "," +
                    gaps[2].ToString("F4") + "," + gaps[3].ToString("F4") + ")" +
                    " badness=(" + badness[0].ToString("F4") + "," + badness[1].ToString("F4") + "," +
                    badness[2].ToString("F4") + "," + badness[3].ToString("F4") + ")" +
                    " valid=(" + cornerValid[0] + "," + cornerValid[1] + "," + cornerValid[2] + "," + cornerValid[3] + ")" +
                    " stepUp=" + stepUp.ToString("F5") + " stepRight=" + stepRight.ToString("F5")
                );
            }

            if (allWithinTolerance && !anyInvalid)
            {
                return true;
            }

            // 纠偏方向统一原则：哪一侧（上/下/左/右）偏差更大（badness 更高），就往那一侧的反方向挪，
            // 不管偏差的成因是“悬空”还是“嵌入”——两种情况都应该远离问题更严重的一侧，靠近问题较轻的一侧。
            // 这样比直接对有符号间隙做差更稳：有符号间隙在“悬空”和“嵌入”两种场景下会给出相反的错误方向。
            float topBadness = (badness[0] + badness[1]) * 0.5f;
            float bottomBadness = (badness[2] + badness[3]) * 0.5f;
            float leftBadness = (badness[0] + badness[2]) * 0.5f;
            float rightBadness = (badness[1] + badness[3]) * 0.5f;

            float diffUpDown = bottomBadness - topBadness;
            float diffLeftRight = leftBadness - rightBadness;

            const float dirDeadzone = 0.0001f;
            int dirUp = diffUpDown > dirDeadzone ? 1 : (diffUpDown < -dirDeadzone ? -1 : 0);
            int dirRight = diffLeftRight > dirDeadzone ? 1 : (diffLeftRight < -dirDeadzone ? -1 : 0);

            if (dirUp == 0 && dirRight == 0)
            {
                // 上下两侧、左右两侧的偏差都恰好打平，但仍未达标：说明这个位置已经是局部最优，
                // 再怎么在这个平面内滑动也无法让四角同时贴合，判定为不合法的墙面几何形状。
                failReason = "四角贴合校验失败：偏差在两侧打平但仍超差，可能是不平整/扭曲的墙面";
                return false;
            }

            if (lastDirUp != 0 && dirUp != 0 && dirUp != lastDirUp) stepUp *= 0.5f;
            if (lastDirRight != 0 && dirRight != 0 && dirRight != lastDirRight) stepRight *= 0.5f;
            if (dirUp != 0) lastDirUp = dirUp;
            if (dirRight != 0) lastDirRight = dirRight;

            Vector3 correction = up * (dirUp * stepUp) + right * (dirRight * stepRight);
            totalCorrection += correction.magnitude;

            if (totalCorrection > placementMaxTotalCorrection)
            {
                failReason = "四角贴合校验失败：累计修正距离超过上限，墙面可能过于不规则";
                return false;
            }

            portalPos += correction;

            if (stepUp < placementMinStep && stepRight < placementMinStep)
            {
                failReason = "四角贴合校验失败：步长已收敛到最小值仍未达标，可能陷入两难位置";
                return false;
            }
        }

        failReason = "四角贴合校验失败：达到最大迭代次数仍未收敛";
        return false;
    }

    /// 正面遮挡校验：矩形包围盒 OverlapBox，不管门实际视觉形状是圆形/三角形/方框，
    /// 因为圆形/三角形都是矩形的内切/内接子集，矩形范围内无遮挡则视觉形状内必然也无遮挡。
    /// A/B互斥校验：不依赖任何碰撞体/图层配置的硬性兜底，保证两扇门永远不会互相重叠。
    /// otherPortal 是"对方"那扇门的 Transform（isPortalA 时 otherPortal=portalB，反之亦然）。
    bool CheckMutualExclusion(Vector3 portalPos, Transform otherPortal, out string failReason)
    {
        failReason = "";
        if (otherPortal == null) return true;

        float halfWidth = 1f;
        float halfHeight = 1f;
        if (portalManager != null)
        {
            halfWidth = Mathf.Max(0.01f, portalManager.portalTriggerWidth * 0.5f);
            halfHeight = Mathf.Max(0.01f, portalManager.portalTriggerHeight * 0.5f);
        }

        // 用门框对角线的一半近似包围球半径：不管门实际是圆形/三角形/方框，这个半径都能完整包住整扇门。
        float portalBoundingRadius = Mathf.Sqrt(halfWidth * halfWidth + halfHeight * halfHeight);
        float minDistance = portalBoundingRadius * 2f + mutualExclusionMargin;

        float actualDistance = Vector3.Distance(portalPos, otherPortal.position);
        if (actualDistance < minDistance)
        {
            failReason = "A/B互斥校验失败：候选位置离另一扇门太近(距离=" + actualDistance.ToString("F3") + " 需要>=" + minDistance.ToString("F3") + ")";
            return false;
        }

        return true;
    }

    bool CheckFrontObstruction(Vector3 portalPos, Quaternion portalRot, Collider wallCollider, Transform selfPortal, out string failReason, out string blockerName)
    {
        failReason = "";
        blockerName = "";

        float halfWidth = 1f;
        float halfHeight = 1f;
        if (portalManager != null)
        {
            halfWidth = Mathf.Max(0.01f, portalManager.portalTriggerWidth * 0.5f);
            halfHeight = Mathf.Max(0.01f, portalManager.portalTriggerHeight * 0.5f);
        }

        Vector3 forward = portalRot * Vector3.forward;
        Vector3 boxCenter = portalPos + forward * placementObstructionOffset;
        Vector3 halfExtents = new Vector3(halfWidth, halfHeight, Mathf.Max(0.001f, placementObstructionDepth * 0.5f));

        Collider[] overlaps = Physics.OverlapBox(boxCenter, halfExtents, portalRot, placementObstructionLayers);

        for (int i = 0; i < overlaps.Length; i++)
        {
            Collider col = overlaps[i];
            if (col == null) continue;

            // 排除贴合用的墙面自己。
            if (wallCollider != null && col == wallCollider) continue;

            // 排除传送枪自身、以及"当前正在放置的这一扇门"自身（含子物体，比如门框装饰用的 Mesh Collider）。
            // 注意：只排除 selfPortal（isPortalA ? portalA : portalB），绝不能把另一扇门也排除掉——
            // 另一扇门必须被当成正常的实体障碍物，否则A、B两扇门可以互相重叠放置到同一个位置。
            if (col.transform == transform || col.transform.IsChildOf(transform)) continue;
            if (selfPortal != null && (col.transform == selfPortal || col.transform.IsChildOf(selfPortal))) continue;

            failReason = "正面遮挡校验失败：检测到障碍物";
            blockerName = col.name;
            return false;
        }

        return true;
    }

    void OnShootFailed(Vector3 rayOrigin, Vector3 hitPoint)
    {
        cooldownTimer = cooldownTime;

        PlayFailSound();

        if (gunAnimator != null && !string.IsNullOrEmpty(shootTriggerFail))
        {
            gunAnimator.SetTrigger(shootTriggerFail);
        }

        if (showDebugRay)
        {
            Debug.DrawLine(rayOrigin, hitPoint, rayColorFail, 1f);
        }
    }

    void PlayFailSound()
    {
        if (audioSource != null && failSounds != null && failSounds.Length > 0)
        {
            int randomIndex = Random.Range(0, failSounds.Length);
            if (failSounds[randomIndex] != null)
            {
                audioSource.PlayOneShot(failSounds[randomIndex]);
            }
        }
    }

    public bool IsOnCooldown()
    {
        return cooldownTimer > 0;
    }

    public float GetCooldownProgress()
    {
        if (cooldownTime <= 0) return 1f;
        return 1f - (cooldownTimer / cooldownTime);
    }

    public float GetRemainingCooldown()
    {
        return Mathf.Max(0, cooldownTimer);
    }

    public Collider GetMarkedColliderA()
    {
        return markedColliderA;
    }
    
    public Collider GetMarkedColliderB()
    {
        return markedColliderB;
    }
    
    public void SetMarkedColliderAEnabled(bool enabled)
    {
        if (markedColliderA != null)
        {
            markedColliderA.enabled = enabled;
        }
    }
    
    public void SetMarkedColliderBEnabled(bool enabled)
    {
        if (markedColliderB != null)
        {
            markedColliderB.enabled = enabled;
        }
    }

    private void OnDrawGizmosSelected()
    {
        if (!showDebugRay) return;
        Gizmos.color = Color.yellow;
        Gizmos.DrawWireSphere(transform.position, maxDistance);
    }
}
