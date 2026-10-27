using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using HarmonyLib;
using SemanticVersioning;
using SPTarkov.Common.Models.Logging;
using SPTarkov.DI.Annotations;
using SPTarkov.Server.Core.DI;
using SPTarkov.Server.Core.Models.Common;
using SPTarkov.Server.Core.Models.Eft.Common;
using SPTarkov.Server.Core.Models.Spt.Mod;

namespace VanillaZombiePerHost;

/// <summary>SPT 4.x 要求：每个服务端 mod 必须有实现 IModMetadata 的类型。</summary>
public record ModMetadata : IModMetadata
{
    public string ModGuid { get; init; } = "com.dsh.vanillazombieperhost";
    public string Name { get; init; } = "Vanilla Zombie Per Host";
    public string Author { get; init; } = "DSH";
    public List<string> Contributors { get; init; } = new();
    public SemanticVersioning.Version Version { get; init; } = new(2, 0, 0);
    public SemanticVersioning.Range SptVersion { get; init; } = new(">=4.0.0");
    public List<string> Incompatibilities { get; init; } = new();
    public Dictionary<string, SemanticVersioning.Range> ModDependencies { get; init; } = new();
    public string Url { get; init; } = "";
    public bool HasPrepatcher { get; init; } = false;
    public string License { get; init; } = "MIT";
}

/// <summary>
/// 原版万圣节僵尸，按主机分服：
///   1) 只有 zombieHosts 里的主机（1号无头）开战时，把原版
///      seasonalevents.json 里 eventBossSpawns.halloweenzombies 的僵尸波注入该局地图；
///      注入时把「触发器波/16分钟后波」改写成「延迟 N 秒、每 M 秒一波」的确定波次，
///      不依赖任何触发器 ⇒ 一定会刷。
///   2) 其它主机（普通对局、自己打）在 GenerateLocationAndLoot 里把僵尸波全部剥掉。
///   3) 不用 SPT 的 ConfigureZombies / AddEventBossesToMaps，避免触发万圣节任务、
///      召唤事件、全局敌友表、地图 Boss 替换；只做「僵尸波」这一件事。
///
/// 客户端还需要 SainZombieFix（让 SAIN 不接管 infected 角色）才能让僵尸走原版近战 AI。
/// </summary>
[Injectable]
public class VanillaZombiePerHostPlugin : IOnLoad
{
    private static ISptLogger<VanillaZombiePerHostPlugin> Log;
    private static readonly HashSet<string> ZombieHosts = new(StringComparer.OrdinalIgnoreCase);
    private static readonly HashSet<string> LoggedHosts = new();
    private static readonly Dictionary<string, List<BossLocationSpawn>> VanillaWaves =
        new(StringComparer.OrdinalIgnoreCase);

    private static Settings _cfg = new();

    public VanillaZombiePerHostPlugin(ISptLogger<VanillaZombiePerHostPlugin> logger)
    {
        Log = logger;
    }

    public Task OnLoadAsync(CancellationToken cancellationToken)
    {
        LoadConfig();
        LoadVanillaWaves();

        var type = AccessTools.TypeByName("SPTarkov.Server.Core.Services.InRaid.LocationLifecycleService");
        if (type == null)
        {
            Log?.Error("[VanillaZombie] type LocationLifecycleService not found");
            return Task.CompletedTask;
        }

        var method = AccessTools.Method(type, "GenerateLocationAndLoot");
        if (method == null)
        {
            Log?.Error("[VanillaZombie] method GenerateLocationAndLoot not found");
            return Task.CompletedTask;
        }

        var harmony = new Harmony("com.dsh.vanillazombieperhost");
        harmony.Patch(method, postfix: new HarmonyMethod(typeof(VanillaZombiePerHostPlugin)
            .GetMethod(nameof(AfterGenerate), BindingFlags.Static | BindingFlags.NonPublic)));

        Log?.Info($"[VanillaZombie] v2.0.0 ready: mode={_cfg.Mode}, zombieHosts=[{string.Join(",", ZombieHosts)}], " +
                  $"vanillaMaps={VanillaWaves.Count} ({string.Join(";", VanillaWaves.Select(kv => kv.Key + "=" + kv.Value.Count))}), " +
                  $"firstDelay={_cfg.FirstWaveDelaySeconds}s, interval={_cfg.WaveIntervalSeconds}s, wavesPerMap={_cfg.WavesPerMap}, " +
                  $"entriesPerWave={_cfg.EntriesPerWave}, escort={_cfg.EscortAmount}");
        return Task.CompletedTask;
    }

    // ---------- 配置 ----------

    internal sealed class Settings
    {
        public string Mode { get; set; } = "waves";          // waves | vanilla | off
        public int FirstWaveDelaySeconds { get; set; } = 120;
        public int WaveIntervalSeconds { get; set; } = 300;
        public int WavesPerMap { get; set; } = 6;
        public int EntriesPerWave { get; set; } = 4;
        public string EscortAmount { get; set; } = "2-3";

        // vanilla 模式：每条波凑到 PerWave 只（1 主 + PerWave-1 护卫）
        public int PerWave { get; set; } = 5;

        // 僵尸宿主的地图感染度，>0 才会启动原版"成群刷在玩家附近"的 crowd 系统
        public int InfectionPercentage { get; set; } = 100;

        // 打开 crowd 后原版会在开局把 InfectedSpawn10..100 全部触发一遍（每图 19 波同时爆），
        // 这里默认丢掉这些触发波，只保留定时波 + crowd 在玩家附近凭空刷。
        public bool DropTriggerWaves { get; set; } = true;
    }

    private static void LoadConfig()
    {
        try
        {
            var dir = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location);
            var file = Path.Combine(dir!, "config.json");
            if (!File.Exists(file))
            {
                ZombieHosts.Add("6acb08501f444127e41896e9");
                File.WriteAllText(file, "{\n  \"zombieHosts\": [\"6acb08501f444127e41896e9\"],\n  \"mode\": \"waves\",\n  \"firstWaveDelaySeconds\": 120,\n  \"waveIntervalSeconds\": 300,\n  \"wavesPerMap\": 6,\n  \"entriesPerWave\": 4,\n  \"escortAmount\": \"2-3\",\n  \"perWave\": 5,\n  \"infectionPercentage\": 100,\n  \"dropTriggerWaves\": true\n}\n");
                return;
            }

            using var doc = JsonDocument.Parse(File.ReadAllText(file));
            var root = doc.RootElement;

            if (root.TryGetProperty("zombieHosts", out var arr) && arr.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in arr.EnumerateArray())
                {
                    var v = item.GetString();
                    if (!string.IsNullOrWhiteSpace(v))
                    {
                        ZombieHosts.Add(v);
                    }
                }
            }

            _cfg = new Settings
            {
                Mode = Str(root, "mode", _cfg.Mode),
                FirstWaveDelaySeconds = Int(root, "firstWaveDelaySeconds", _cfg.FirstWaveDelaySeconds),
                WaveIntervalSeconds = Int(root, "waveIntervalSeconds", _cfg.WaveIntervalSeconds),
                WavesPerMap = Int(root, "wavesPerMap", _cfg.WavesPerMap),
                EntriesPerWave = Int(root, "entriesPerWave", _cfg.EntriesPerWave),
                EscortAmount = Str(root, "escortAmount", _cfg.EscortAmount),
                PerWave = Int(root, "perWave", _cfg.PerWave),
                InfectionPercentage = Int(root, "infectionPercentage", _cfg.InfectionPercentage),
                DropTriggerWaves = Bool(root, "dropTriggerWaves", _cfg.DropTriggerWaves),
            };

            if (ZombieHosts.Count == 0)
            {
                ZombieHosts.Add("6acb08501f444127e41896e9");
            }
        }
        catch (Exception ex)
        {
            Log?.Error($"[VanillaZombie] config load failed: {ex}");
        }
    }

    private static string Str(JsonElement e, string name, string fallback)
    {
        return e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? (v.GetString() ?? fallback) : fallback;
    }

    private static int Int(JsonElement e, string name, int fallback)
    {
        return e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetInt32() : fallback;
    }

    private static bool Bool(JsonElement e, string name, bool fallback)
    {
        if (!e.TryGetProperty(name, out var v))
        {
            return fallback;
        }

        return v.ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            JsonValueKind.String => bool.TryParse(v.GetString(), out var b) ? b : fallback,
            _ => fallback,
        };
    }

    // ---------- 读原版僵尸波数据 ----------

    private static void LoadVanillaWaves()
    {
        try
        {
            var file = FindSeasonalEventsFile();
            if (file == null)
            {
                Log?.Error("[VanillaZombie] seasonalevents.json not found, vanilla waves empty");
                return;
            }

            using var doc = JsonDocument.Parse(File.ReadAllText(file));
            if (!doc.RootElement.TryGetProperty("eventBossSpawns", out var ebs)
                || !ebs.TryGetProperty("halloweenzombies", out var hz)
                || hz.ValueKind != JsonValueKind.Object)
            {
                Log?.Error($"[VanillaZombie] eventBossSpawns.halloweenzombies missing in {file}");
                return;
            }

            foreach (var mapProp in hz.EnumerateObject())
            {
                var list = new List<BossLocationSpawn>();
                foreach (var entry in mapProp.Value.EnumerateArray())
                {
                    list.Add(FromJson(entry));
                }

                if (list.Count > 0)
                {
                    VanillaWaves[mapProp.Name] = list;
                }
            }

            Log?.Info($"[VanillaZombie] vanilla zombie waves loaded from {file}");
        }
        catch (Exception ex)
        {
            Log?.Error($"[VanillaZombie] read seasonalevents failed: {ex}");
        }
    }

    private static string FindSeasonalEventsFile()
    {
        var probes = new List<string>();
        void Add(string baseDir)
        {
            if (string.IsNullOrEmpty(baseDir))
            {
                return;
            }

            probes.Add(Path.Combine(baseDir, "SPT_Data", "configs", "seasonalevents.json"));
            probes.Add(Path.Combine(baseDir, "..", "SPT_Data", "configs", "seasonalevents.json"));
        }

        Add(AppContext.BaseDirectory);
        Add(Directory.GetCurrentDirectory());

        foreach (var p in probes)
        {
            try
            {
                var full = Path.GetFullPath(p);
                if (File.Exists(full))
                {
                    return full;
                }
            }
            catch
            {
                // ignore
            }
        }

        return null;
    }

    private static BossLocationSpawn FromJson(JsonElement e)
    {
        return new BossLocationSpawn
        {
            BossChance = Num(e, "BossChance"),
            BossDifficulty = Txt(e, "BossDifficult") ?? Txt(e, "BossDifficulty"),
            BossEscortAmount = Txt(e, "BossEscortAmount"),
            BossEscortDifficulty = Txt(e, "BossEscortDifficult") ?? Txt(e, "BossEscortDifficulty"),
            BossEscortType = Txt(e, "BossEscortType"),
            BossName = Txt(e, "BossName"),
            IsBossPlayer = Bool(e, "BossPlayer"),
            BossZone = Txt(e, "BossZone"),
            IsRandomTimeSpawn = Bool(e, "RandomTimeSpawn"),
            Time = Num(e, "Time"),
            TriggerId = Txt(e, "TriggerId"),
            TriggerName = Txt(e, "TriggerName"),
            Delay = Num(e, "Delay"),
            ForceSpawn = Bool(e, "ForceSpawn"),
            IgnoreMaxBots = Bool(e, "IgnoreMaxBots"),
            SptId = Txt(e, "SptId"),
            SpawnMode = StrList(e, "SpawnMode"),
        };
    }

    private static double? Num(JsonElement e, string name)
    {
        if (!e.TryGetProperty(name, out var v))
        {
            return null;
        }

        return v.ValueKind switch
        {
            JsonValueKind.Number => v.GetDouble(),
            JsonValueKind.String => double.TryParse(v.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var d) ? d : null,
            _ => null,
        };
    }

    private static bool? Bool(JsonElement e, string name)
    {
        if (!e.TryGetProperty(name, out var v))
        {
            return null;
        }

        return v.ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            _ => null,
        };
    }

    private static string Txt(JsonElement e, string name)
    {
        return e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
    }

    private static List<string> StrList(JsonElement e, string name)
    {
        if (!e.TryGetProperty(name, out var v) || v.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        return v.EnumerateArray().Where(x => x.ValueKind == JsonValueKind.String).Select(x => x.GetString()).ToList();
    }

    // ---------- 注入 / 剥离 ----------

    private static void AfterGenerate(MongoId __0, string __1, ref LocationBase __result)
    {
        try
        {
            if (__result == null)
            {
                return;
            }

            var host = __0.ToString();
            var map = NormalizeMap(__1);
            var isZombieHost = ZombieHosts.Contains(host);

            // 复制一份再改，避免污染 LocationTable 里的缓存对象（否则会跨局残留/串到普通局）
            var waves = new List<BossLocationSpawn>(__result.BossLocationSpawn ?? new List<BossLocationSpawn>());
            var existingZombieWaves = waves.Where(IsZombieWave).ToList();

            if (LoggedHosts.Add(host + "|" + map))
            {
                Log?.Info($"[VanillaZombie] host={host} map={__1} -> total waves={waves.Count}, zombie waves={existingZombieWaves.Count}, zombieHost={isZombieHost}, mode={_cfg.Mode}");
            }

            if (!isZombieHost || _cfg.Mode == "off")
            {
                if (existingZombieWaves.Count > 0)
                {
                    __result.BossLocationSpawn = waves.Where(w => !IsZombieWave(w)).ToList();
                    Log?.Info($"[VanillaZombie] stripped {existingZombieWaves.Count} zombie wave(s) for non-zombie host {host} on {map}");
                }

                return;
            }

            // 僵尸宿主：写入地图感染度，原版 crowd（靠近玩家成群刷新）才会真正生效
            ApplyInfectionPercentage(__result);

            if (!VanillaWaves.TryGetValue(map, out var source) || source.Count == 0)
            {
                Log?.Warning($"[VanillaZombie] no vanilla zombie wave data for map '{__1}', skip injection");
                return;
            }

            if (_cfg.Mode == "vanilla")
            {
                if (existingZombieWaves.Count > 0)
                {
                    return;
                }

                // 结构与触发/时间照原版，只把"每条波 1 只"补成 PerWave 只（用 escort 补齐）
                var raw = BuildVanillaWaves(source);
                foreach (var w in raw)
                {
                    waves.Add(w);
                }

                Log?.Info($"[VanillaZombie] injected {raw.Count} vanilla zombie wave(s) perWave={_cfg.PerWave} infection={_cfg.InfectionPercentage} for host={host} map={__1}");
                __result.BossLocationSpawn = waves;
                return;
            }

            if (existingZombieWaves.Count > 0)
            {
                return;
            }

            var injected = BuildWaves(source);
            foreach (var w in injected)
            {
                waves.Add(w);
            }

            Log?.Info($"[VanillaZombie] injected {injected.Count} zombie wave(s) for host={host} map={__1} " +
                      $"(first at +{_cfg.FirstWaveDelaySeconds}s, every {_cfg.WaveIntervalSeconds}s, ~{_cfg.EntriesPerWave} entries/wave, escort={_cfg.EscortAmount})");
            __result.BossLocationSpawn = waves;
        }
        catch (Exception ex)
        {
            Log?.Error($"[VanillaZombie] injection failed: {ex}");
        }
    }

    // InfectionPercentage > 0 才会让客户端 CalcBotsCanSpawnedPerCrowd() 返回非 0，
    // 原版"靠近玩家成群刷新"的 crowd 系统才会真的刷僵尸出来。
    private static void ApplyInfectionPercentage(LocationBase location)
    {
        if (_cfg.InfectionPercentage <= 0)
        {
            return;
        }

        try
        {
            var events = location.GetType().GetProperty("Events")?.GetValue(location);
            if (events == null)
            {
                Log?.Warning("[VanillaZombie] location.Events is null, skip infection percentage");
                return;
            }

            var halloween = events.GetType().GetProperty("Halloween2024")?.GetValue(events);
            if (halloween == null)
            {
                Log?.Warning("[VanillaZombie] Events.Halloween2024 is null, skip infection percentage");
                return;
            }

            var prop = halloween.GetType().GetProperty("InfectionPercentage");
            if (prop == null || !prop.CanWrite)
            {
                Log?.Warning("[VanillaZombie] Halloween2024.InfectionPercentage is not writable");
                return;
            }

            var targetType = Nullable.GetUnderlyingType(prop.PropertyType) ?? prop.PropertyType;
            prop.SetValue(halloween, Convert.ChangeType(_cfg.InfectionPercentage, targetType));
            Log?.Info($"[VanillaZombie] infection percentage -> {_cfg.InfectionPercentage}");
        }
        catch (Exception ex)
        {
            Log?.Error($"[VanillaZombie] set infection percentage failed: {ex.Message}");
        }
    }

    private static List<BossLocationSpawn> BuildVanillaWaves(List<BossLocationSpawn> source)
    {
        var pool = source.Where(IsZombieWave).ToList();
        if (_cfg.DropTriggerWaves)
        {
            // crowd 打开后开局会把 InfectedSpawn10..100 一次性全触发（每图 19 波同时刷），太猛且易崩无头
            pool = pool.Where(w => string.IsNullOrWhiteSpace(w.TriggerId)).ToList();
        }

        var result = pool.Select(Clone).ToList();

        // 原版定时波（无 TriggerId）全部压在 Time=960 一起爆；只把它们的出现时间前移/摊开，
        // 触发波（TriggerName=botEvent, TriggerId=InfectedSpawn10..100）在保持原样的模式下不动。
        var index = 0;
        var interval = Math.Max(0, _cfg.WaveIntervalSeconds);
        foreach (var wave in result)
        {
            // 每条波补到 PerWave 只：1 个本体 + (PerWave-1) 个同类型护卫
            var escort = Math.Max(0, _cfg.PerWave - 1);
            if (escort > 0)
            {
                wave.BossEscortAmount = escort.ToString();
                if (string.IsNullOrWhiteSpace(wave.BossEscortType))
                {
                    wave.BossEscortType = wave.BossName;
                }
            }

            if (!string.IsNullOrWhiteSpace(wave.TriggerId))
            {
                continue;
            }

            wave.Time = _cfg.FirstWaveDelaySeconds + (index * interval);
            wave.Delay = 0;
            wave.IsRandomTimeSpawn = false;
            index++;
        }

        return result;
    }

    private static List<BossLocationSpawn> BuildWaves(List<BossLocationSpawn> source)
    {
        var result = new List<BossLocationSpawn>();
        var waves = Math.Max(1, _cfg.WavesPerMap);
        var entriesPerWave = Math.Max(1, _cfg.EntriesPerWave);

        // 原版列表里混有普通 assault 波（给万圣节当填充），这里只取真正的感染体，
        // 免得普通 scav 被当成"僵尸"刷出来。
        var pool = source.Where(IsZombieWave).ToList();
        if (pool.Count == 0)
        {
            pool = source;
        }

        for (var w = 0; w < waves; w++)
        {
            var time = _cfg.FirstWaveDelaySeconds + (w * Math.Max(0, _cfg.WaveIntervalSeconds));

            for (var i = 0; i < entriesPerWave; i++)
            {
                var seed = pool[(w * entriesPerWave + i) % pool.Count];
                var spawn = Clone(seed);
                spawn.BossChance = 10000;
                spawn.ForceSpawn = true;
                spawn.IgnoreMaxBots = true;
                spawn.TriggerId = null;
                spawn.TriggerName = null;
                spawn.IsRandomTimeSpawn = false;
                spawn.Time = time;
                spawn.Delay = 0;
                spawn.BossEscortAmount = string.IsNullOrWhiteSpace(_cfg.EscortAmount) ? "0" : _cfg.EscortAmount;
                result.Add(spawn);
            }
        }

        return result;
    }

    private static BossLocationSpawn Clone(BossLocationSpawn src)
    {
        return new BossLocationSpawn
        {
            BossChance = src.BossChance,
            BossDifficulty = src.BossDifficulty,
            BossEscortAmount = src.BossEscortAmount,
            BossEscortDifficulty = src.BossEscortDifficulty,
            BossEscortType = src.BossEscortType,
            BossName = src.BossName,
            IsBossPlayer = src.IsBossPlayer,
            BossZone = src.BossZone,
            IsRandomTimeSpawn = src.IsRandomTimeSpawn,
            ShowOnTarkovMap = src.ShowOnTarkovMap,
            ShowOnTarkovMapPvE = src.ShowOnTarkovMapPvE,
            Time = src.Time,
            TriggerId = src.TriggerId,
            TriggerName = src.TriggerName,
            Delay = src.Delay,
            DependKarma = src.DependKarma,
            DependKarmaPVE = src.DependKarmaPVE,
            ForceSpawn = src.ForceSpawn,
            IgnoreMaxBots = src.IgnoreMaxBots,
            Supports = src.Supports,
            SptId = src.SptId,
            SpawnMode = src.SpawnMode == null ? null : new List<string>(src.SpawnMode),
        };
    }

    private static string NormalizeMap(string map)
    {
        if (string.IsNullOrWhiteSpace(map))
        {
            return "";
        }

        var m = map.Trim().ToLowerInvariant();
        return m switch
        {
            "factory4_day" => "factory4_day",
            "factory4_night" => "factory4_night",
            "rezervbase" => "rezervbase",
            "sandbox" => "sandbox",
            "sandbox_high" => "sandbox_high",
            _ => m,
        };
    }

    private static bool IsZombieWave(BossLocationSpawn wave)
    {
        var name = (wave.BossName ?? "") + "|" + (wave.BossEscortType ?? "") + "|" + (wave.SptId ?? "");
        return name.IndexOf("infected", StringComparison.OrdinalIgnoreCase) >= 0
            || name.IndexOf("zombie", StringComparison.OrdinalIgnoreCase) >= 0;
    }
}
