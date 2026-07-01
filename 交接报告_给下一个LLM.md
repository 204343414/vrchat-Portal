# VRChat 双向传送门系统 · 交接报告（面向下一个 LLM）

> 日期：2026-07-02（Asia/Shanghai）
> 仓库：https://github.com/204343414/vrchat-Portal/tree/main
> 工作区：/home/user/vrchat-Portal
> 语言偏好：请用中文与用户沟通。

---

## 0. 一句话现状

系统已经非常接近 1.0：普通墙、普通地板、无限下坠、45°斜墙都基本稳定。
**只剩最后一个 bug**（见第 4 节）：垂直放置的「天花板↔地板」传送门，让玩家**自然下坠（全程不按 WASD、无任何水平输入力）**，玩家会**慢慢漂移、逐渐传送到传送门边缘**，而不是稳定原地循环下落。修好它就是 1.0。

**用户已明确：不要动裁剪距离 / near clip。** 他刚测过，当前 GitHub 版本手感最舒服，near clip 不需要改。

---

## 1. 基线：以 GitHub main 为准（重要）

用户认定「GitHub 上的版本是最舒服的版本」。经核对：

- GitHub `main` 当前文件行数：
  - `双向传送门管理器.cs` = **2309 行**
  - `传送枪.cs` = **344 行**
- 本地 git 的 `origin/main` 缓存是旧的（2102/344，commit 14b06f6）。**sandbox 无法 fetch**（无网络到 git remote），所以别信 `git log origin/main`。请以 `raw.githubusercontent.com` 抓到的真实文件为准。
- 本地工作区 `双向传送门管理器.cs` 与 GitHub main **完全一致**（diff 为空）。
- 本地工作区 `传送枪.cs` 与 GitHub main **有差异**（见第 3 节，必须先和用户确认）。

抓取 GitHub 真实文件的方法（sandbox 可用 curl 到 raw）：
```bash
curl -s "https://raw.githubusercontent.com/204343414/vrchat-Portal/main/%E5%8F%8C%E5%90%91%E4%BC%A0%E9%80%81%E9%97%A8%E7%AE%A1%E7%90%86%E5%99%A8.cs" -o /tmp/gh_mgr.cs
curl -s "https://raw.githubusercontent.com/204343414/vrchat-Portal/main/%E4%BC%A0%E9%80%81%E6%9E%AA.cs" -o /tmp/gh_gun.cs
```

---

## 2. 千万不能动的东西（改坏会前功尽弃）

这些是历经大量深夜调试才稳定下来的地基，除非用户明确要求，**默认不要改**：

1. **经典 halfTurn 架构**（核心）
   - `useClassicHalfTurn = true`（`双向传送门管理器.cs:269`），并在 Start 里运行时强制为 true（约 `:381-388`）。
   - 所有门到门映射统一走 `to * LocalHalfTurn() * from^-1`。
   - **绝对不要**回退到「传送枪把 B 门本体预翻 180°」作为默认。那是所有「同门进出 A进A出/B进B出」bug 的根源。

2. **不要加粗暴的能量守恒 / 速度 clamp / 出口微推补丁**
   - 用户要做 Portal-like 玩法（发射台、fling、精确落点），物理必须 **1:1**、可预测、可计算落点。
   - 历史上被删掉的坑：
     - 旧 `exitNudge = 0.03 + speed*dt*0.5`（按速度放大的出口位移）→ 会「左脚踩右脚越飞越高」。**别加回来。**
     - `pendingVelocityFrames` 固定为 2 的重发 → 注入能量。现在用 `velocityReapplyFrames = 0`，别改回非 0 除非有充分理由。
   - `ApplyOptionalMomentumSnapping`（`:1059`）在 classic 模式下第一行就 `return localVel;`（`:1063`）——即不做任何 snapping。**保持。**

3. **连续穿越物理（crossingT / postCrossDt）**
   - `TeleportSebStyle`（`:1494`）里用 `crossingT` 还原穿越瞬间，再用 `postCrossDt` 在出口世界继续积分重力。这是 1:1 落点的关键。别简化成「直接映射当前帧 head」。

4. **图层切换代替禁用碰撞体**
   - marked collider 的 `gameObject.layer` 走 `28 -> 25 -> 原值`（不是 29！VRChat 里 29 仍与玩家碰撞）。
   - 有 layer 29 -> 25 的运行时迁移，别删。
   - 共享 collider 保护（`protectSharedMarkedCollider`）别删。

5. **root / hybrid traveller 模式**
   - `useRootAsTraveller = true`（`:245`）、`useHybridRootXYHeadZTraveller = true`（`:248`）。
   - hybrid 规则见 `TravellerLocalForPortal`（`:995`）：
     - 平面门(地板/天花板)：`root.x, root.y, head.z`
     - 墙面门：`root.x, head.y, root.z`
   - 真实 TeleportTo 点始终是 root（`TeleportPointLocalForPortal`, `:1023`）。
   - 这套解决了「歪头触发墙门」「脚先传导致头卡天花板」。别退回纯 head。

6. **出口侧保险**（本地工作区已有，见第 3 节）
   - `enableExitSideCorrection`（`:251`）、`exitSideMinDistance = 0.02`（`:254`）。
   - 只在「落到错误侧 / 太贴门」时沿出口法线拉回最小距离，正常时 `fix=0`，不按速度放大。这是给 45°斜墙兜底的。别把它改成随速度推。

7. **过渡系统 portalDelta 用 classic halfTurn**
   - `UpdateTransition` 里 `portalDelta = to * LocalHalfTurn() * from^-1`，再 `ExtractPitchRollOnly`。别改回不带 halfTurn 的版本，否则过渡画面翻转。

---

## 3. ⚠️ 必须先和用户确认的一个矛盾（动手前第一件事）

本地工作区 `传送枪.cs` 与 GitHub main **不一致**，且存在潜在「双翻」逻辑矛盾：

- **GitHub main 的 `传送枪.cs`（344行）**：仍然**无条件**翻 B 门：
  ```csharp
  if (!isPortalA)
  {
      portalRot = portalRot * Quaternion.Euler(0, 180f, 0);
  }
  ```
- **本地工作区 `传送枪.cs`（369行）**：已改成受开关控制，默认不翻：
  ```csharp
  public bool applyBHalfTurnInGun = false;   // 默认关
  ...
  if (!isPortalA && applyBHalfTurnInGun) { ... }   // 只有开关开才翻
  ```
  并加了运行时强制关 + `debugPlayerPhysicsOnStart` 玩家物理日志。

**矛盾点**：管理器现在是 `useClassicHalfTurn=true`（映射里已经做 halfTurn）。
- 如果场景里挂的传送枪**还翻 B 门**（GitHub 版逻辑）→ 理论上会「双翻」→ 应该出问题；
- 但用户说 GitHub 版「最舒服」。说明**用户 Unity 场景里实际生效的传送枪很可能不是 GitHub 那份源码逻辑**，或者 B 门摆放/hit normal 恰好抵消。

**行动：动任何代码前，先用 ask_user 问清楚**：
1. 你 Unity 里现在挂的传送枪，是 GitHub 那份（无条件翻 B 门）还是本地这份（applyBHalfTurnInGun=false 不翻）？
2. 是否希望把本地这份「开关式不翻」的传送枪 push 成 GitHub 版，统一为 classic 架构？

在没确认前，不要擅自 push 传送枪，也不要以为「双翻」是 bug——它可能被场景配置抵消了。

---

## 4. 唯一剩余 bug（要修的就是它）→ 1.0

**现象**：垂直放置的「天花板传送门 ↔ 地板传送门」，玩家**纯自然下坠**（全程不按任何方向键、无水平输入力），会**慢慢横向漂移**，一次次传送后逐渐移动到传送门**边缘**，最终可能掉出门框。理想应是：原地垂直无限循环下落，水平位置几乎不变。

**这是位置漂移（drift），不是能量漂移**。注意区分：之前修好的是「越飞越高」（能量/Y方向），现在这个是「横向 XY 平面缓慢平移」。

### 4.1 建议的排查方向（按可能性排序）

1. **hybrid 平面门的 root/head 混合导致 XY 注入**（最可疑）
   - 平面门 traveller 用 `root.x, root.y, head.z`（触发点），但真实传送点用 root。
   - 关键代码在 `TeleportSebStyle`（`:1494`）：
     - `:1515` `flatHybridTraveller = useRootAsTraveller && useHybridRootXYHeadZTraveller && IsFlatPortal(fromPlane)`
     - `:1519` `mappedCrossingLocal = flatHybridTraveller ? crossingLocal : crossingTeleportLocal;`
     - `:1526-1528` 由 mapped 点 + 出口速度积分得 `newMappedPointPos`
     - `:1568-1571` flat hybrid：`cameraHeadAfterTeleport = newMappedPointPos; newTeleportPos = cameraHeadAfterTeleport - headFromRoot;`
   - **怀疑点**：`crossingLocal`(rootXY+headZ) 与真实 root 的 XY 若有微小差，经过 halfTurn 映射 + 减 `headFromRoot` 后，每次循环会累积一点 XY 偏移。VR/桌面模式下 head 相对 root 有前倾偏移（headFromRoot 的 XZ 分量），下坠时若 head 不完全在 root 正上方，就会每圈平移一点。
   - **验证**：在 OUT 日志里加打印 `newTeleportPos` 的门局部 XY，观察每次循环 XY 是否单调漂移。若是，问题就在 flat hybrid 的 XY 处理。

2. **halfTurn 对 XY 的处理**
   - `LocalHalfTurn()` 是绕门局部 Y 轴 180°。对「地板↔天花板」这种法线竖直的门，局部 Y 轴方向要想清楚：绕 Y 转 180° 会把局部 X 翻号、Z 翻号。若 traveller 的触发点 XY 与真实传送 root 的 XY 在 halfTurn 下被不一致地翻转，就产生固定方向的偏移累积。
   - **建议**：对 flat hybrid，考虑「触发点只用来判定 crossingT / 何时传送，真正的落点 XY 严格用 root 的 crossingTeleportLocal，不要用 head.z 混出来的点再反推 root」。即把 `:1519` 对 flat 分支也改成用 `crossingTeleportLocal`，但触发判定仍用 head.z。需要小心别破坏「脚先传头卡天花板」的修复——这正是当初引入 flat hybrid 的原因，所以要两者兼顾。

3. **crossingT 的插值点选择**
   - `crossingTeleportLocal = Vector3.Lerp(previousTeleportLocal, currentTeleportLocal, crossingT)`（约 `:1417`）。
   - 纯下坠时水平速度≈0，理论上 previous/current 的 XY 相同，Lerp 不该引入 XY 漂移。若这里 XY 有变化，说明 previousTeleportLocal 记录时机（传送后写回）和 current 采样存在坐标系不一致。检查传送后写回：`:1670-1697` 的 `SetTeleportTrackingLocal` 是否用的是「传送后真实 root 在出口门的局部坐标」。

### 4.2 排查用的临时日志（保持简洁，用户讨厌刷屏）

建议临时在 OUT 日志追加**出口门局部 XY**，只在下坠循环里看几行即可：
```csharp
Vector3 outLocal = TravellerLocalForPortal(toPlane, newTeleportPos, cameraHeadAfterTeleport);
TPLog("[OUT xy] lx=" + outLocal.x + " ly=" + outLocal.y);
```
看每次循环 `lx/ly` 是否往同一方向单调爬。定位到根因后**删掉临时日志**再交付。

### 4.3 修复原则

- 只针对「flat 平面门 + 自然下坠」这条路径做最小改动。
- 不要引入速度 clamp / 位置吸附到门中心 之类黑箱（用户明确反对，会破坏精确落点）。
- 若必须加「XY 对齐」，要做成 **可选 bool**（用户偏好保留开关切换），且默认行为对墙面门零影响。

---

## 5. 可以清理的死代码（用户已同意清理，但需谨慎、逐个确认后再删）

用户说「舒服了就开始清死代码」。但因为地基敏感，**建议清理时每删一处先 grep 确认零引用，删完让用户编译/测一次**。以下是已确认基本安全的候选：

1. **`portalState == 2` 冷却分支（死分支）**
   - `双向传送门管理器.cs:1431` `else if (thisPortalState == 2) { ... }` 整块。
   - 全文已无任何地方把 portalState 设为 2（grep `= 2` 只在此分支内部读，赋值只有 `=0`）。这是旧「离开冷却」逻辑，classic 架构下已废弃。可整块删除。
   - 注意：删时确认 `currentBodySide`、`headZ/feetZ`、`RestorePassThroughLayer("leave")` 是否只被这块用到——`currentBodySide` 除此之外只在一条 debug 日志里用（`:1267`），可一并简化。

2. **命名残留（改名，不改逻辑）**
   - `trackingHeadA/B`、`previousHeadLocalA/B`、`SetHeadTracking`、`GetHeadTracking`、`GetPreviousHeadLocal`（`:330-334, :954-975`）现在存的是「traveller local」不是 head。建议重命名为 `...Traveller...` 提升可读性。**纯改名，逐个替换，别改语义。**

3. **`useHeadAsTraveller`（`:258`, `[HideInInspector]`）**
   - 现在是隐藏兼容字段，实际逻辑由 `useRootAsTraveller` 控制。可保留（无害）或删除。删除前 grep 确认无其它引用（目前只有声明处）。

4. **`useSebastianCrossing`（`:236`, `[HideInInspector]`）**
   - 声明后全文无引用（grep 只命中声明行）。可删。

5. **`travellerTrackDepth`（`:239`）**
   - 只被 `IsHeadInsidePortalVisualVolume`（`:880`）用于 oblique clipping 的深度判定，**仍在用**（`:633-634` 调用）。**不是死代码，别删**，但注释说「已去掉开关」有误导，可更新注释。

6. **`enableFlatPortalMomentumSnapping`（`:277`）**
   - classic 模式下 `ApplyOptionalMomentumSnapping` 第一行就 return，所以此字段目前不生效。但它是「非 classic 模式」的兜底。建议**保留**（无害），或标注 `[HideInInspector]`。别在 classic 分支里改动它。

**清理顺序建议**：先删死分支(1) → 编译测试 → 再改名(2) → 再删纯死字段(3)(4)。每步一测。

---

## 6. 用户偏好（务必遵守）

- **中文沟通。**
- 深夜调试很累，**日志要短**：只留 `[T#...]` 和 `[OUT ...]` 这种核心短日志。用户已关 Unity Stack Trace 减噪，别要求他贴海量日志。
- 物理 **1:1**，可预测，可算落点。**拒绝黑箱能量补丁 / 速度 clamp。**
- 喜欢用 **bool 开关**切换模式（head/root 等），改行为尽量给开关、保留旧路径。
- 模糊改动前先问 / 先讲清推理；已明确的下一步就直接做。
- 「token 燃烧」可以接受（用于认真推理），但 debug 日志要精简。
- **不要动 near clip / 裁剪距离**（用户已确认当前手感最舒服）。

---

## 7. 关键文件与行号速查（基于当前 2309 行管理器）

| 项 | 位置 |
|---|---|
| `cameraNearClip = 0.01f` | `双向传送门管理器.cs:14`（**别动**） |
| `useClassicHalfTurn = true` | `:269`；Start 强制 `:381-388` |
| `useRootAsTraveller = true` | `:245` |
| `useHybridRootXYHeadZTraveller = true` | `:248` |
| `enableExitSideCorrection / exitSideMinDistance` | `:251 / :254` |
| `flatPortalDotThreshold = 0.9925` | `:280` |
| `IsFlatPortal` | `:989` |
| `TravellerLocalForPortal`（hybrid 规则） | `:995` |
| `TeleportPointLocalForPortal`（真实 root 点） | `:1023` |
| `ApplyOptionalMomentumSnapping`（classic 直接 return） | `:1059-1063` |
| 穿越检测 / crossingT / crossingLocal | `:1328` 起，Lerp 约 `:1417` |
| **`TeleportSebStyle`（核心传送 + 下坠 bug 就在这）** | `:1494` |
| flatHybridTraveller / mappedCrossingLocal | `:1515 / :1519` |
| newMappedPointPos 积分 | `:1526-1528` |
| flat hybrid 落点反推 root | `:1568-1571` |
| 出口侧保险 | 约 `:1607-1620` |
| 传送后写回 tracking | `:1664-1697` |
| 递归渲染 near/oblique（**别动**） | `:2002` `SyncRecursiveNearClipToPortalPlane` / `:2036` `ApplyObliqueClippingSebStyle` |
| 死分支 `portalState==2` | `:1431`（可删） |

传送枪：翻 B 门逻辑在本地 `传送枪.cs` 约 `:220-230`（`if (!isPortalA && applyBHalfTurnInGun)`）；GitHub 版是无条件 `if (!isPortalA)`（见第 3 节矛盾）。

---

## 8. 下一步（给下一个 LLM 的 TODO 顺序）

1. **先 ask_user 确认第 3 节的传送枪矛盾**（场景里实际挂哪份 / 是否统一 push）。
2. **修第 4 节的下坠横向漂移 bug**（加临时 XY 日志 → 定位 flat hybrid 是否注入 XY → 最小改动修复 → 删临时日志）。这是通往 1.0 的唯一拦路虎。
3. bug 修好、用户确认稳定后，**再做第 5 节死代码清理**（逐步、每步一测）。
4. 全程遵守第 6 节偏好：中文、短日志、1:1 物理、不动 near clip、不加黑箱补丁。
