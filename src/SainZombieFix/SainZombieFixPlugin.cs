using System;
using BepInEx;
using BepInEx.Logging;
using HarmonyLib;

namespace SainZombieFix;

/// <summary>
/// 让 SAIN 不要接管感染体（infectedAssault / infectedPmc / infectedCivil /
/// infectedLaborant / infectedTagilla）。
///
/// 问题：SAIN 的排除列表是硬编码的（BotSpawnController.IsExcluded 遍历一个静态
/// WildSpawnType 列表），感染体不在里面 ⇒ SAIN 把僵尸当普通枪手接管 ⇒
/// 僵尸失去原版近战层 ⇒ 表现为"站着不攻击"。
///
/// 修法：给 BotSpawnController.IsExcluded(WildSpawnType) 加后置，
/// 角色名含 "infected" 时强制返回 true ⇒ SAIN 跳过它们，僵尸回到原版 AI。
/// </summary>
[BepInPlugin(Guid, "SAIN Zombie Fix", "1.0.0")]
public class SainZombieFixPlugin : BaseUnityPlugin
{
    public const string Guid = "com.dsh.sainzombiefix";

    internal static ManualLogSource Log;

    private void Awake()
    {
        Log = Logger;

        var type = AccessTools.TypeByName("SAIN.Components.BotController.BotSpawnController");
        var method = type == null ? null : AccessTools.Method(type, "IsExcluded");
        if (method == null)
        {
            Log.LogWarning("[SainZombieFix] SAIN BotSpawnController.IsExcluded not found");
            return;
        }

        var harmony = new Harmony(Guid);
        harmony.Patch(method, postfix: new HarmonyMethod(typeof(SainZombieFixPlugin)
            .GetMethod(nameof(ForceExcludeInfected), System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic)));

        Log.LogInfo("[SainZombieFix] patched SAIN BotSpawnController.IsExcluded (infected bots now left to vanilla AI)");
    }

    // 用位置参数 __0 而不是按名字绑定：SAIN 4.5.1 的参数名是 "type"，
    // 旧写法用 "role" 会让 HarmonyX 直接抛
    // Parameter "role" not found in method ...IsExcluded(EFT.WildSpawnType type)
    private static void ForceExcludeInfected(EFT.WildSpawnType __0, ref bool __result)
    {
        if (__result)
        {
            return;
        }

        var name = __0.ToString();
        if (name.IndexOf("infected", StringComparison.OrdinalIgnoreCase) >= 0)
        {
            __result = true;
        }
    }
}
