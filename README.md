# SPT-ZombieHost

按主机开关的原版万圣节僵尸（SPT 4.1.6 + Fika 合作联机）。

**核心思路**：不新增僵尸角色、不加自定义刷新表，而是把 SPT 原版 `halloweenzombies` 波数据
按"谁开房"注入到战局里 —— 只有白名单里的主机开房才出僵尸，其它主机开房是完全干净的原版战局。

- 服务端 mod：`VanillaZombiePerHost`
- 客户端门控 / AI 增强：`DshZombieEvent`
- SAIN 兼容：`SainZombieFix`

适配环境：`SPT 4.1.6` · `EFT 0.16.9.40743` · `BepInEx 5.4.23.5` · `Fika.Core 2.4.x`

---

## 一、三个组件各干什么

| 组件 | 端 | 作用 |
|---|---|---|
| `VanillaZombiePerHost` | 服务端 (`SPT_Runtime/user/mods`) | 按开房主机 profileId 决定这一局是"僵尸局"还是"普通局" |
| `DshZombieEvent` | 客户端（只装在**僵尸主机**那台） | 打开客户端被锁死的万圣节僵尸总开关，并让僵尸能跑、能砍、不被 bot 上限卡住 |
| `SainZombieFix` | 客户端（装了 SAIN 的端） | 让 SAIN 不要接管感染体，否则僵尸会当普通枪手站着不动 |

---

## 二、服务端：VanillaZombiePerHost

Harmony 后置 `SPTarkov.Server.Core.Services.InRaid.LocationLifecycleService.GenerateLocationAndLoot`，
每局生成地图时判定：

- **开房主机在白名单里** → 把这局的感染度写成 `infectionPercentage`，再按
  `SPT_Runtime/SPT_Data/configs/seasonalevents.json` 里 `eventBossSpawns.halloweenzombies.<地图>`
  的数据注入僵尸波（原版时间/触发方式照抄）。
- **不在白名单里** → 把地图里任何 infected/zombie 波剥掉，其余一个字都不改。

关键点：`GenerateLocationAndLoot` 内部是 `cloner.Clone(location.Base)`，每局拿到独立副本，
往上写感染度不会污染缓存、不会串到下一局、也不影响同时开的其它图。

### 配置 `config.json`

```json
{
  "zombieHosts": ["<僵尸主机的 profileId>"],
  "mode": "vanilla",
  "firstWaveDelaySeconds": 10,
  "waveIntervalSeconds": 300,
  "wavesPerMap": 6,
  "entriesPerWave": 5,
  "escortAmount": "2-3",
  "perWave": 5,
  "infectionPercentage": 100,
  "dropTriggerWaves": true
}
```

| 键 | 说明 |
|---|---|
| `zombieHosts` | 允许出僵尸的开房者 profileId（大小写不敏感）。留空 = 谁开都不出 |
| `mode` | `vanilla` 用原版时间表；`waves` 用 `firstWaveDelaySeconds`/`waveIntervalSeconds` 自定义节奏；`off` 全关 |
| `perWave` | 每波补到几只（原版每条波配置里只有 1 只，用 escort 补齐） |
| `infectionPercentage` | 写入战局的地图感染度，客户端 `CalcBotsCanSpawnedPerCrowd()` 靠它算尸潮规模 |
| `dropTriggerWaves` | 去掉 `TriggerId=InfectedSpawn*` 那批"要事件才触发"的波，只留定时波 |

---

## 三、客户端：DshZombieEvent

`Enabled=false`（默认）时**不挂任何 Harmony**，插件等于不存在；只有僵尸主机那台设 `true`。

开了之后做五件事：

1. **打开总开关** —— 前缀 `BotsEventsController.Activate / ManualUpdate / SpawnAction`，
   把 `BotInternalSettingsController.Core.ACTIVE_HALLOWEEN_ZOMBIES_EVENT` 置 `true`。
   原版这个开关只有服务端打开 `zombieSettings.enabled` 才会写 true，而那条路会连带打开
   万圣节任务、召唤 Boss、敌友表改写 —— 我们只借开关，不借副作用。
2. **不被 bot 上限卡住** —— 前缀 `BotSpawner.CheckOnMax`，僵尸波不再因为 ABPS/原版
   `MaxBots` 已满而延迟。
3. **别走走停停** —— 后置 `InfectedAttackLayer.GetSlowMeleeZombieDecision`，
   把 `runToEnemyZigZag`(25) / `goToEnemyZigZag`(26) 改成直线 `goToEnemy`(8)。
4. **速度下限** —— 前缀 `BotMover.SetSprintSpeed`，僵尸冲刺速度不低于 `MinSprintSpeed`
   （原版只有 0.85~2.35，人类约 4.5）。
5. **到点强制挥刀** —— 每 `MeleeKickInterval` 秒扫一遍：非感染目标进入 `MeleeRange` 且手上
   拿着刀时，反射调 `KnifeController.MakeKnifeKick()`，不等 AI 自己想起要砍。

配置 `BepInEx/config/com.dsh.zombieevent.cfg`：

```
Enabled = false          # 只有僵尸主机那台 true
DriveMelee = true        # 强制挥刀
MeleeRange = 4           # 进入这个距离就挥（原版 oneMeleeAttack 阈值也是 4 米）
MinSprintSpeed = 3.6     # 冲刺速度下限
ApplyInfection = false   # 距离触发感染（默认关，感染交给下面"感染者之手"的武器 buff）
InfectionRange = 2.5
InfectionCooldown = 8
```

---

## 四、客户端：SainZombieFix

SAIN 的排除列表是硬编码的静态 `WildSpawnType` 集合，感染体不在里面 ⇒ SAIN 把僵尸当普通
枪手接管 ⇒ 僵尸丢近战层、站着不开枪也不砍。本插件后置
`SAIN.Components.BotController.BotSpawnController.IsExcluded(WildSpawnType)`，
角色名含 `infected` 时直接返回 `true`，SAIN 跳过，僵尸回到原版 AI。

没装 SAIN 的端不需要它（装上也无害，找不到类型只会打一条 warning）。

---

## 五、需要你手动准备的两份数据

这两处不在本仓库里（属于游戏数据文件，且已被其它 mod 改动过），需要在服务端 `SPT_Data` 里确认：

1. **`database/bots/types/infected*.json` 的 `ENEMY_BOT_TYPES`**
   原版这五个文件里这个数组是**空的**，只有 `DEFAULT_*_BEHAVIOUR: AlwaysEnemies`（打玩家）
   和 `FRIENDLY_BOT_TYPES`（互相是朋友）。所以僵尸不理 scav / PMC / boss。
   把 `dist/data/enemy-bot-types.txt` 里的 56 个角色填进去（4 个难度段都要填），
   僵尸就会主动去找 AI 打。

2. **`database/templates/items.json` 里 `weapon_InfectionArms`（感染者之手）的 `StimulatorBuffs`**
   原版这个隐藏武器没有 buff，导致"命中也不感染"。设成 `"BuffsZombieInfection"` 即可 ——
   命中时走 EFT 原版 buff 系统上感染，`ShowZone`/`ShowIcon`/`UseScreenPanel` 都是原版行为。

这两项都是纯数据改动，改完重启服务端。

---

## 六、安装

**服务端**

```
SPT_Runtime/user/mods/VanillaZombiePerHost/{VanillaZombiePerHost.dll, VanillaZombiePerHost.deps.json, config.json}
```

**客户端（每一台都要）**

```
BepInEx/plugins/SainZombieFix/SainZombieFix.dll
BepInEx/plugins/DshZombieEvent/DshZombieEvent.dll     # 装到所有端，但只有僵尸主机把 cfg 的 Enabled 设 true
```

**无头**同理：三个文件都放，然后只改僵尸主机那台的 `com.dsh.zombieevent.cfg`。

> 为什么客户端 DLL 要装在每一台：NarcoNet 之类的分发工具是整目录同步的，且 `Enabled` 由各端
> 本地 cfg 控制，装了不启用不会有任何副作用。

---

## 七、自己编译

客户端（需要游戏目录里的 `BepInEx/core/0Harmony.dll` 等）：

```powershell
dotnet build src/DshZombieEvent/DshZombieEvent.csproj -c Release -p:EFT_DIR="C:\SPT"
dotnet build src/SainZombieFix/SainZombieFix.csproj -c Release -p:EFT_DIR="C:\SPT"
```

服务端（`net10.0`，需要 SPT 服务器目录下的 `SPTarkov.Server.Core.dll` 等）：

```powershell
dotnet build src/VanillaZombiePerHost/VanillaZombiePerHost.csproj -c Release -p:SptDir="C:\SPT\SPT_Runtime"
```

---

## 八、已知限制

- **AI 只在主机侧跑**：僵尸由僵尸主机那台模拟并同步，所以 `DshZombieEvent` 必须装在主机那台，
  装在客机上没有任何作用。
- **刷点集中在原版 zone**：波次位置来自 `seasonalevents.json` 的 `BossZone`，
  像 `sandbox_high` 只有 `ZoneSandbox` 一个点，一波 20 只容易挤在一起。
- **不是"全年万圣节"**：不碰 `seasonalevents.json` 的启用窗口/感染度，也不启用
  `zombieSettings.enabled`，因此不会触发万圣节任务、召唤事件和敌友表改写。
- **原版没有的"正确感染"入口**：`IZombieInfection` 是空接口，感染只能走武器的
  `StimulatorBuffs`（见第五节第 2 条）或距离兜底（`ApplyInfection`）。

---

## 九、免责声明

非官方 mod，仅用于单机 / 朋友间合作联机。与 Battlestate Games、SPT 官方无关。
