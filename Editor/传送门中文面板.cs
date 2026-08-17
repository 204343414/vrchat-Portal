// ============================================================
// 传送门中文面板（仅编辑器生效，不参与 Udon 编译）
// ============================================================
// 作用：把「双向传送门管理器」和「传送枪」两个 UdonSharpBehaviour 在 Inspector 里显示的
// 字段标签替换成中文，不改动脚本里任何 public 变量名。
//
// 为什么这样做最安全：
// Unity 按“字段名字符串”把 Inspector 里填的数值序列化保存到场景/预制体文件里。
// 如果直接把 C# 代码里的变量名（比如 noClipDepth）改成中文，Unity 会认为这是一个
// “全新的字段”，找不到旧名字对应的存档值，所有你已经调好的数值会被重置为代码里的默认值。
// 这个脚本完全不碰变量名，只在“显示”这一层做文字替换，序列化路径完全不变，
// 因此不存在任何丢失已保存配置的风险，也不会影响 Udon 编译（本文件放在 Editor 文件夹下，
// 打包/上传世界时会被 Unity 自动排除，不会计入 Udon 程序）。
//
// 使用方法：
// 直接放着就行，不需要额外操作。选中挂了这两个脚本的物体，Inspector 会自动显示中文标签。
// 如果以后想加新字段的中文名，只要在下面的字典里加一行 "英文变量名" -> "中文标签" 即可。
// ============================================================
// ================================================================================
// 交接文档 —— 写给下一个接手这份代码的人（人类或 LLM）
// 最后更新：2026-08-17（砍掉规则5撞墙近似反弹 + Local空间粒子手动清单 + 死代码清理，见十五轮）
// ================================================================================
//
// 【项目结构，一共3个脚本】
//   双向传送门管理器.cs（本文件）：门的渲染（Seb风格递归渲染+oblique裁剪）、传送判定与坐标映射、
//                                过渡视角、碰撞穿透（Layer切换）。这是最核心也最庞大的脚本(2700+行)。
//   传送枪.cs：拾取物，负责把 portalA/portalB 这两个 Transform 挪到玩家瞄准的位置，
//              包含合法放置检测（贴合校验+遮挡校验）、手持图层切换、冷却/输入消抖。
//   Editor/传送门中文面板.cs：只在编辑器生效的自定义 Inspector，把上面两个脚本的英文字段名
//              显示成中文标签，不改变量名本身，不影响 Udon 编译，也不会导致已保存的场景数值丢失。
//
// 【几个绝对不能想当然去改的核心约定，改错了会导致传送方向/朝向全错】
//
// 1. 经典 Portal 半转 (useClassicHalfTurn，默认 true，必须保持 true)：
//    传送枪放置B门时，B门本体的 Transform 不再被额外转180度（传送枪那边 applyBHalfTurnInGun 必须是 false）。
//    所有"A门→B门"的坐标/朝向/速度映射，统一在这个管理器脚本内部用 LocalHalfTurn()（Y轴180度旋转）
//    去乘一次，映射公式统一形如 to * halfTurn * from^-1。递归渲染、摄像机镜像、传送坐标变换、
//    过渡视角旋转补偿——全部必须用同一个 halfTurn，绝对不能有的地方转、有的地方不转，
//    否则地板/天花板门会立刻出现"传送后立刻被传送回去"的鬼畜循环。
//    如果以后想改这个约定，必须搜索全文件所有 `useClassicHalfTurn` 和 `LocalHalfTurn()` 的调用点，
//    一次性全部改掉，不能只改一处。
//
// 2. 传送门形状系统 (portalShapeA / portalShapeB，int 类型，0=圆形 1=三角形 2=方框)：
//    - 用 int 常量（PORTAL_SHAPE_CIRCLE/TRIANGLE/BOX/UNSET）而不是 C# enum，是刻意的：
//      UdonSharp 对自定义 enum 有一些已知的默认值/相等比较的坑，为了稳妥直接用 int。
//    - 默认值是 PORTAL_SHAPE_UNSET(-1)，代表"没有手动设置"，此时会自动回退到旧字段
//      useCircularPortalCheck 换算（true→圆形，false→方框）。这是为了兼容旧场景/旧Prefab，
//      不要把默认值改成 0，否则旧场景升级后行为会静默突变。
//    - 唯一负责判定的函数是 LocalPointInPortalRect(localPoint, shapeType)，
//      所有用到"点是否在门范围内"的地方（传送触发判定、碰撞穿透区域、可见性/体积判定、
//      Gizmos调试线框）全部调用这一个函数、传入 ResolvePortalShape(isPortalA) 解析出来的实际形状。
//      如果以后要加第4种形状，只需要改这一个函数 + GetPortalShapeOutline2D（Gizmos可视化用），
//      不需要动其他任何调用点。
//    - 三角形定义：等腰三角形，尖角朝上，底边在下，用符号面积法判定点在不在三角形内（无三角函数）。
//    - 传送枪那边的"正面遮挡校验"是个例外：无论门是什么形状，永远用矩形包围盒判定，
//      这是刻意的简化（矩形能放，圆形/三角形作为矩形的内切/内接子集必然也能放），不是遗漏。
//
// 3. Traveller 追踪模式 (useRootAsTraveller=true, useHybridRootXYHeadZTraveller=true，
//    这是当前测试出来手感最好的组合，不建议改动)：
//    判断"玩家有没有穿过传送门"这件事，横向XY用玩家根骨(root)位置，纵深Z用头部(head)位置，
//    这样歪头不会误触发传送，但地板/天花板门需要头部真正穿过深度才会触发，不会因为脚先落地
//    就卡在天花板。TeleportSebStyle() 里对"地板门"和"墙面门"两种出口分别有不同的 root/head
//    换算公式（flatHybridTraveller 分支），这部分逻辑很绕，改之前务必在测试世界里对着地板门/
//    墙面门/斜面门分别实测，不要只测一种朝向就断言"改对了"。
//
// 4. 传送触发使用独立的触发平面 (teleportTriggerOffset，默认跟随 noClipDepth)，
//    不是在门的正中心(z=0)触发，而是在门框外侧 z=±teleportTriggerOffset 处触发。
//    这是为了让"从A门外侧穿入→在B门外侧穿出"的沉浸感更连贯（穿过整个门厚度才算数），
//    如果改小/改到0会退化成旧版"沾到门中心线就传送"的行为，手感会变差但不会报错。
//
// 5. Layer 穿透方案 (solidCollisionLayer=28 → playerPassThroughLayer=25)：
//    玩家靠近门时，传送枪打中的碰撞体所在物体会被临时切到 25 层（需要在 Unity 的
//    Physics Collision Matrix 里把 25 设置成"不与 Player 碰撞，但仍与其他刚体碰撞"）。
//    这是替代旧版"直接禁用 Collider"的方案，好处是刚体/物品依然能撞到墙。
//    如果 A、B 两扇门恰好打在同一个 Collider 上，有 protectSharedMarkedCollider 兜底
//    防止两扇门互相抢着复原 Layer，不要在没搞懂这段共享保护逻辑之前就删掉它。
//
// 【传送枪那边这次新加的东西，如果要继续往上迭代，从这几个方法看起】
//   ValidateAndCorrectPlacement()：四角贴合校验，二分搜索式步长收敛（不是固定步长，也不是
//     一次性解析解），已经用独立的数值模拟脚本验证过多种"十字穿模墙面"场景的收敛性和方向正确性，
//     核心细节：贴合目标间隙是 wallOffset 而不是 0；纠偏方向看"哪一侧偏差(badness)更大"而不是
//     直接对"有符号间隙"做差（后者在"悬空"和"嵌入"两种物理状态下方向会算反，这是本轮踩过的坑，
//     写在这里防止未来重蹈覆辙）。
//   CheckFrontObstruction()：正面遮挡校验，矩形 OverlapBox，只排除贴合用的墙面自己和
//     "当前正在放置的这一扇门"自身(selfPortal参数)，绝不排除另一扇门。
//   TryShootPortal() 开头的 RaycastAll 逻辑：Portal 原作手感，瞄准射线只会穿过"自己"这一扇门
//     (portal = isPortalA ? portalA : portalB)自身的碰撞体（含子物体），这样贴着自己的门也能
//     重新微调位置；但另一扇门必须保留为正常障碍物，射线/遮挡检测都不能把它一起忽略掉——
//     早期版本曾经把portalA、portalB两个都从过滤名单里排除，导致瞄B门时激光直接穿过B命中
//     它背后的墙，于是"A被放到了B的位置上"，这是本轮修复的一个真实回归bug，写在这里防止
//     未来重构时不小心又把两扇门都塞进忽略名单。
//   CheckMutualExclusion()：独立于上面两项的第三道防线，不依赖任何碰撞体/图层配置，
//     纯粹用两扇门的包围球半径之和判断候选位置离对方是否太近。这是"A只能放A自己身上、
//     B只能放B自己身上，A放到B身上或反之永远不合法"这条基本规则的硬性兜底——哪怕以后
//     场景里的门框忘了挂碰撞体、或者碰撞体设成了Trigger，这道检查依然能生效。
//     调用点用 enableMutualExclusionCheck 独立开关（不挂在 enablePlacementValidation 下面），
//     因为这是正确性问题不是可选的美观校验，即使用户关掉贴合/遮挡校验也不该被一起关掉。
//   Update() 里的 scrollUpLatched/scrollDownLatched：鼠标滚轮是连续轴不是按键，必须做边缘锁存，
//     否则冷却期间/冷却刚结束瞬间同一次滚动手势会被识别成好几次开火，反复触发失败音效。
//     VR 扳机那边同理已经有 vrTriggerLeftPressed/vrTriggerRightPressed 锁存，是同一个道理。
//
// 【刚体传送 + 抓取已完成（2026-07-09）】
//   - 传送枪新增：Kinematic 抓取（鼠标左键 / VR Grip）、临时质量=1、Layer 切换、grabSounds、Animator bool 可配置。
//   - 管理器新增：OverlapBox + LocalPointInPortalRect(shape) 检测、prevPos 穿越判断、复用 TeleportSebStyle/halfTurn 逻辑。
//   - 支持：抓取中物体可无缝跨门（UpdateHeldAfterTeleport）、Layer 自动切 25、速度/位置/旋转 1:1 映射。
//   - 视觉：完整传送（相对坐标保持），无需新 Shader。性能优先（复用 checkInterval）。
//   - 泛用性：可直接做 Prefab，无需新组件。networking 留空（本地优先）。
//   - 交接注意：刚体传送完全复用现有 traveller/halfTurn/形状系统，极高鲁棒性。
//
// 【clone材质动画跟随修复（2026-08-13，两轮迭代后的最终结论）】
//   症状：本体刚体的材质被动画控制器动画（如变色）时，穿门clone显示文件夹里的默认材质，不跟随变色。
//   查证结论（联网核实Unity官方文档/论坛）：Animator对材质属性的动画【不写进材质本身】，
//         而是通过渲染器的 MaterialPropertyBlock 应用——因此任何"复制/共享材质"的方案都同步不到动画值
//         （第一轮只共享材质引用的修复因此无效）。另外clone的Animator被Destroy是帧末延迟生效的，
//         销毁时Unity会把clone渲染器材质还原回资产，一次性赋值也可能被撤销，需要持续校正。
//   方案（RepointCloneMaterials 双层校正，UpdateRigidbodyClonePoses 每帧执行）：
//         1) 材质引用校正：slot0 sharedMaterial做廉价探针，引用漂移才重写；
//         2) PropertyBlock同步：本体 GetPropertyBlock -> clone SetPropertyBlock
//            （官方API，UdonSharp支持已联网查证：有多个真实VRChat世界用例）。
//
// 【剪刀穿模结构图层还原修复（2026-08-13）】
//   症状：A打地板、B打在穿模过来的斜面上，A的clipVolume把斜面切到穿透层；从A穿到B后，
//         斜面永远卡在穿透层，不还原默认层。
//   根因：传送同帧的 afterTeleport 会对B的markedCollider(斜面)执行 ApplyPassThroughLayer，
//         此时斜面已被A的clipVolume切到穿透层（clipVolume还原发生在LateUpdate更后面），
//         旧代码查不到真原始层就把穿透层本身误记为"原始layer"，离开时"还原"成穿透层=永不还原。
//   方案：新增 FindClipVolumeOriginalLayer 查clipVolume追踪表（它只在非穿透层时接管记录，
//         表里必是真原始值）；在 ApplyPassThroughLayer 记录时（根治，共两处）和
//         RestorePassThroughLayer 还原时（防御兜底）都用它纠正被污染的穿透层记录。
//
// 【斜向门触发语义 + 下落穿透修复（2026-08-13，两轮迭代后的最终形态）】
//   症状：45°斜坡门"靠近就传送"；直直下落穿斜门会穿模不触发。
//   根因1：旧混合检测点是 root XY + head Z 的混搭。纯平/竖直门无碍（head/root平面内坐标重合），
//          但斜门上 head 与 root 的门平面内 XY 相差"身高在平面上的投影"（45°约1.1米）——
//          混搭点不在身体上，直落穿斜门时检测XY偏离头部实际穿平面位置约1.1米→门框检查失败→穿模。
//          同时旧 IsFlatPortal 阈值0.9925把斜坡归为"墙面"(深度用root)，脚先过平面→靠近就传。
//   最终方案：检测点全局统一为纯 head——任何朝向都是"头穿过门平面→传送"，
//          没有任何按角度分类/阈值参与触发判定（用户明确要求去掉角度机制）；
//          实际传送落点仍由 TeleportPointLocalForPortal(root) 计算，检测与落点分离。
//          IsUpwardFacingPortal 降级为纯内部函数(常量0.5)，只用于 TeleportSebStyle 的
//          落点映射配方选择，误判由出口侧保险兜底。改造后零调用的旧 IsFlatPortal 已删除。
//   附带：扫掠判定修复死区洞（上一帧在±triggerOffset死区内时旧门槛哑火），
//          用 lastBodySide 记录来向放行"死区内穿出"，传送出口边界落点种 lastBodySide 防哑火/防重传。
//
// 【手持刚体松手提交（2026-08-14，用户提出的简化方案，取代之前三轮失败的实时干预）】
//   历史教训：三轮"握持期间实时干预"的尝试（穿越即传/线段隧穿判定/折射握持）全部失败，
//         根因一致：枪每帧 MovePosition 刚体，实时干预逻辑每帧又去重定位它，
//         穿越检测还会把重定位产生的位置跳变当成新穿越 → 三方拉扯 = 鬼畜/塞不进/松手位置错乱。
//   最终方案（职责分离，只在松手瞬间动一次）：
//         握持期间：枪独占刚体位置；传送门系统只显示 clone 镜像（UpdateRigidbodyClonePoses
//         每帧用与传送相同的 from→to+半转数学镜像本体，clone位置=正确的出口位置）。
//         松手瞬间：传送枪 ReleaseHeldRigidbody 调用管理器 CommitHeldRigidbodyToClone，两级判定：
//         1) 有活跃clone且本体真在fromPortal平面后侧 → 对齐到clone位姿、清速度、销毁clone；
//         2) 无clone（深捅超过追踪深度1.1米导致追踪移除、clone销毁——这正是早期版本
//            松手提交失效、刚体留在门后的根因）→ 纯几何直判：本体过某门平面且在门框内
//            → 按同款镜像数学传送到另一侧。
//         防拽回闸门：clone路径要求本体在平面后侧；几何路径额外要求本体不在另一扇门的
//         门前区域内（"已经出来了"的状态不提交）。没伸进门时空操作，正常松手行为不变。
//
// 【粒子传送（2026-08-14，1.0后的首个新功能）】
//   功能：白名单粒子系统里穿过门平面的粒子被映射到另一侧（位置+速度同款 from→to+半转数学）。
//   关键设计：
//     - 无状态穿越判定：用粒子速度反推本帧线段 [pos-vel*dt, pos] 求交，不存上一帧位置——
//       Unity粒子缓冲槽位会被死亡粒子复用，按序号对齐不可靠（这是不做prev缓存的原因）。
//     - 矩阵每帧只构造4次（A/B各一套），粒子循环内只有 MultiplyPoint/MultiplyVector。
//     - 性能闸门：白名单 + 距离闸门(particleTeleportMaxDistance) + 每系统每帧读取上限
//       (particleTeleportBufferSize，GetParticles只读缓冲大小颗)。
//     - 一帧至多穿越一次：两门都命中取t更大的（更晚的），与粒子终点一致。
//   使用要求：参与系统 Simulation Space 必须是 World（Local空间语义不同，暂不支持）。
//   泛用化迭代：自动收集两个来源——1) transform.root 子树（预制件自带特效零配置生效，
//     联网查证确认：FindObjectsOfType 实测编译报错、Scene.GetRootGameObjects 在 VRChat
//     官方反馈板有请求但至今未开放，全场景枚举类API整体被 Udon 沙箱禁用；
//     沿层级树 GetComponentsInChildren 是白名单内最优路径）；
//     2) particleDiscoveryRoots 收集根（特效挂在别的根下时手动拖容器，可选）。
//   放置时发现（2026-08-16，用户'巨型触发器'思路的正确工具化）：传送枪放门成功时
//     管理器以门位置为中心 OverlapSphereNonAlloc 枚举半径内碰撞体，向上爬层级/向下
//     GetComponentsInChildren 找粒子系统，去重注册进第三路列表。不用触发器的原因：
//     粒子不触发OnTriggerEnter（已实测），且需求是"找系统"不是"检测粒子到达"。
//     诚实边界：无碰撞体的独立物体无法被枚举，仍需收集根/白名单。
//   贴墙门+碰撞粒子修复：粒子带碰撞时被墙体碰撞体在门平面处弹走，数学上永远穿不过门平面。
//     particleTeleportPlaneOffset 把检测面沿法线推出墙面（默认5cm），粒子撞墙前即传送；
//     同时只收"朝门飞"的穿越（denom<0），反弹向外飞的粒子不误传。
//   高速隧穿修复：缓冲区默认512 + 穿越回溯窗 t∈[-particleTeleportRetroWindow,1]（可调）——
//     粒子碰撞子步使速度反推线段偏离真实路径，回溯窗随速度等比放大补漏；
//     理论边界：逐帧采样无法保证任意速度零隧穿（帧间无数据），但窗口可调到覆盖任意现实速度。
//   风险预案：GetParticles/SetParticles 若在 Udon 白名单外，编译报错后按报错逐项降级。
//   触发器方案裁决（2026-08-16，编译实测）：OnParticleTrigger 未被 Udon 暴露
//         （override 报 CS0115: no suitable method found to override）——Unity 粒子
//         Trigger 模块的回调在 Udon 里接不上线；且该回调无参无位置，即使暴露传送仍需
//         扫缓冲。结论：缓冲扫描是 Udon 约束下粒子传送检测的唯一可行路径，
//         触发器/碰撞事件路线结案，勿再投入。
//   "复刻Unity粒子碰撞逻辑"邪修路线裁决（同日）：不可行且不需要——粒子碰撞实现在
//         闭源C++引擎层（公开Reference Source无此部分）；且Unity粒子碰撞本身也是离散
//         采样（极端高速照样隧穿墙壁），而我们的隧穿病灶在穿越判定环节不在碰撞物理。
//   残余隧穿排查顺序（回溯窗=2仍隧穿时）：1)该系统是否被注册(看注册日志) 2)距离闸门
//         (系统原点>100m跳过,已默认100) 3)缓冲上限(活粒子>1024,已默认1024) 4)诊断日志数字。
//
// 【近门渲染退化区与 teleportTriggerOffset=0 的坑（2026-08-15 用户实测破案）】
//   现象链：teleportTriggerOffset=0 时穿越判定发生在门平面正中(z=0)，传送落点
//         exitTargetDist=max(exitSideMinDistance,0)=0.02米——头恰好停在出口门平面上，
//         卡在"头在门体积内+贴面"的渲染退化区：skipAllOblique生效→递归相机不裁剪→
//         门后墙背面漏进画面；同时相机贴面时斜裁剪数学退化→门面本身渲染异常。
//         转头前探离开退化区后画面恢复正常（用户逐步转头实测复现全链）。
//   解决：teleportTriggerOffset 调高（默认0.3）后所有异常消失——传送提前到触发平面
//         发生，落点离出口平面0.3米，头不再滞留退化区。
//   结论：teleportTriggerOffset 不建议设为0；贴面渲染退化（跳过裁剪会漏、强制裁剪会反向）
//         仍是遗留课题，但正常游玩不会停在退化区，优先级靠后。
//   后续清理（2026-08-16）：诊断开关 obliqueClipWhenHeadInsideVolume 实测"开启更糟、
//         关闭=可用"后已删除（行为锁定为贴门跳过斜裁剪），配套置顶绘制/HelpBox一并移除。
//
// 【粒子传送当前状态（2026-08-16）】
//   已通：无碰撞可传、四路注册、实测位移+速度重建双轨、双向穿越。
//   破案记录（三轮递进）：
//     一轮：方向过滤拒绝平面后方穿越（墙后发射器）→ 双向穿越，回溯窗分方向。
//     二轮：denom>0 回溯窗被误关致重建误差拒收 → 统一回溯窗 + 落点安全边距防回穿。
//     三轮：斜粒子先在框外穿检测面、飘进框内时已在回溯窗外 → 矩形判定补判当前位置。
//     四轮：回溯窗拉到8仍隧穿 → 真凶是窗的计量单位：旧窗按"帧位移线段长度"算，
//           斜粒子每帧在门法线方向前进的分量小，同样窗值对它们覆盖的法线距离更短，
//           越斜越快逃出窗（隧穿的全是斜粒子）。重构为"沿门法线前进距离(米)"计量，
//           与角度无关；本帧穿越(t∈[0,1])必抓 + 已过面法线漂移≤窗值也抓。
//           落点边距随之解耦为固定值(2*offset+窗+0.1)，只需大于窗即防回穿，与速度无关。
//           参数单位由"线段长度"改为"米"，默认0.5（旧的6/8是旧单位，需重设为0.5~2）。
//     五轮：窗改米后高速仍偶发隧穿+出射点偏远 → 根治在配对：旧"归属系统+寿命连续"配对太弱，
//           同系统里寿命相近的两颗粒子会被误认成同一颗 → 实测位移张冠李戴 → 线段错位漏检。
//           改用 randomSeed（粒子出生时分配、终生不变的稳定ID）做身份配对，本帧穿越的粒子
//           无论快慢都必抓；窗退化为只兜底"槽位漂移回退速度重建"的少数粒子，默认降到0.3，
//           出射点(2*offset+窗+0.1)随之从~2.2米缩到~0.5米。注意：场景里已序列化的窗值需从2手动改到0.3。
//     六轮（2026-08-17，window=0 零漏检架构）：用户要求"窗=0 也不漏一颗"。先做理论穷举：
//           漏检只可能来自"穿越那一刻没有可用的位移记录"（首见/槽位洗牌/缓冲截断/注册晚）。
//           动手前先破案旧窗：withinWindow 的符号写反了——forwardPastPlane=dot(segEnd-planePoint,
//           forward)>=0 抓的是检测面【前侧】正在靠近的粒子（门前窗值米内被提前吸走），
//           而真正过平面后的漏检粒子 forwardPastPlane<0 永远进不了窗。窗从没兜住过入口侧漏检，
//           这就是"窗=2 仍残留 1~2 颗隧穿"的病根（残留全靠运气在穿越帧被 t∈[0,1] 抓到）。
//           新架构三条规则：
//             规则1 本帧穿越：配对实测位移线段与检测面符号翻转求交（z插值，退化线段也成立，
//                   门移动扫过静止粒子照样抓）——与刚体/玩家的扫掠防隧穿同构，任意速度必抓；
//             规则2 后侧追补窗（符号修正版）：已过检测面且深度<=窗值 → 补抓（窗=0自然关闭）；
//             规则3 首见后侧兜底：未配对粒子当前已在检测面后侧且门框内 → 立即传送，
//                   覆盖出生即穿越/出生在墙后/注册晚/槽位洗牌/缓冲扩容帧。无乒乓：出口落点在
//                   出口门【前侧】且沿映射速度远离（已用几何证明，不落进任何规则的触发区）。
//           配套：缓冲顶满自动翻倍扩容（不再永久漏）；配对有效标记 particlePrevValid 替代
//           seed!=0 技巧；未配对粒子按 startLifetime-remainingLifetime 反推出生点（<=0.5秒用
//           出生反推，更老只反推一帧防幻影线段）。出射点边距去掉窗值项(2*offset+0.1)再缩近。
//           理论边界（诚实声明）：仍有一种漏——粒子在门框【外】穿过检测面、之后才横向飘进门框
//           后侧区域（此时穿越瞬间在框外不该传、飘进框时又无穿越事件），只有窗>0 能清；
//           正常朝门飞的粒子流穿越点都在框内，不受影响。
//           若 startLifetime 不在 Udon 白名单（编译报错），把那行反推改成 span=dt 即可（规则3兜底不受影响）。
//     七轮（2026-08-17，带碰撞粒子+速度100目标）：用户实测"速度100开碰撞粒子全被墙反弹、
//           一颗不传"。破案：Unity粒子碰撞把粒子当球（半径≈粒子尺寸×Collision模块Radius
//           Scale的百分比，官方文档），粒子在离墙一个碰撞半径处就被弹走；检测面外推(默认
//           0.05m)<粒子碰撞半径时，粒子数学上永远穿不过检测面，规则1/2/3全哑火。帧内顺序
//           （官方执行顺序图）：粒子模拟在所有脚本Update之后，靠调offset只是参数调优。
//           用户的直觉"继承碰撞系统的防隧穿"给出正解：反弹本身就是"到达门面"的最强信号。
//           新增规则4反弹捕获：配对粒子门法向速度一帧内反转(朝门→离门，阈值±0.1m/s)且位置
//           在门框内、z∈(-0.2, offset+1) → 传送。速度映射用反弹【前】速度（用反弹后的会让
//           出口粒子扎进出口墙），锚点投影到检测面（落点恒在出口检测面前侧，防乒乓）。
//           与速度/粒子尺寸/offset值全部无关，系统自愈；墙边最多可见一帧反弹。
//           速度边界诚实声明：我们的线段检测对任意速度免疫（线段更长更好抓）；真正的高速
//           极限在Unity自身——碰撞质量低时粒子会穿墙（官方文档）、startSpeed超过约999
//           粒子模拟自身不稳（用户实测）。产品决策：速度100内带碰撞必传，超出视为噪音。
//           用户实测情报（纠正了两轮误传）：simulationSpeed上限100；startSpeed.scalar
//           可上千但>~999粒子乱飘。
//     八轮（2026-08-17，玩家高速隧穿破案）：用户场景：天花板门+地板门无限坠落加速循环
//           （VRChat无空气阻力），高速后断裂为"碰撞先比传送快一步触发停下来"或隧穿。
//           破案：玩家切层区深度 = noClipDepth + colliderDisableBuffer + 速度缓冲，而速度缓冲
//           被钳死在1.5米（Clamp(speed*dt*2, 0, 1.5)）→ 下落速度>45m/s缓冲停止生长；
//           每帧位移超过区域总深(~2米)时，胶囊一帧从区域外跳到平面外，碰撞体还没切穿透层
//           → 碰撞/隧穿赢了传送。修复：去钳制（仅留50m安全上限防异常值），步长改用
//           max(渲染dt, 物理dt)——真正解算碰撞的是物理步（VRChat FixedUpdate锁刷新率），
//           高帧率下只用渲染dt会低估位移。传送判定本身是线段插值、速度免疫，无需改动。
//           粒子侧对照结论（同轮）：速度100日志"滞留2246"全是门框【外】凿穿地板的粒子
//           （Unity粒子碰撞对世界几何的高速极限，官方文档：碰撞质量决定粒子会不会穿碰撞体），
//           门框内穿越零漏检——粒子门逻辑在100内达成，残留属引擎行为。
//     九轮（2026-08-17，剪刀门高速滞留真凶）：用户实测"关碰撞速度100仍有隧穿粒子，
//           速度10几乎不漏"，滞留4115且无浅深度嫌疑样本（全在深处后侧）。破案：
//           剪刀形重叠门（两门相距~0.5m）+ 速度100的1.7米长线段 → 一根线段同帧穿过
//           两扇门平面；旧逻辑"两门都命中取t更晚的"把先进A门的粒子按B门映射，落点
//           甩到某扇门后侧深处，此后配对成立、四规则全不满足 → 永久滞留可见隧穿。
//           速度10线段短极少双穿 → 几乎不漏（与实测吻合）。修复：ParticleSegmentCrossesPortal
//           输出hitKind（1=框内实穿越/2=兜底命中），选择改为"实穿越取t最早"（先碰先进），
//           无实穿越时兜底命中也取最早。玩家侧补充排查线索：若天花板+地板门高速循环
//           仍断裂，查场景里 teleportBlockFrames 是否为0（>0会在高速下让整段检测停摆），
//           并开 debugTeleportCoreLog 抓异常跳变的 [T#] 行（z区间+t值直接暴露晚检测）。
//     十五轮（2026-08-17，用户裁决砍掉两项粒子辅助功能 + 死代码清理）：
//           1) 规则5撞墙近似反弹整体移除：particleWallBounceAssist 字段、TryWallAssistBounce、
//              诊断计数 particleDebugWallAssistCount 一并删除。原适用场景（门嵌墙+开碰撞+
//              高速隧穿墙面）属Unity离散碰撞引擎极限，产品决策不再由脚本近似接管反弹。
//           2) Local空间粒子手动清单整体移除：particleLocalSpaceSystems 字段、
//              IsLocalSpaceParticleSystem、循环内 localMode 局部↔世界坐标转换与写回转回局部、
//              排除拦截里的Local优先放行全部删除。理由：自动收集已覆盖系统注册，手动拖
//              清单不合理。语义回到十四轮前：参与传送的系统一律按 World 空间处理，已注册的
//              Local 系统会被按世界坐标错误映射（enableParticleTeleport 的 tooltip 声明不变：
//              Local 暂不支持）。若未来要恢复，注意 ps.main.simulationSpace 有 Udon 白名单
//              风险（这正是当初用手动清单的原因）。
//           3) 死代码：debugTeleportLog 字段从未被任何代码读取（总开关tooltip未接线），删除。
//           4) 性能备忘（沿自旧十四轮审计，仍有效）：两门同屏掉帧主因是递归渲染本身——
//              recursiveRenderLimit 默认3，两门同屏=多次全场景 Camera.Render()，属传送门渲染
//              固有价值成本；缓解=调低 recursiveRenderLimit。
//     十一轮（2026-08-17，毕业后实测返修）：
//           a) 放置发现"没工作"破案方向：旧版只扫"碰撞体自身子树+祖先链单点"，
//              粒子系统挂在碰撞体【兄弟节点】（常见预制件结构）时完全扫不到 → 改为
//              爬到最高祖先(8层)后整体 GetComponentsInChildren，祖先+兄弟+后代全覆盖；
//              全程加诊断日志（跳过原因/命中碰撞体数/新注册数），传送枪未接 manager 也告警。
//              另一嫌疑：传送枪 portalManager 槽位未接线（新增 Warning 直接点名）。
//           b) 传送枪自身特效排除：枪的激光/枪口特效被传送会鬼畜。新增
//              particleTeleportExclusionRoots 排除根数组 + 传送枪层级自动排除；
//              ProcessSingleParticleSystem 入口拦截，覆盖白名单/root扫描/放置发现全部路径。
//     十轮（2026-08-17，v1.0毕业收尾）：
//           a) 放置发现健壮性：注册表满员(64)自动翻倍扩容（旧行为静默丢弃）；
//              OverlapSphere缓冲256→512，顶满时输出截断告警（旧行为静默漏扫）。
//           b) 死代码清理：particlePrevLifetimes（五轮改种子配对后只写不读）移除。
//           c) 【已知边界定案】玩家超高速实时隧穿不再追查——用户实验定性：
//              逐帧播放永不隧穿、自然播放速度到位必隧穿。机制：逐帧模式下脚本Update/
//              物理子步/粒子模拟的交错相位完全固定（确定性）；自然播放帧率波动+VRC
//              FixedUpdate锁刷新率+GC插针，交错是随机的，每帧位移数米时"切层先于碰撞
//              还是碰撞先于切层"变成调度掷骰子。逻辑确定、调度随机、速度放大抖动——
//              继续修等于和Unity/VRC线程时序搏斗，性价比到头。记录在案，不算缺陷。
//           d) 粒子侧毕业状态：速度10~100门框内穿越零漏检（规则1~4）；超速残留=
//              Unity粒子碰撞对世界几何的极限+九轮修复前的剪刀双穿（已修）。
//   已知现象（非bug）：六轮后滞设计数应收敛到接近0（出生在后侧的粒子已被规则3直接收走，
//     只剩门框外的后侧粒子）；探针改采"检测面后侧浅深度"样本，并输出配对状态与种子。
//     诊断日志新增"反弹捕获"计数：带碰撞粒子场景此数>0即规则4在工作。
// ================================================================================
#if UNITY_EDITOR
using System.Collections.Generic;
using UnityEditor;
using UnityEngine;

public static class 传送门中文面板_标签表
{
    // 双向传送门管理器 字段名 -> 中文标签
    public static readonly Dictionary<string, string> 门管理器标签 = new Dictionary<string, string>
    {
        { "cameraNearClip", "相机近裁剪面" },

        { "portalParentA", "A门 父物体" },
        { "portalPlaneA", "A门 门面" },
        { "cameraA", "A门 摄像机" },
        { "portalMatA", "A门 材质球" },

        { "portalParentB", "B门 父物体" },
        { "portalPlaneB", "B门 门面" },
        { "cameraB", "B门 摄像机" },
        { "portalMatB", "B门 材质球" },

        { "noClipDepth", "门厚度（无碰撞深度）" },
        { "portalTriggerWidth", "门框宽度" },
        { "portalTriggerHeight", "门框高度" },
        { "clipPlaneOffset", "裁剪面偏移" },

        { "portalShapeA", "A门 判定形状(0圆1三角2方框)" },
        { "portalShapeB", "B门 判定形状(0圆1三角2方框)" },
        { "useCircularPortalCheck", "旧版-使用圆形判定" },

        { "portalGun", "传送枪引用" },
        { "colliderDisableBuffer", "碰撞穿透缓冲距离" },
        { "solidCollisionLayer", "实体碰撞层" },
        { "playerPassThroughLayer", "玩家穿透层" },

        { "enableParticleTeleport", "启用粒子传送" },
        { "portalParticleSystems", "粒子传送白名单(手动)" },
        { "particleTeleportBufferSize", "粒子传送-初始缓冲(自动扩容)" },
        { "particleTeleportMaxDistance", "粒子传送-距离闸门" },
        { "autoDiscoverParticleSystems", "粒子传送-自动收集开关" },
        { "particleDiscoveryRoots", "粒子传送-收集根(拖容器)" },
        { "particleTeleportExclusionRoots", "粒子传送-排除根(枪已自动排除)" },
        { "particleDiscoveryRadius", "粒子传送-自动收集半径" },
        { "particleDiscoveryRefreshInterval", "粒子传送-收集刷新间隔" },
        { "particleTeleportPlaneOffset", "粒子传送-检测面外推(防墙弹)" },
        { "particleTeleportRetroWindow", "粒子传送-后侧追补深度(0=零漏检)" },

        { "enableVisibilityOptimization", "启用可见性优化" },
        { "maxRenderDistance", "最大渲染距离" },
        { "maxViewAngle", "最大视角" },
        { "checkInterval", "检测间隔帧数" },

        { "showDebugGizmos", "显示调试线框" },
        { "gizmoColorA", "A门 调试颜色" },
        { "gizmoColorB", "B门 调试颜色" },

        { "vrTargetFOV", "VR目标视场角" },

        { "enableSebRecursiveRendering", "启用递归渲染" },
        { "recursiveRenderLimit", "递归渲染层数上限" },
        { "recursiveForceManualCamerasDisabled", "递归-强制关闭相机自动渲染" },
        { "recursiveUseSkyboxTerminal", "递归-终点用天空盒" },
        { "recursiveHideExitScreen", "递归-隐藏出口门面" },
        { "recursiveEarlyStop", "递归-提前停止" },
        { "recursiveMaxDistance", "递归-提前停止距离" },
        { "recursiveMaxViewAngle", "递归-提前停止视角" },
        { "debugRecursiveRenderLog", "递归-调试日志" },
        { "debugRecursiveLogIntervalFrames", "递归-调试日志间隔帧" },
        { "recursiveDisplayMaskProperty", "递归-显示遮罩属性名" },
        { "recursiveTerminalUseDisplayMask", "递归-终点用显示遮罩" },
        { "recursiveHideExitUseDisplayMask", "递归-隐藏出口用显示遮罩" },
        { "recursiveForceClearSkybox", "递归-强制清屏为天空盒" },
        { "recursiveUseSebObliqueClip", "递归-使用Seb斜切裁剪" },
        { "recursiveNearClipOffset", "递归-近裁剪偏移" },
        { "recursiveNearClipLimit", "递归-近裁剪阈值" },
        { "recursiveForceObliqueClip", "递归-强制斜切裁剪" },
        { "recursiveFlipObliqueClipNormal", "递归-翻转裁剪法线" },
        { "recursiveRenderUseClassicHalfTurn", "递归-使用经典半转" },
        { "recursiveSyncNearClipToPortalPlane", "递归-近裁剪贴合门面" },
        { "recursiveDynamicNearClipPadding", "递归-动态近裁剪余量" },

        { "enablePortalOverlayWhenHeadNear", "近门时门面置顶" },
        { "portalOverlayDepth", "门面置顶触发深度" },
        { "portalOverlayZTestProperty", "门面置顶ZTest属性名" },
        { "recursivePauseDuringTransition", "过渡时暂停递归(旧)" },
        { "recursiveDynamicNearClipMax", "动态近裁剪最大值" },
        { "debugRecursiveClipLog", "递归裁剪调试日志" },

        { "portalViewTransitionCube", "过渡视角立方体" },
        { "transitionDuration", "过渡时长" },
        { "transitionCameraSafeNearClip", "过渡相机安全近裁剪" },

        { "dumpConfigSnapshotOnStart", "开局打印配置快照" },

        { "debugTeleportCoreLog", "调试-核心传送日志" },
        { "debugLayerLog", "调试-图层切换日志" },
        { "debugTransitionLog", "调试-过渡相机日志" },
        { "debugTeleportVerbose", "调试-详细状态日志" },
        { "debugLogIntervalFrames", "调试-日志间隔帧数" },
        { "playerCapsuleRadius", "玩家胶囊体半径" },
        { "playerCapsuleHeight", "玩家胶囊体高度" },
        { "teleportBlockFrames", "传送后屏蔽帧数" },
        { "stopAfterTeleportSameFrame", "传送后结束本帧" },
        { "protectSharedMarkedCollider", "保护共享碰撞体" },

        { "travellerTrackDepth", "追踪触发深度" },
        { "crossingEpsilon", "穿越判定死区" },

        { "teleportTriggerOffset", "传送触发面偏移" },
        { "useRootAsTraveller", "使用根骨追踪" },
        { "useHybridRootXYHeadZTraveller", "混合追踪(头判定/根骨落点)" },
        { "enableExitSideCorrection", "出口侧保险修正" },
        { "exitSideMinDistance", "出口最小安全距离" },
        { "useVRCTrackingRootTeleport", "旧版-头部反推根骨" },
        { "useScaleFreePortalMatrix", "忽略门物体缩放" },
        { "useClassicHalfTurn", "经典Portal半转" },
        { "keepPlayerUpright", "传送后保持站立" },
        { "enableFlatPortalMomentumSnapping", "地板门动量吸附" },
        { "flatPortalDotThreshold", "地板门判定阈值" },
        { "verticalVelocitySnapThreshold", "垂直速度吸附阈值" },

        { "isVRPlayer", "状态-是否VR玩家" },
        { "currentFOV", "状态-当前视场角" },
        { "isCameraARendering", "状态-A相机渲染中" },
        { "isCameraBRendering", "状态-B相机渲染中" },
        { "isClippingActiveA", "状态-A裁剪生效" },
        { "isClippingActiveB", "状态-B裁剪生效" },
        { "playerNearestPortal", "状态-玩家最近的门" },
        { "portalStateA", "状态-A门状态" },
        { "portalStateB", "状态-B门状态" },
        { "colliderADisabled", "状态-A碰撞体已禁用" },
        { "colliderBDisabled", "状态-B碰撞体已禁用" },
        { "recursiveDepthRenderedA", "状态-A递归渲染深度" },
        { "recursiveDepthRenderedB", "状态-B递归渲染深度" },

        { "velocityReapplyFrames", "速度重发帧数" },
    };

    // 传送枪 字段名 -> 中文标签
    public static readonly Dictionary<string, string> 传送枪标签 = new Dictionary<string, string>
    {
        { "portalA", "传送门A" },
        { "portalB", "传送门B" },

        { "maxDistance", "最大射程" },
        { "shootPoint", "射线发射点" },
        { "placementLayers", "可放置层" },
        { "blockedLayers", "阻挡层" },
        { "wallOffset", "墙面偏移距离" },

        { "cooldownTime", "发射冷却时间" },
        { "playSoundOnCooldown", "冷却中播放失败音效" },

        { "gunAnimator", "枪械动画控制器" },
        { "shootTriggerA", "发射A门Trigger名" },
        { "shootTriggerB", "发射B门Trigger名" },
        { "shootTriggerFail", "发射失败Trigger名" },

        { "audioSource", "音源组件" },
        { "shootSoundsA", "A门音效数组" },
        { "shootSoundsB", "B门音效数组" },
        { "failSounds", "失败音效数组" },

        { "markedColliderA", "A门标记的碰撞体" },
        { "markedColliderB", "B门标记的碰撞体" },

        { "showDebugRay", "显示调试射线" },
        { "rayColorA", "A门射线颜色" },
        { "rayColorB", "B门射线颜色" },
        { "rayColorFail", "失败射线颜色" },

        { "applyBHalfTurnInGun", "旧版-枪身给B门加180度" },
        { "debugPortalGunLog", "调试-放置日志" },
        { "debugPlayerPhysicsOnStart", "调试-开局打印玩家物理参数" },

        { "switchLayerWhenHeld", "手持时切换图层" },
        { "heldLayer", "手持时的图层" },

        { "enablePlacementValidation", "启用合法放置检测" },
        { "portalManager", "传送门管理器引用" },
        { "placementCornerInsetRatio", "贴合检测-角点内缩比例" },
        { "placementGapTolerance", "贴合检测-间隙容差" },
        { "placementMaxIterations", "贴合检测-最大迭代次数" },
        { "placementInitialStep", "贴合检测-初始步长" },
        { "placementMinStep", "贴合检测-最小步长" },
        { "placementMaxTotalCorrection", "贴合检测-累计修正上限" },
        { "placementProbeOutDistance", "贴合检测-探测起点外推距离" },
        { "placementMaxNormalAngle", "贴合检测-最大法线夹角" },
        { "placementObstructionDepth", "遮挡检测-盒子厚度" },
        { "placementObstructionOffset", "遮挡检测-盒子法线偏移" },
        { "placementObstructionLayers", "遮挡检测-检测层" },
        { "enableMutualExclusionCheck", "A/B互斥校验-启用" },
        { "mutualExclusionMargin", "A/B互斥校验-安全间距" },
        { "debugPlacementValidationLog", "调试-放置校验日志" },
    };
}

[CustomEditor(typeof(双向传送门管理器))]
public class 双向传送门管理器_中文面板 : Editor
{
    public override void OnInspectorGUI()
    {
        if (UdonSharpEditorGUIHelper.尝试绘制默认头部(serializedObject, target)) return;

        serializedObject.Update();
        UdonSharpEditorGUIHelper.绘制中文字段(serializedObject, 传送门中文面板_标签表.门管理器标签);
        serializedObject.ApplyModifiedProperties();
    }
}

[CustomEditor(typeof(传送枪))]
public class 传送枪_中文面板 : Editor
{
    public override void OnInspectorGUI()
    {
        if (UdonSharpEditorGUIHelper.尝试绘制默认头部(serializedObject, target)) return;

        serializedObject.Update();
        UdonSharpEditorGUIHelper.绘制中文字段(serializedObject, 传送门中文面板_标签表.传送枪标签);
        serializedObject.ApplyModifiedProperties();
    }
}

// 通用绘制辅助：按 SerializedObject 的默认可见字段顺序遍历，命中字典就换中文标签，
// 命不中就原样绘制（保留原本的英文名 + Tooltip），保证以后新增字段不会“凭空消失”。
public static class UdonSharpEditorGUIHelper
{
    public static bool 尝试绘制默认头部(SerializedObject so, Object target)
    {
        // 尝试调用 UdonSharpEditor 提供的默认头部（转换成 UdonBehaviour 的按钮、同步设置等）。
        // 用反射调用，避免本文件在没有安装 UdonSharp 编辑器插件时直接编译失败。
        var udonSharpGUIType = FindType("UdonSharpEditor.UdonSharpGUI");
        if (udonSharpGUIType != null)
        {
            var method = udonSharpGUIType.GetMethod("DrawDefaultUdonSharpBehaviourHeader", new System.Type[] { typeof(Object) });
            if (method != null)
            {
                object result = method.Invoke(null, new object[] { target });
                if (result is bool b) return b;
            }
        }
        return false;
    }

    private static System.Type FindType(string fullName)
    {
        foreach (var asm in System.AppDomain.CurrentDomain.GetAssemblies())
        {
            var t = asm.GetType(fullName);
            if (t != null) return t;
        }
        return null;
    }

    public static void 绘制中文字段(SerializedObject so, Dictionary<string, string> 标签表)
    {
        SerializedProperty prop = so.GetIterator();
        bool enterChildren = true;
        while (prop.NextVisible(enterChildren))
        {
            enterChildren = false;

            if (prop.name == "m_Script")
            {
                using (new EditorGUI.DisabledScope(true))
                {
                    EditorGUILayout.PropertyField(prop, true);
                }
                continue;
            }

            string label;
            if (标签表.TryGetValue(prop.name, out label))
            {
                GUIContent content = new GUIContent(label, prop.tooltip);
                EditorGUILayout.PropertyField(prop, content, true);
            }
            else
            {
                EditorGUILayout.PropertyField(prop, true);
            }
        }
    }
}
#endif
