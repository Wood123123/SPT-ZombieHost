using System;
using System.Collections.Generic;
using System.Reflection;
using BepInEx;
using Comfort.Common;
using EFT;
using EFT.InventoryLogic;
using UnityEngine;
using BepInEx.Configuration;
using BepInEx.Logging;
using HarmonyLib;

namespace DshZombieEvent
{
    // 原版万圣节僵尸事件在客户端被 BotInternalSettingsController.Core.ACTIVE_HALLOWEEN_ZOMBIES_EVENT 卡住。
    // 服务端只有把 seasonal event 的 zombieSettings.enabled 打开才会置 true，而那会连带打开
    // 召唤/敌友表/感染 UI，普通局也被污染。这里改成"哪台机器当僵尸主机就在哪台强制置 true"。
    [BepInPlugin("com.dsh.zombieevent", "DSH Zombie Event Gate", "1.0.0")]
    public sealed class DshZombieEventPlugin : BaseUnityPlugin
    {
        internal static ManualLogSource Log;
        internal static ConfigEntry<bool> Enabled;
        internal static ConfigEntry<bool> LogCrowd;
        internal static ConfigEntry<bool> DriveMelee;
        internal static ConfigEntry<float> MeleeKickInterval;
        internal static ConfigEntry<float> MinSprintSpeed;
        internal static ConfigEntry<double> MeleeRange;
        internal static ConfigEntry<bool> ApplyInfection;
        internal static ConfigEntry<float> InfectionRange;
        internal static ConfigEntry<float> InfectionCooldown;

        private static bool _warnedMissing;

        private void Awake()
        {
            Log = Logger;
            Enabled = Config.Bind("General", "Enabled", false, "强制开启原版万圣节僵尸事件（只有僵尸主机那台需要 true）");
            LogCrowd = Config.Bind("General", "LogCrowdSpawn", true, "把 crowd 刷怪/追击记到日志");
            DriveMelee = Config.Bind("General", "DriveMelee", true, "每 0.25s 让 4 米内的僵尸直接 MakeKnifeKick（不依赖 AI 决策）");
            MeleeKickInterval = Config.Bind("General", "MeleeKickInterval", 0.25f, "近战驱动轮询间隔（秒）");
            MinSprintSpeed = Config.Bind("General", "MinSprintSpeed", 3.6f, "僵尸冲刺速度下限（原版僵尸只有 0.85~2.35，人类约 4.5）");
            MeleeRange = Config.Bind("General", "MeleeRange", 4.0, "僵尸进入这个距离就强制挥刀（原版 oneMeleeAttack 阈值也是 4 米）");
            ApplyInfection = Config.Bind("Infection", "ApplyInfection", true, "被僵尸近身时给自己上原版感染效果 ZombieInfection（普通局没有僵尸，不会误触）");
            InfectionRange = Config.Bind("Infection", "InfectionRange", 2.5f, "僵尸贴到多近算咬到（米）");
            InfectionCooldown = Config.Bind("Infection", "InfectionCooldown", 8f, "两次上感染之间至少间隔（秒）");

            if (!Enabled.Value)
            {
                Log.LogInfo("[ZombieEvent] disabled in config, vanilla gate left untouched");
                return;
            }

            try
            {
                var harmony = new Harmony("com.dsh.zombieevent");
                var gate = new HarmonyMethod(typeof(DshZombieEventPlugin).GetMethod(nameof(Gate), BindingFlags.Static | BindingFlags.NonPublic));
                var eventsType = AccessTools.TypeByName("BotsEventsController");

                foreach (var name in new[] { "Activate", "ManualUpdate", "SpawnAction" })
                {
                    var m = eventsType == null ? null : AccessTools.Method(eventsType, name);
                    if (m == null)
                    {
                        Log.LogWarning("[ZombieEvent] missing BotsEventsController." + name);
                        continue;
                    }

                    harmony.Patch(m, prefix: gate);
                    Log.LogInfo("[ZombieEvent] patched BotsEventsController." + name);
                }

                // 僵尸主机的最大 bot 上限：BotSpawner.CheckOnMax 会把超出 MaxBots 的波次推迟/截断，
                // 僵尸波一多就"刷不出来"。这里只在本机（僵尸主机）把上限判断整体放行。
                var spawnerType = AccessTools.TypeByName("EFT.BotSpawner") ?? AccessTools.TypeByName("BotSpawner");
                var checkOnMax = spawnerType == null ? null : AccessTools.Method(spawnerType, "CheckOnMax");
                if (checkOnMax == null)
                {
                    Log.LogWarning("[ZombieEvent] missing BotSpawner.CheckOnMax");
                }
                else
                {
                    harmony.Patch(checkOnMax, prefix: new HarmonyMethod(typeof(DshZombieEventPlugin).GetMethod(nameof(NoBotCap), BindingFlags.Static | BindingFlags.NonPublic)));
                    Log.LogInfo("[ZombieEvent] patched BotSpawner.CheckOnMax (no bot cap)");
                }

                if (LogCrowd.Value)
                {
                    var crowdType = AccessTools.TypeByName("BotHalloweenCrowdSpawn");
                    var doSpawn = crowdType == null ? null : AccessTools.Method(crowdType, "DoSpawn");
                    if (doSpawn != null)
                    {
                        harmony.Patch(doSpawn, postfix: new HarmonyMethod(typeof(DshZombieEventPlugin).GetMethod(nameof(AfterDoSpawn), BindingFlags.Static | BindingFlags.NonPublic)));
                        Log.LogInfo("[ZombieEvent] patched BotHalloweenCrowdSpawn.DoSpawn");
                    }

                    var withZombies = AccessTools.TypeByName("BotHalloweenWithZombies");
                    var pursuit = withZombies == null ? null : AccessTools.Method(withZombies, "StartPursuit");
                    if (pursuit != null)
                    {
                        harmony.Patch(pursuit, prefix: new HarmonyMethod(typeof(DshZombieEventPlugin).GetMethod(nameof(BeforeStartPursuit), BindingFlags.Static | BindingFlags.NonPublic)));
                        Log.LogInfo("[ZombieEvent] patched BotHalloweenWithZombies.StartPursuit");
                    }
                }

                // easy -> Slow 模式：InfectedLayersStrategy 构造函数 SetCharacterMovementSpeed(0)，僵尸几乎不动
                //（迟钝），也走不到 4 米内 => 永远不攻击。hard -> Shooting 模式拿手枪不打近战。
                // 统一强制 Fast：正常奔跑速度 + 匕首近战。
                var strategyType = AccessTools.TypeByName("InfectedLayersStrategy");
                var modeGetter = strategyType == null ? null : AccessTools.Method(strategyType, "GetZombieModeByDifficulty");
                if (modeGetter == null)
                {
                    Log.LogWarning("[ZombieEvent] missing InfectedLayersStrategy.GetZombieModeByDifficulty");
                }
                else
                {
                    harmony.Patch(modeGetter, postfix: new HarmonyMethod(typeof(DshZombieEventPlugin).GetMethod(nameof(ForceFastZombie), BindingFlags.Static | BindingFlags.NonPublic)));
                    Log.LogInfo("[ZombieEvent] patched InfectedLayersStrategy.GetZombieModeByDifficulty (force Fast)");
                }

                // 原版僵尸的冲刺速度由 BotMover.SetSprintSpeed 压到 0.85~2.35（人类 ~4.5），
                // 这里把僵尸的下限抬到 MinSprintSpeed。
                var moverType = AccessTools.TypeByName("EFT.BotMover") ?? AccessTools.TypeByName("BotMover");
                var setSprint = moverType == null ? null : AccessTools.Method(moverType, "SetSprintSpeed");
                if (setSprint == null)
                {
                    Log.LogWarning("[ZombieEvent] missing BotMover.SetSprintSpeed");
                }
                else
                {
                    harmony.Patch(setSprint, prefix: new HarmonyMethod(typeof(DshZombieEventPlugin).GetMethod(nameof(SprintSpeedPrefix), BindingFlags.Static | BindingFlags.NonPublic)));
                    Log.LogInfo("[ZombieEvent] patched BotMover.SetSprintSpeed (min " + MinSprintSpeed.Value + ")");
                }

                // Slow 决策里的 goToEnemy(8)/goToEnemyZigZag(26) 改成 runToEnemyZigZag(25)：直线跑过去，别绕圈。
                // GetSlowMeleeZombieDecision 在 InfectedAttackLayer 上（返回 goToEnemyZigZag）。
                var attackLayer = AccessTools.TypeByName("InfectedAttackLayer") ?? strategyType;
                var slowDecision = attackLayer == null ? null : AccessTools.Method(attackLayer, "GetSlowMeleeZombieDecision");
                if (slowDecision == null)
                {
                    Log.LogWarning("[ZombieEvent] missing InfectedAttackLayer.GetSlowMeleeZombieDecision");
                }
                else
                {
                    harmony.Patch(slowDecision, postfix: new HarmonyMethod(typeof(DshZombieEventPlugin).GetMethod(nameof(Straighten), BindingFlags.Static | BindingFlags.NonPublic)));
                    Log.LogInfo("[ZombieEvent] patched InfectedAttackLayer.GetSlowMeleeZombieDecision (run chase)");
                }

                var infectionEffect = ZombieInfectionType();
                var ahcType = AccessTools.TypeByName("EFT.HealthSystem.ActiveHealthController");
                var hasAddEffect = false;
                if (ahcType != null)
                {
                    foreach (var m in ahcType.GetMethods(BindingFlags.Public | BindingFlags.Instance))
                    {
                        if (m.Name == "AddEffect" && m.IsGenericMethodDefinition && m.GetParameters().Length == 6)
                        {
                            hasAddEffect = true;
                            break;
                        }
                    }
                }

                Log.LogInfo("[ZombieEvent] infection gate ready (effect=" + (infectionEffect != null) + ", AddEffect=" + hasAddEffect + ")");
                Log.LogInfo("[ZombieEvent] ready");
            }
            catch (Exception ex)
            {
                Log.LogError("[ZombieEvent] init failed: " + ex);
            }
        }

        private static void Gate()
        {
            try
            {
                var controller = AccessTools.TypeByName("BotInternalSettingsController");
                var coreField = controller?.GetField("Core", BindingFlags.Public | BindingFlags.Static);
                var core = coreField?.GetValue(null);
                if (core == null)
                {
                    WarnMissing("BotInternalSettingsController.Core not found");
                    return;
                }

                var flagField = core.GetType().GetField("ACTIVE_HALLOWEEN_ZOMBIES_EVENT", BindingFlags.Public | BindingFlags.Instance);
                if (flagField == null)
                {
                    WarnMissing("ACTIVE_HALLOWEEN_ZOMBIES_EVENT not found");
                    return;
                }

                if (!(bool)flagField.GetValue(core))
                {
                    flagField.SetValue(core, true);
                    Log.LogInfo("[ZombieEvent] ACTIVE_HALLOWEEN_ZOMBIES_EVENT -> true");
                }
            }
            catch (Exception ex)
            {
                WarnMissing(ex.Message);
            }
        }

        // BotSpawner.CheckOnMax(int wantSpawn, out int toDelay, out int toSpawn, bool calcOnlySimpleBots = false)
        // 原逻辑：MaxBots 用满就把 wantSpawn 全部塞进 toDelay（推迟刷新）→ 僵尸被卡住。
        // 僵尸主机上直接全量放行。
        private static bool NoBotCap(int wantSpawn, out int toDelay, out int toSpawn)
        {
            toDelay = 0;
            toSpawn = wantSpawn;
            return false;
        }

        private static bool _modeLogged;

        private static void ForceFastZombie(ref InfectedAttackLayer.EZombieMode __result)
        {
            if (__result == InfectedAttackLayer.EZombieMode.Fast)
            {
                return;
            }

            var was = __result;
            __result = InfectedAttackLayer.EZombieMode.Fast;
            if (!_modeLogged)
            {
                _modeLogged = true;
                Log.LogInfo("[ZombieEvent] zombie mode " + was + " -> Fast (move speed 1, melee)");
            }
        }

        // ---------- 奔跑速度 ----------

        private static void SprintSpeedPrefix(BotMover __instance, ref float val)
        {
            var player = __instance == null ? null : __instance._player;
            if (player == null || !IsInfected(player))
            {
                return;
            }

            if (val < MinSprintSpeed.Value)
            {
                val = MinSprintSpeed.Value;
            }
        }

        // ---------- 直线追 ----------

        private static void Straighten(ref AICoreActionResult<BotLogicDecision, CoreActionResultParams> __result)
        {
            var action = (int)__result.Action;
            // 8 = goToEnemy, 26 = goToEnemyZigZag -> 25 = runToEnemyZigZag
            if (action == 8 || action == 26)
            {
                __result.Action = BotLogicDecision.runToEnemyZigZag;
            }
        }

        // ---------- 近战驱动（不依赖 AI 决策，照 Roaming Zombies 的做法） ----------

        private const string FallbackKnife = "57e26ea9245977162a14b789";

        private float _tick;
        private bool _driveLogged;
        private bool _driveWarned;
        private bool _kickLogged;
        private float _lastInfection = -999f;
        private bool _infectionLogged;
        private readonly Dictionary<int, float> _lastKick = new Dictionary<int, float>();

        private void Update()
        {
            _tick -= Time.deltaTime;
            if (_tick > 0f)
            {
                return;
            }

            _tick = Mathf.Max(0.05f, MeleeKickInterval.Value);

            try
            {
                if (Enabled.Value && DriveMelee.Value)
                {
                    DriveMeleeOnce();
                }

                if (ApplyInfection.Value)
                {
                    DriveInfectionOnce();
                }
            }
            catch (Exception ex)
            {
                if (!_driveWarned)
                {
                    _driveWarned = true;
                    Log.LogWarning("[ZombieEvent] drive: " + ex.Message);
                }
            }
        }

        private void DriveMeleeOnce()
        {
            var gameWorld = Singleton<GameWorld>.Instance;
            var botGame = Singleton<IBotGame>.Instance;
            if (gameWorld == null || botGame == null || botGame.BotsController == null)
            {
                return;
            }

            var ownerSource = botGame.BotsController.Bots == null ? null : botGame.BotsController.Bots.BotOwners;
            var players = gameWorld.AllAlivePlayersList;
            if (ownerSource == null || players == null)
            {
                return;
            }

            var owners = ownerSource as IList<BotOwner> ?? new List<BotOwner>(ownerSource);

            var rangeSqr = (float)(MeleeRange.Value * MeleeRange.Value);
            var zombies = 0;
            var kicked = 0;

            for (var i = 0; i < owners.Count; i++)
            {
                var bot = owners[i];
                var zombie = bot == null ? null : bot.GetPlayer;
                if (zombie == null || !IsInfected(zombie))
                {
                    continue;
                }

                zombies++;

                Player target = null;
                var best = float.MaxValue;
                for (var j = 0; j < players.Count; j++)
                {
                    var p = players[j];
                    if (p == null || p.HealthController == null || !p.HealthController.IsAlive || p == zombie || IsInfected(p))
                    {
                        continue;
                    }

                    var d = (p.Position - zombie.Position).sqrMagnitude;
                    if (d < best)
                    {
                        best = d;
                        target = p;
                    }
                }

                if (target == null || best > rangeSqr)
                {
                    continue;
                }

                var knife = zombie.HandsController as IKnifeController;
                if (knife == null)
                {
                    TryGiveKnife(zombie);
                    continue;
                }

                var id = zombie.GetInstanceID();
                float last;
                if (_lastKick.TryGetValue(id, out last) && Time.time - last < 0.8f)
                {
                    continue;
                }

                if (knife.MakeKnifeKick())
                {
                    _lastKick[id] = Time.time;
                    kicked++;
                }
            }

            if (!_driveLogged && zombies > 0)
            {
                _driveLogged = true;
                Log.LogInfo("[ZombieEvent] melee drive active (" + zombies + " infected tracked)");
            }

            if (!_kickLogged && kicked > 0)
            {
                _kickLogged = true;
                Log.LogInfo("[ZombieEvent] first forced melee kick landed");
            }
        }

        private static void TryGiveKnife(Player player)
        {
            var equipment = player.InventoryController != null
                ? player.InventoryController.Inventory?.Equipment
                : player.Profile?.Inventory?.Equipment;
            if (equipment == null)
            {
                return;
            }

            var slot = equipment.GetSlot(EquipmentSlot.Scabbard);
            if (slot == null)
            {
                return;
            }

            var knife = slot.ContainedItem == null ? null : slot.ContainedItem.GetItemComponent<KnifeComponent>();
            if (knife == null)
            {
                var factory = Singleton<ItemFactory>.Instance;
                if (factory == null)
                {
                    return;
                }

                var item = factory.CreateItem(MongoID.Generate(false), FallbackKnife, (UnparsedData)null);
                if (item == null)
                {
                    return;
                }

                slot.AddWithoutRestrictions(item);
                knife = item.GetItemComponent<KnifeComponent>();
                if (knife == null)
                {
                    return;
                }
            }

            player.Proceed(knife, (Callback<IKnifeController>)delegate (Result<IKnifeController> r)
            {
            }, true);
        }

        // ---------- 感染 ----------

        private static Type _infectionType;
        private static bool _infectionTypeResolved;

        private static Type ZombieInfectionType()
        {
            if (_infectionTypeResolved)
            {
                return _infectionType;
            }

            _infectionTypeResolved = true;
            foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
            {
                Type[] types;
                try
                {
                    types = asm.GetTypes();
                }
                catch
                {
                    continue;
                }

                foreach (var t in types)
                {
                    if (t != null && t.Name == "ZombieInfection" && t.FullName != null && t.FullName.EndsWith("ZombieInfection"))
                    {
                        _infectionType = t;
                        Log.LogInfo("[ZombieEvent] infection effect type = " + t.FullName);
                        return _infectionType;
                    }
                }
            }

            Log.LogWarning("[ZombieEvent] ZombieInfection effect type not found");
            return null;
        }

        private void DriveInfectionOnce()
        {
            if (Time.time - _lastInfection < InfectionCooldown.Value)
            {
                return;
            }

            var gameWorld = Singleton<GameWorld>.Instance;
            var me = gameWorld == null ? null : gameWorld.MainPlayer;
            if (me == null || me.HealthController == null || !me.HealthController.IsAlive)
            {
                return;
            }

            var botGame = Singleton<IBotGame>.Instance;
            if (botGame == null || botGame.BotsController == null || botGame.BotsController.Bots == null)
            {
                return;
            }

            var ownerSource = botGame.BotsController.Bots.BotOwners;
            if (ownerSource == null)
            {
                return;
            }

            var rangeSqr = (float)(InfectionRange.Value * InfectionRange.Value);
            foreach (var bot in ownerSource)
            {
                var zombie = bot == null ? null : bot.GetPlayer;
                if (zombie == null || zombie == me || !IsInfected(zombie))
                {
                    continue;
                }

                if ((zombie.Position - me.Position).sqrMagnitude > rangeSqr)
                {
                    continue;
                }

                if (ApplyInfectionEffect(me))
                {
                    _lastInfection = Time.time;
                    if (!_infectionLogged)
                    {
                        _infectionLogged = true;
                        Log.LogInfo("[ZombieEvent] zombie bite -> infection applied");
                    }
                }

                return;
            }
        }

        private static bool ApplyInfectionEffect(Player player)
        {
            var effectType = ZombieInfectionType();
            var controller = player.HealthController;
            if (effectType == null || controller == null)
            {
                return false;
            }

            MethodInfo addEffect = null;
            var methods = controller.GetType().GetMethods(BindingFlags.Public | BindingFlags.Instance);
            foreach (var m in methods)
            {
                if (m.Name == "AddEffect" && m.IsGenericMethodDefinition && m.GetParameters().Length == 6)
                {
                    addEffect = m;
                    break;
                }
            }

            if (addEffect == null)
            {
                return false;
            }

            try
            {
                addEffect.MakeGenericMethod(effectType).Invoke(controller, new object[] { EBodyPart.Head, null, null, null, null, null });
                return true;
            }
            catch (Exception ex)
            {
                var inner = ex.InnerException ?? ex;
                Log.LogWarning("[ZombieEvent] infection apply failed: " + inner.Message);
                return false;
            }
        }

        internal static bool IsInfected(Player player)
        {
            var role = player.Profile?.Info?.Settings;
            if (role == null)
            {
                return false;
            }

            var name = role.Role.ToString().ToLowerInvariant();
            return name.Contains("infected") || name.Contains("zombie");
        }

        private static void AfterDoSpawn(object __0)
        {
            try
            {
                var remains = __0?.GetType().GetField("spawnsRemains")?.GetValue(__0);
                Log.LogInfo("[ZombieEvent] crowd spawn fired (spawnsRemains=" + remains + ")");
            }
            catch
            {
                Log.LogInfo("[ZombieEvent] crowd spawn fired");
            }
        }

        private static void BeforeStartPursuit(object __1)
        {
            try
            {
                var id = __1?.GetType().GetProperty("ProfileId")?.GetValue(__1);
                Log.LogInfo("[ZombieEvent] pursuit started -> " + id);
            }
            catch
            {
                Log.LogInfo("[ZombieEvent] pursuit started");
            }
        }

        private static void WarnMissing(string message)
        {
            if (_warnedMissing)
            {
                return;
            }

            _warnedMissing = true;
            Log.LogWarning("[ZombieEvent] " + message);
        }
    }
}
