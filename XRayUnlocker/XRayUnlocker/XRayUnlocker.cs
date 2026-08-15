using System.Drawing;
using System.Reflection;
using System.Text.Json;
using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Core.Attributes;
using CounterStrikeSharp.API.Core.Attributes.Registration;
using CounterStrikeSharp.API.Modules.Commands;
using CounterStrikeSharp.API.Modules.Cvars;
using CounterStrikeSharp.API.Modules.Extensions;
using CounterStrikeSharp.API.Modules.Memory;
using CounterStrikeSharp.API.Modules.Memory.DynamicFunctions;
using CounterStrikeSharp.API.Modules.Utils;

namespace XRayUnlocker;

/// <summary>
/// 本地透视 + 无敌插件 v1.8.0
/// 
/// 玩家透视（!x）：
///   创建不可见 prop_dynamic 幽灵发光实体跟随玩家模型，
///   通过 CheckTransmit 按玩家过滤，观战者不可见。
///
/// 无敌模式（!god）：
///   ——输入生效，与 CS2 内置 buddha 不同，
///   不掉血、不抖动、不减速，子弹正常命中，击中效果完整。
///   机制：OnPlayerTakeDamagePre 在引擎层面将伤害归零。
///
/// 暗金计数（!st）—— v1.6.0 重写为简单值模型：
///   移除实体绑定/购买追踪/认领判定三层机制，
///   改为 slot → designerName → value 直接映射，
///   玩家设过值的武器类型始终递增，无指纹断裂风险。
/// </summary>
[MinimumApiVersion(80)]
public class XRayUnlockerPlugin : BasePlugin
{
    public override string ModuleName => "XRayUnlocker";
    public override string ModuleVersion => "1.8.1";
    public override string ModuleAuthor => "CS2 Local Server";
    public override string ModuleDescription => "透视 !x + 无敌 !god + 暗金 !st + 防闪 !nf + 蹲起 !sc + 全图可穿 !wp + 穿墙无衰减 !wd + 自动急停 !cs + 魔法子弹 !mb + 掉落物透视 !dx + C4透视 !cx + 隐身 !inv + 秒下包秒拆弹 !fb + BOT数量解锁 + 暗金JSON持久化 !stattrak";

    // ==================== 玩家透视 ====================
    private readonly Dictionary<int, (CDynamicProp relay, CDynamicProp glow)> _playerGlows = new();
    private readonly HashSet<int> _xrayUsers = new();

    // ==================== 无敌模式 ====================
    private readonly HashSet<int> _godPlayers = new();
    // pawn.Index → player.Slot 快速映射（OnPlayerTakeDamagePre 参数是 pawn 不是 controller）
    private readonly Dictionary<uint, int> _pawnToSlot = new();

    // ==================== 防闪光 ====================
    private readonly HashSet<int> _noFlashPlayers = new();

    // ==================== 无限蹲起（无体力）====================
    private readonly HashSet<int> _noStaminaPlayers = new();  // 开启的玩家
    private bool _staminaCvarsSet;                             // 全局 cvar 是否已设

    // ==================== 穿墙功能：全图可穿 ====================
    private readonly HashSet<int> _fullPenPlayers = new();           // 全图可穿玩家

    // ==================== 穿墙功能：穿墙无伤害衰减 ====================
    private readonly HashSet<int> _noWallDmgReductionPlayers = new(); // 穿墙无伤害衰减玩家
    // 武器实体索引 → (原始穿透次数, 原始伤害倍率)，用于丢枪/换人时恢复默认属性
    private readonly Dictionary<uint, (int origPen, float origDmg)> _weaponOrigValues = new();

    // ==================== 自动急停（!cs）====================
    private readonly HashSet<int> _counterStrafePlayers = new();

    // ==================== 魔法子弹（!mb）====================
    // 开火时自动将子弹导向最近敌人（头部 65% / 身体 35%），瞄准容差 ±10°
    private readonly HashSet<int> _magicBulletPlayers = new();

    // ==================== 掉落物透视（!dx）====================
    // 创建 CDynamicProp 跟随地上武器（双实体 relay+glow），
    // 模型固定用 AK-47 世界模型（CS2 保证存在，不会 ERROR）
    private readonly HashSet<int> _dropXrayUsers = new();
    private readonly Dictionary<nint, (CDynamicProp relay, CDynamicProp glow)> _dropGlows = new();

    // ==================== C4透视（!cx）====================
    private readonly HashSet<int> _c4XrayUsers = new();
    private (CDynamicProp relay, CDynamicProp glow)? _c4GlowPair = null;
    private CPlantedC4? _c4Entity = null;
    private int _c4FlashTick;

    // ==================== 隐身（!inv）====================
    // 原理：玩家/武器/手套 alpha 置 0 + 影子强度归零，并在 CheckTransmit 中
    // 阻止其他玩家接收该玩家实体；设置 FL_NOTARGET 让 BOT 的目标感知失效。
    private readonly HashSet<int> _invisiblePlayers = new();
    private readonly Dictionary<CEntityInstance, int> _hiddenEntities = new(); // 隐藏实体 → 拥有者 slot
    private const uint FL_NOTARGET = 0x8000u; // m_fFlags 位：AI 不把该实体作为目标（对应 notarget）

    // ==================== 秒下包/秒拆弹（!fb）====================
    // 原理：拦截 bomb_beginplant / bomb_begindefuse 事件，
    // 将 C4 下包耗时（ArmedTime）或拆弹倒计时（DefuseCountDown）归零。
    private readonly HashSet<int> _fastBombPlayers = new();

    // ==================== BOT数量解锁 ====================
    private const int MaxPerTeam = 32;
    private readonly List<string> _createdSpawnGlobalnames = new();

    // ==================== 掉落物透视（!dx / dx）====================
    // 创建双实体（relay + glow）跟随地上武器，模型用 AK-47 世界模型（CS2 保证存在）

    private void OnDropXrayCommand(CCSPlayerController? player, CommandInfo info)
    {
        if (player == null || !player.IsValid) return;
        int slot = player.Slot;
        if (_dropXrayUsers.Contains(slot))
        {
            _dropXrayUsers.Remove(slot);
            DestroyAllDropGlows();
            player.PrintToChat(" [DropXray] 掉落物透视已关闭");
        }
        else
        {
            _dropXrayUsers.Add(slot);
            player.PrintToChat(" [DropXray] 掉落物透视已开启 - 地上武器/道具/拆弹器 淡黄边框");
        }
    }

    private void OnDropXrayConsoleCommand(CCSPlayerController? player, CommandInfo info)
    {
        if (player == null || !player.IsValid) return;
        int slot = player.Slot;
        if (info.ArgCount < 2) { player.PrintToChat(" [DropXray] 用法: dx 1 开启 / dx 0 关闭"); return; }
        if (int.TryParse(info.GetArg(1), out int val) && val == 0)
        { _dropXrayUsers.Remove(slot); DestroyAllDropGlows(); player.PrintToChat(" [DropXray] 掉落物透视已关闭"); }
        else
        { _dropXrayUsers.Add(slot); player.PrintToChat(" [DropXray] 掉落物透视已开启 - 地上武器/道具/拆弹器 淡黄边框"); }
    }

    private static readonly string[] AllWeaponDesignerNames =
    {
        "weapon_ak47", "weapon_m4a1", "weapon_m4a1_silencer", "weapon_aug", "weapon_sg556",
        "weapon_awp", "weapon_ssg08", "weapon_scar20", "weapon_g3sg1",
        "weapon_galilar", "weapon_famas", "weapon_deagle", "weapon_revolver",
        "weapon_elite", "weapon_fiveseven", "weapon_p250", "weapon_usp_silencer",
        "weapon_glock", "weapon_hkp2000", "weapon_cz75a", "weapon_tec9",
        "weapon_mp9", "weapon_mac10", "weapon_mp7", "weapon_mp5sd", "weapon_ump45",
        "weapon_p90", "weapon_bizon",
        "weapon_nova", "weapon_xm1014", "weapon_mag7", "weapon_sawedoff",
        "weapon_m249", "weapon_negev",
        "weapon_knife", "weapon_knife_t", "weapon_bayonet",
        "weapon_flashbang", "weapon_hegrenade", "weapon_smokegrenade",
        "weapon_decoy", "weapon_molotov", "weapon_incgrenade",
        "weapon_taser", "weapon_healthshot",
        "weapon_c4", "item_defuser", "item_cutters",
    };

    /// <summary>AK-47 世界模型路径（CS2 保证存在，用作所有掉落物的发光载体）</summary>
    private const string GlowMarkerModel = "models/weapons/w_rif_ak47.vmdl";

    /// <summary>为物品创建跟随的双实体发光组</summary>
    private static (CDynamicProp relay, CDynamicProp glow)? CreateItemGlow(CEntityInstance target, Color color)
    {
        if (!target.IsValid) return null;

        var relay = Utilities.CreateEntityByName<CDynamicProp>("prop_dynamic");
        if (relay == null || !relay.IsValid) return null;

        relay.SetModel(GlowMarkerModel);
        relay.Spawnflags = 256u;
        relay.RenderMode = RenderMode_t.kRenderNone;
        relay.DispatchSpawn();

        var glow = Utilities.CreateEntityByName<CDynamicProp>("prop_dynamic");
        if (glow == null || !glow.IsValid) { relay.Remove(); return null; }

        glow.SetModel(GlowMarkerModel);
        glow.Spawnflags = 256u;
        glow.Render = Color.FromArgb(1, 255, 255, 255);
        glow.DispatchSpawn();

        glow.Glow.GlowColorOverride = color;
        glow.Glow.GlowRange = 5000;
        glow.Glow.GlowRangeMin = 0;
        glow.Glow.GlowTeam = -1;
        glow.Glow.GlowType = 3;

        relay.AcceptInput("FollowEntity", target, relay, "!activator");
        glow.AcceptInput("FollowEntity", relay, glow, "!activator");

        return (relay, glow);
    }

    private void UpdateDropGlows()
    {
        var seenHandles = new HashSet<nint>();

        foreach (var designerName in AllWeaponDesignerNames)
        {
            foreach (var entity in Utilities.FindAllEntitiesByDesignerName<CBasePlayerWeapon>(designerName))
            {
                if (entity is not CBasePlayerWeapon weapon || !weapon.IsValid) continue;
                var owner = weapon.OwnerEntity?.Value;
                if (owner is { IsValid: true }) continue;

                nint handle = weapon.Handle;
                seenHandles.Add(handle);
                if (_dropGlows.ContainsKey(handle)) continue;

                try
                {
                    var pair = CreateItemGlow(weapon, Color.Gold);
                    if (pair != null) _dropGlows[handle] = pair.Value;
                }
                catch { }
            }
        }

        var stale = _dropGlows.Keys.Where(k => !seenHandles.Contains(k)).ToList();
        foreach (var key in stale)
        {
            if (_dropGlows.TryGetValue(key, out var pair))
            {
                if (pair.glow is { IsValid: true }) pair.glow.Remove();
                if (pair.relay is { IsValid: true }) pair.relay.Remove();
            }
            _dropGlows.Remove(key);
        }
    }

    private void DestroyAllDropGlows()
    {
        foreach (var pair in _dropGlows.Values)
        {
            if (pair.glow is { IsValid: true }) pair.glow.Remove();
            if (pair.relay is { IsValid: true }) pair.relay.Remove();
        }
        _dropGlows.Clear();
    }

    // ==================== C4透视（!cx / cx）====================

    private void OnC4XrayCommand(CCSPlayerController? player, CommandInfo info)
    {
        if (player == null || !player.IsValid) return;
        int slot = player.Slot;
        if (_c4XrayUsers.Contains(slot))
        {
            _c4XrayUsers.Remove(slot);
            DestroyC4Glow();
            player.PrintToChat(" [C4Xray] C4透视已关闭");
        }
        else
        {
            _c4XrayUsers.Add(slot);
            if (_c4Entity is { IsValid: true } && _c4GlowPair == null)
                _c4GlowPair = CreateItemGlow(_c4Entity, Color.Lime);
            player.PrintToChat(" [C4Xray] C4透视已开启 - 已激活C4 绿色闪烁边框");
        }
    }

    private void OnC4XrayConsoleCommand(CCSPlayerController? player, CommandInfo info)
    {
        if (player == null || !player.IsValid) return;
        int slot = player.Slot;
        if (info.ArgCount < 2) { player.PrintToChat(" [C4Xray] 用法: cx 1 开启 / cx 0 关闭"); return; }
        if (int.TryParse(info.GetArg(1), out int val) && val == 0)
        { _c4XrayUsers.Remove(slot); DestroyC4Glow(); player.PrintToChat(" [C4Xray] C4透视已关闭"); }
        else
        {
            _c4XrayUsers.Add(slot);
            if (_c4Entity is { IsValid: true } && _c4GlowPair == null)
                _c4GlowPair = CreateItemGlow(_c4Entity, Color.Lime);
            player.PrintToChat(" [C4Xray] C4透视已开启 - 已激活C4 绿色闪烁边框");
        }
    }

    private HookResult OnBombPlanted(EventBombPlanted @event, GameEventInfo info)
    {
        DestroyC4Glow();
        AddTimer(0.1f, () =>
        {
            var c4 = Utilities.FindAllEntitiesByDesignerName<CPlantedC4>("planted_c4").FirstOrDefault();
            if (c4 is not { IsValid: true }) return;
            _c4Entity = c4;
            _c4FlashTick = 0;
            if (_c4XrayUsers.Count > 0)
                _c4GlowPair = CreateItemGlow(c4, Color.Lime);
        });
        return HookResult.Continue;
    }

    private HookResult OnBombDefused(EventBombDefused @event, GameEventInfo info)
    {
        DestroyC4Glow();
        return HookResult.Continue;
    }

    private HookResult OnBombExploded(EventBombExploded @event, GameEventInfo info)
    {
        DestroyC4Glow();
        return HookResult.Continue;
    }

    private void DestroyC4Glow()
    {
        if (_c4GlowPair is { } pair)
        {
            if (pair.glow is { IsValid: true }) pair.glow.Remove();
            if (pair.relay is { IsValid: true }) pair.relay.Remove();
        }
        _c4GlowPair = null;
        _c4Entity = null;
    }

    // ==================== 暗金计数器 ====================
    // slot → DesignerName → Value（简化为直接值模型，无实体绑定、无购买追踪）
    // 玩家对某武器类型设过值后，该类型所有击杀始终递增，OnTick 每帧强制写回
    private readonly Dictionary<int, Dictionary<string, int>> _statTrakValues = new();

    // StatTrak JSON 持久化（跨会话保留击杀记录）
    private Dictionary<string, Dictionary<string, int>> _savedKillCounts = new();
    private string _stattrakJsonPath = string.Empty;

    // ==================== 暗金计数命令 ====================

    private static readonly MemoryFunctionWithReturn<nint, string, float, int> _setAttributeValueByName =
        new(GameData.GetSignature("CAttributeList::SetOrAddAttributeValueByName"));

    // ==================== 生命周期 ====================

    public override void Load(bool hotReload)
    {
        _stattrakJsonPath = Path.Combine(ModuleDirectory, "stattrak_data.json");
        LoadStatTrakData();

        RegisterListener<Listeners.OnTick>(OnTick);
        RegisterListener<Listeners.CheckTransmit>(OnCheckTransmit);
        RegisterListener<Listeners.OnPlayerTakeDamagePre>(OnPlayerTakeDamagePre);
        RegisterListener<Listeners.OnEntityCreated>(OnWeaponEntityCreated);
        RegisterEventHandler<EventPlayerSpawn>(OnPlayerSpawn);
        RegisterEventHandler<EventPlayerDisconnect>(OnPlayerDisconnect);
        RegisterEventHandler<EventRoundStart>(OnRoundStart);
        RegisterEventHandler<EventPlayerDeath>(OnPlayerDeathMagicFix, HookMode.Pre);
        RegisterEventHandler<EventPlayerDeath>(OnPlayerDeathPost, HookMode.Post);
        RegisterEventHandler<EventWeaponFire>(OnWeaponFireMagicBullet);
        RegisterEventHandler<EventBombPlanted>(OnBombPlanted);
        RegisterEventHandler<EventBombDefused>(OnBombDefused);
        RegisterEventHandler<EventBombExploded>(OnBombExploded);
        RegisterEventHandler<EventBombBeginplant>(OnBombBeginPlant);
        RegisterEventHandler<EventBombBegindefuse>(OnBombBeginDefuse);

        RegisterListener<Listeners.OnMapStart>(OnMapStart);
        AddCommandListener("bot_add", OnBotAddCommand, HookMode.Pre);

        AddCommand("css_x", "开关X光透视（敌我全体可见）", OnXRayCommand);
        AddCommand("css_god", "开关无敌模式（不掉血不抖动不减速）", OnGodCommand);
        AddCommand("x", "控制台透视开关: x 1 开启, x 0 关闭", OnXConsoleCommand);
        AddCommand("god", "控制台无敌开关: god 1 开启, god 0 关闭", OnGodConsoleCommand);
        AddCommand("css_st", "修改当前武器暗金计数器: st <数字>", OnStatTrakCommand);
        AddCommand("st", "控制台修改暗金计数器: st <数字>", OnStatTrakConsoleCommand);
        AddCommand("css_nf", "开关防闪光白屏", OnNoFlashCommand);
        AddCommand("nf", "控制台防闪光: nf 1 开启, nf 0 关闭", OnNoFlashConsoleCommand);
        AddCommand("css_sc", "开关无限蹲起（无体力无冷却）", OnSpamCrouchCommand);
        AddCommand("sc", "控制台无限蹲起: sc 1 开启, sc 0 关闭", OnSpamCrouchConsoleCommand);
        AddCommand("css_stattrak", "暗金JSON管理: stattrakshow [武器名] 或 stattraks<数字>设置基准", OnStattrakJsonCommand);
        AddCommand("css_wp", "开关全图可穿（子弹穿透任何掩体/材质/地板）", OnWallPenCommand);
        AddCommand("wp", "控制台全图可穿: wp 1 开启, wp 0 关闭", OnWallPenConsoleCommand);
        AddCommand("css_wd", "开关穿墙无伤害衰减（穿墙不减伤害，仅保留距离衰减）", OnWallDmgReductionCommand);
        AddCommand("wd", "控制台穿墙无衰减: wd 1 开启, wd 0 关闭", OnWallDmgReductionConsoleCommand);
        AddCommand("css_cs", "开关自动急停（松键瞬间停稳，无需反方向键）", OnCounterStrafeCommand);
        AddCommand("cs", "控制台自动急停: cs 1 开启, cs 0 关闭", OnCounterStrafeConsoleCommand);
        AddCommand("css_mb", "开关魔法子弹（瞄准大致方向自动命中最近敌人）", OnMagicBulletCommand);
        AddCommand("mb", "控制台魔法子弹: mb 1 开启, mb 0 关闭", OnMagicBulletConsoleCommand);
        AddCommand("css_dx", "开关掉落物透视（地上武器/道具/拆弹器 淡黄边框）", OnDropXrayCommand);
        AddCommand("dx", "控制台掉落物透视: dx 1 开启, dx 0 关闭", OnDropXrayConsoleCommand);
        AddCommand("css_cx", "开关C4透视（已激活C4 绿色闪烁边框）", OnC4XrayCommand);
        AddCommand("cx", "控制台C4透视: cx 1 开启, cx 0 关闭", OnC4XrayConsoleCommand);
        AddCommand("css_inv", "开关隐身（模型/枪械/影子消失，BOT 看不见）", OnInvisCommand);
        AddCommand("inv", "控制台隐身: inv 1 开启, inv 0 关闭", OnInvisConsoleCommand);
        AddCommand("css_fb", "开关秒下包/秒拆弹（0.1秒，不分阵营）", OnFastBombCommand);
        AddCommand("fb", "控制台秒下包/秒拆弹: fb 1 开启, fb 0 关闭", OnFastBombConsoleCommand);

        Console.WriteLine("[XRayUnlocker] v1.8.1 已加载 | !x !god !st !nf !sc !wp !wd !cs !mb !dx !cx !inv !fb !stattrak | BOT数量解锁已启用");

        if (hotReload)
        {
            AddTimer(0.5f, () =>
            {
                foreach (var p in Utilities.GetPlayers())
                {
                    if (p is not { IsValid: true }) continue;
                    CreatePlayerGlow(p);
                    RegisterPawnMapping(p);
                }
            });
            AddTimer(1.0f, () =>
            {
                EnsureSpawnPoints();
                ApplyAllLimits();
            });
        }
    }

    public override void Unload(bool hotReload)
    {
        DestroyAllPlayerGlows();
        _xrayUsers.Clear();
        _godPlayers.Clear();
        _noFlashPlayers.Clear();
        _noStaminaPlayers.Clear();
        _fullPenPlayers.Clear();
        _noWallDmgReductionPlayers.Clear();
        _counterStrafePlayers.Clear();
        _magicBulletPlayers.Clear();
        _pendingMagicBulletKills.Clear();
        _dropXrayUsers.Clear();
        _c4XrayUsers.Clear();
        DestroyAllDropGlows();
        DestroyC4Glow();
        // 恢复所有隐身玩家并清理
        foreach (var slot in _invisiblePlayers.ToList())
        {
            var p = Utilities.GetPlayerFromSlot(slot);
            if (p is { IsValid: true })
                RestoreVisibility(p);
        }
        _invisiblePlayers.Clear();
        _hiddenEntities.Clear();
        _fastBombPlayers.Clear();
        _weaponOrigValues.Clear();
        _pawnToSlot.Clear();
        _statTrakValues.Clear();
        RemoveCreatedSpawns();
        SaveStatTrakData();
        Console.WriteLine("[XRayUnlocker] 已卸载");
    }

    // ==================== 命令 ====================

    private void OnXRayCommand(CCSPlayerController? player, CommandInfo info)
    {
        if (player == null || !player.IsValid) return;
        int slot = player.Slot;

        if (_xrayUsers.Contains(slot))
        {
            _xrayUsers.Remove(slot);
            player.PrintToChat(" [XRay] 透视已关闭");
        }
        else
        {
            _xrayUsers.Add(slot);
            player.PrintToChat(" [XRay] 透视已开启 - 敌我全体可见");
        }
    }

    private void OnGodCommand(CCSPlayerController? player, CommandInfo info)
    {
        if (player == null || !player.IsValid) return;
        int slot = player.Slot;

        if (_godPlayers.Contains(slot))
        {
            _godPlayers.Remove(slot);
            player.PrintToChat(" [God] 无敌模式已关闭");
        }
        else
        {
            _godPlayers.Add(slot);
            SetupGodMode(player);
            player.PrintToChat(" [God] 无敌模式已开启 - 100HP 不掉血不抖动不减速");
        }
    }

    // ==================== 控制台命令（x 1/0, god 1/0）====================

    private void OnXConsoleCommand(CCSPlayerController? player, CommandInfo info)
    {
        if (player == null || !player.IsValid) return;
        int slot = player.Slot;

        if (info.ArgCount < 2)
        {
            player.PrintToChat(" [XRay] 用法: x 1 开启 / x 0 关闭");
            return;
        }

        if (int.TryParse(info.GetArg(1), out int val) && val == 0)
        {
            _xrayUsers.Remove(slot);
            player.PrintToChat(" [XRay] 透视已关闭");
        }
        else
        {
            _xrayUsers.Add(slot);
            player.PrintToChat(" [XRay] 透视已开启 - 敌我全体可见");
        }
    }

    private void OnGodConsoleCommand(CCSPlayerController? player, CommandInfo info)
    {
        if (player == null || !player.IsValid) return;
        int slot = player.Slot;

        if (info.ArgCount < 2)
        {
            player.PrintToChat(" [God] 用法: god 1 开启 / god 0 关闭");
            return;
        }

        if (int.TryParse(info.GetArg(1), out int val) && val == 0)
        {
            _godPlayers.Remove(slot);
            player.PrintToChat(" [God] 无敌模式已关闭");
        }
        else
        {
            _godPlayers.Add(slot);
            SetupGodMode(player);
            player.PrintToChat(" [God] 无敌模式已开启 - 100HP 不掉血不抖动不减速");
        }
    }

    // ==================== 防闪光 ====================

    private void OnNoFlashCommand(CCSPlayerController? player, CommandInfo info)
    {
        if (player == null || !player.IsValid) return;
        int slot = player.Slot;

        if (_noFlashPlayers.Contains(slot))
        {
            _noFlashPlayers.Remove(slot);
            player.PrintToChat(" [NoFlash] 防闪光已关闭");
        }
        else
        {
            _noFlashPlayers.Add(slot);
            player.PrintToChat(" [NoFlash] 防闪光已开启 - 闪光弹不白屏");
        }
    }

    private void OnNoFlashConsoleCommand(CCSPlayerController? player, CommandInfo info)
    {
        if (player == null || !player.IsValid) return;
        int slot = player.Slot;

        if (info.ArgCount < 2)
        {
            player.PrintToChat(" [NoFlash] 用法: nf 1 开启 / nf 0 关闭");
            return;
        }

        if (int.TryParse(info.GetArg(1), out int val) && val == 0)
        {
            _noFlashPlayers.Remove(slot);
            player.PrintToChat(" [NoFlash] 防闪光已关闭");
        }
        else
        {
            _noFlashPlayers.Add(slot);
            player.PrintToChat(" [NoFlash] 防闪光已开启 - 闪光弹不白屏");
        }
    }

    // ==================== 隐身（!inv / inv）====================
    // 原理：将玩家及其所有武器渲染 alpha 置 0、影子强度归零，
    // 同时在 CheckTransmit 中阻止其他玩家接收这些实体；
    // SpottedState 归零让 BOT 的目标感知也无法发现隐身玩家。

    private void OnInvisCommand(CCSPlayerController? player, CommandInfo info)
    {
        if (player == null || !player.IsValid) return;
        ToggleInvisibility(player);
    }

    private void OnInvisConsoleCommand(CCSPlayerController? player, CommandInfo info)
    {
        if (player == null || !player.IsValid) return;
        if (info.ArgCount < 2)
        {
            player.PrintToChat(" [Invis] 用法: inv 1 开启 / inv 0 关闭");
            return;
        }

        bool enable = !(int.TryParse(info.GetArg(1), out int val) && val == 0);
        if (enable == _invisiblePlayers.Contains(player.Slot)) return;

        if (enable) SetInvisible(player);
        else SetVisible(player);
    }

    private void ToggleInvisibility(CCSPlayerController player)
    {
        if (_invisiblePlayers.Contains(player.Slot)) SetVisible(player);
        else SetInvisible(player);
    }

    private void SetInvisible(CCSPlayerController player)
    {
        _invisiblePlayers.Add(player.Slot);
        ApplyInvisibility(player);
        player.PrintToChat(" [Invis] 隐身已开启 - 模型/枪械/影子消失，BOT 也看不见");
    }

    private void SetVisible(CCSPlayerController player)
    {
        _invisiblePlayers.Remove(player.Slot);
        RestoreVisibility(player);
        player.PrintToChat(" [Invis] 隐身已关闭 - 已恢复可见");
    }

    /// <summary>将玩家及其所有武器/手套设为不可见，并登记到 CheckTransmit 屏蔽集合。</summary>
    private void ApplyInvisibility(CCSPlayerController player)
    {
        var pawn = player.PlayerPawn?.Value;
        if (pawn is not { IsValid: true }) return;

        // 玩家本体：alpha 0 + 影子归零 + 取消被发现标记 + 让 BOT 忽略
        pawn.Render = Color.FromArgb(0, pawn.Render);
        Utilities.SetStateChanged(pawn, "CBaseModelEntity", "m_clrRender");
        pawn.ShadowStrength = 0f;
        Utilities.SetStateChanged(pawn, "CBaseModelEntity", "m_flShadowStrength");
        pawn.EntitySpottedState.Spotted = false;
        pawn.EntitySpottedState.SpottedByMask[0] = 0;
        pawn.Flags |= FL_NOTARGET;
        Utilities.SetStateChanged(pawn, "CBaseEntity", "m_fFlags");
        _hiddenEntities[pawn] = player.Slot;

        // 所有武器（主武器/副武器/刀/投掷物）：一并隐藏
        var weapons = pawn.WeaponServices?.MyWeapons;
        if (weapons != null)
        {
            foreach (var handle in weapons)
            {
                var weapon = handle.Value;
                if (weapon is not { IsValid: true }) continue;

                weapon.Render = Color.FromArgb(0, weapon.Render);
                Utilities.SetStateChanged(weapon, "CBaseModelEntity", "m_clrRender");
                weapon.ShadowStrength = 0f;
                Utilities.SetStateChanged(weapon, "CBaseModelEntity", "m_flShadowStrength");
                _hiddenEntities[weapon] = player.Slot;
            }
        }

        // 手套（CEconWearable）：一并隐藏，避免只有手套露出来
        var wearables = pawn.MyWearables;
        if (wearables != null)
        {
            foreach (var handle in wearables)
            {
                var wearable = handle.Value;
                if (wearable is not { IsValid: true }) continue;

                wearable.Render = Color.FromArgb(0, wearable.Render);
                Utilities.SetStateChanged(wearable, "CBaseModelEntity", "m_clrRender");
                wearable.ShadowStrength = 0f;
                Utilities.SetStateChanged(wearable, "CBaseModelEntity", "m_flShadowStrength");
                _hiddenEntities[wearable] = player.Slot;
            }
        }
    }

    /// <summary>恢复玩家及其武器/手套的可见性与影子。</summary>
    private void RestoreVisibility(CCSPlayerController player)
    {
        int slot = player.Slot;

        // 清理该玩家登记过的隐藏实体
        var stale = _hiddenEntities.Where(kv => kv.Value == slot).Select(kv => kv.Key).ToList();
        foreach (var entity in stale)
            _hiddenEntities.Remove(entity);

        var pawn = player.PlayerPawn?.Value;
        if (pawn is not { IsValid: true }) return;

        pawn.Render = Color.FromArgb(255, pawn.Render);
        Utilities.SetStateChanged(pawn, "CBaseModelEntity", "m_clrRender");
        pawn.ShadowStrength = 1f;
        Utilities.SetStateChanged(pawn, "CBaseModelEntity", "m_flShadowStrength");
        pawn.Flags &= ~FL_NOTARGET;
        Utilities.SetStateChanged(pawn, "CBaseEntity", "m_fFlags");

        var weapons = pawn.WeaponServices?.MyWeapons;
        if (weapons != null)
        {
            foreach (var handle in weapons)
            {
                var weapon = handle.Value;
                if (weapon is not { IsValid: true }) continue;

                weapon.Render = Color.FromArgb(255, weapon.Render);
                Utilities.SetStateChanged(weapon, "CBaseModelEntity", "m_clrRender");
                weapon.ShadowStrength = 1f;
                Utilities.SetStateChanged(weapon, "CBaseModelEntity", "m_flShadowStrength");
            }
        }

        var wearables = pawn.MyWearables;
        if (wearables != null)
        {
            foreach (var handle in wearables)
            {
                var wearable = handle.Value;
                if (wearable is not { IsValid: true }) continue;

                wearable.Render = Color.FromArgb(255, wearable.Render);
                Utilities.SetStateChanged(wearable, "CBaseModelEntity", "m_clrRender");
                wearable.ShadowStrength = 1f;
                Utilities.SetStateChanged(wearable, "CBaseModelEntity", "m_flShadowStrength");
            }
        }
    }

    // ==================== 秒下包/秒拆弹（!fb / fb）====================
    // 原理：拦截 bomb_beginplant / bomb_begindefuse 事件，
    // 将 C4 下包耗时（ArmedTime）或拆弹倒计时（DefuseCountDown）归零，实现秒完成。
    // 不区分阵营：CT 拿到 C4 后同样可以秒下包。

    private void OnFastBombCommand(CCSPlayerController? player, CommandInfo info)
    {
        if (player == null || !player.IsValid) return;
        int slot = player.Slot;
        if (_fastBombPlayers.Remove(slot))
            player.PrintToChat(" [FastBomb] 已关闭秒下包/秒拆弹");
        else
        {
            _fastBombPlayers.Add(slot);
            player.PrintToChat(" [FastBomb] 已开启秒下包/秒拆弹（0.1秒）");
        }
    }

    private void OnFastBombConsoleCommand(CCSPlayerController? player, CommandInfo info)
    {
        if (player == null || !player.IsValid) return;
        if (info.ArgCount < 2)
        {
            player.PrintToChat(" [FastBomb] 用法: fb 1 开启 / fb 0 关闭");
            return;
        }

        int slot = player.Slot;
        bool enable = !(int.TryParse(info.GetArg(1), out int val) && val == 0);
        if (enable) _fastBombPlayers.Add(slot);
        else _fastBombPlayers.Remove(slot);
        player.PrintToChat(enable ? " [FastBomb] 已开启秒下包/秒拆弹" : " [FastBomb] 已关闭");
    }

    private HookResult OnBombBeginPlant(EventBombBeginplant @event, GameEventInfo info)
    {
        var player = @event.Userid;
        if (player == null || !player.IsValid || !_fastBombPlayers.Contains(player.Slot))
            return HookResult.Continue;

        var bomb = Utilities.FindAllEntitiesByDesignerName<CC4>("weapon_c4").FirstOrDefault();
        if (bomb is not { IsValid: true }) return HookResult.Continue;

        bomb.BombPlacedAnimation = false;
        bomb.ArmedTime = 0f;
        return HookResult.Continue;
    }

    private HookResult OnBombBeginDefuse(EventBombBegindefuse @event, GameEventInfo info)
    {
        var player = @event.Userid;
        if (player == null || !player.IsValid || !_fastBombPlayers.Contains(player.Slot))
            return HookResult.Continue;

        // 拆弹放到下一帧执行，避免在事件回调内直接操作 planted_c4 导致偶发崩溃
        Server.NextFrame(() =>
        {
            var bomb = Utilities.FindAllEntitiesByDesignerName<CPlantedC4>("planted_c4").FirstOrDefault();
            if (bomb is not { IsValid: true }) return;
            bomb.DefuseCountDown = 0f;
        });
        return HookResult.Continue;
    }

    // ==================== 无限蹲起（!sc / sc）====================

    private void OnSpamCrouchCommand(CCSPlayerController? player, CommandInfo info)
    {
        if (player == null || !player.IsValid) return;
        int slot = player.Slot;

        if (_noStaminaPlayers.Contains(slot))
            DisableNoStamina(player);
        else
            EnableNoStamina(player);
    }

    private void OnSpamCrouchConsoleCommand(CCSPlayerController? player, CommandInfo info)
    {
        if (player == null || !player.IsValid) return;

        if (info.ArgCount < 2)
        {
            player.PrintToChat(" [SpamCrouch] 用法: sc 1 开启 / sc 0 关闭");
            return;
        }

        if (int.TryParse(info.GetArg(1), out int val) && val == 0)
            DisableNoStamina(player);
        else
            EnableNoStamina(player);
    }

    private void EnableNoStamina(CCSPlayerController player)
    {
        int slot = player.Slot;
        if (_noStaminaPlayers.Contains(slot))
        {
            player.PrintToChat(" [SpamCrouch] 无限蹲起已经开启");
            return;
        }

        _noStaminaPlayers.Add(slot);

        // 全局 cvar 仅首次设置
        if (!_staminaCvarsSet)
        {
            Server.ExecuteCommand("sv_stamina 0");
            Server.ExecuteCommand("sv_staminajumpcost 0");
            Server.ExecuteCommand("sv_staminalandcost 0");
            Server.ExecuteCommand("sv_staminarecoveryrate 9999");
            Server.ExecuteCommand("sv_staminamax 0");
            Server.ExecuteCommand("sv_timebetweenducks 0");
            Server.ExecuteCommand("sv_jump_spam_penalty_time 0");
            _staminaCvarsSet = true;
        }

        player.PrintToChat(" [SpamCrouch] 无限蹲起已开启 - 无体力无冷却");
    }

    private void DisableNoStamina(CCSPlayerController player)
    {
        int slot = player.Slot;
        if (!_noStaminaPlayers.Remove(slot))
        {
            player.PrintToChat(" [SpamCrouch] 无限蹲起已经关闭");
            return;
        }

        // 所有玩家都关闭时才恢复 cvar
        if (_noStaminaPlayers.Count == 0 && _staminaCvarsSet)
        {
            Server.ExecuteCommand("sv_stamina 1");
            Server.ExecuteCommand("sv_staminajumpcost 0.08");
            Server.ExecuteCommand("sv_staminalandcost 0.05");
            Server.ExecuteCommand("sv_staminarecoveryrate 60");
            Server.ExecuteCommand("sv_staminamax 80");
            _staminaCvarsSet = false;
        }

        player.PrintToChat(" [SpamCrouch] 无限蹲起已关闭 - 体力已恢复");
    }

    // ==================== 全图可穿（!wp / wp）====================
    // 原理：通过修改 CCsWeaponBase 实体的穿透次数(m_nPenetrationCount)为极高值，
    // 使子弹可以穿透任何掩体、材质甚至地板。
    // 每帧检测武器切换，仅在新武器时写入，避免重复 Schema 操作。

    private void OnWallPenCommand(CCSPlayerController? player, CommandInfo info)
    {
        if (player == null || !player.IsValid) return;
        int slot = player.Slot;

        if (_fullPenPlayers.Contains(slot))
        {
            _fullPenPlayers.Remove(slot);
            RestorePlayerActiveWeaponIfNeeded(player);
            player.PrintToChat(" [WallPen] 全图可穿已关闭");
        }
        else
        {
            _fullPenPlayers.Add(slot);
            var wpn = player.PlayerPawn?.Value?.WeaponServices?.ActiveWeapon?.Value;
            if (wpn is { IsValid: true })
                ModifyWeaponForFeatures(wpn, slot);
            player.PrintToChat(" [WallPen] 全图可穿已开启 - 子弹穿透任何掩体/材质/地板");
        }
    }

    private void OnWallPenConsoleCommand(CCSPlayerController? player, CommandInfo info)
    {
        if (player == null || !player.IsValid) return;
        int slot = player.Slot;

        if (info.ArgCount < 2)
        {
            player.PrintToChat(" [WallPen] 用法: wp 1 开启 / wp 0 关闭");
            return;
        }

        if (int.TryParse(info.GetArg(1), out int val) && val == 0)
        {
            _fullPenPlayers.Remove(slot);
            RestorePlayerActiveWeaponIfNeeded(player);
            player.PrintToChat(" [WallPen] 全图可穿已关闭");
        }
        else
        {
            _fullPenPlayers.Add(slot);
            var wpn2 = player.PlayerPawn?.Value?.WeaponServices?.ActiveWeapon?.Value;
            if (wpn2 is { IsValid: true })
                ModifyWeaponForFeatures(wpn2, slot);
            player.PrintToChat(" [WallPen] 全图可穿已开启 - 子弹穿透任何掩体/材质/地板");
        }
    }

    // ==================== 穿墙无伤害衰减（!wd / wd）====================
    // 原理：修改武器实体的穿透伤害保留率，使穿墙后伤害不衰减，
    // 仅保留距离带来的正常子弹伤害衰减。

    private void OnWallDmgReductionCommand(CCSPlayerController? player, CommandInfo info)
    {
        if (player == null || !player.IsValid) return;
        int slot = player.Slot;

        if (_noWallDmgReductionPlayers.Contains(slot))
        {
            _noWallDmgReductionPlayers.Remove(slot);
            RestorePlayerActiveWeaponIfNeeded(player);
            player.PrintToChat(" [WallDmg] 穿墙无衰减已关闭");
        }
        else
        {
            _noWallDmgReductionPlayers.Add(slot);
            var wd1 = player.PlayerPawn?.Value?.WeaponServices?.ActiveWeapon?.Value;
            if (wd1 is { IsValid: true })
                ModifyWeaponForFeatures(wd1, slot);
            player.PrintToChat(" [WallDmg] 穿墙无衰减已开启 - 穿墙不减伤害，仅保留距离衰减");
        }
    }

    private void OnWallDmgReductionConsoleCommand(CCSPlayerController? player, CommandInfo info)
    {
        if (player == null || !player.IsValid) return;
        int slot = player.Slot;

        if (info.ArgCount < 2)
        {
            player.PrintToChat(" [WallDmg] 用法: wd 1 开启 / wd 0 关闭");
            return;
        }

        if (int.TryParse(info.GetArg(1), out int val) && val == 0)
        {
            _noWallDmgReductionPlayers.Remove(slot);
            RestorePlayerActiveWeaponIfNeeded(player);
            player.PrintToChat(" [WallDmg] 穿墙无衰减已关闭");
        }
        else
        {
            _noWallDmgReductionPlayers.Add(slot);
            var wd2 = player.PlayerPawn?.Value?.WeaponServices?.ActiveWeapon?.Value;
            if (wd2 is { IsValid: true })
                ModifyWeaponForFeatures(wd2, slot);
            player.PrintToChat(" [WallDmg] 穿墙无衰减已开启 - 穿墙不减伤害，仅保留距离衰减");
        }
    }

    // ==================== 自动急停（!cs / cs）====================
    // 原理：OnTick 检测松键瞬间，水平方向键全释放时立刻将水平速度归零，
    // 模拟完美反方向急停。不影响 peek（peek 时至少有一个方向键按住，不会触发）。

    private void OnCounterStrafeCommand(CCSPlayerController? player, CommandInfo info)
    {
        if (player == null || !player.IsValid) return;
        int slot = player.Slot;

        if (_counterStrafePlayers.Contains(slot))
        {
            _counterStrafePlayers.Remove(slot);
            player.PrintToChat(" [CounterStrafe] 自动急停已关闭");
        }
        else
        {
            _counterStrafePlayers.Add(slot);
            player.PrintToChat(" [CounterStrafe] 自动急停已开启 - 松键瞬间自动停稳");
        }
    }

    private void OnCounterStrafeConsoleCommand(CCSPlayerController? player, CommandInfo info)
    {
        if (player == null || !player.IsValid) return;
        int slot = player.Slot;

        if (info.ArgCount < 2)
        {
            player.PrintToChat(" [CounterStrafe] 用法: cs 1 开启 / cs 0 关闭");
            return;
        }

        if (int.TryParse(info.GetArg(1), out int val) && val == 0)
        {
            _counterStrafePlayers.Remove(slot);
            player.PrintToChat(" [CounterStrafe] 自动急停已关闭");
        }
        else
        {
            _counterStrafePlayers.Add(slot);
            player.PrintToChat(" [CounterStrafe] 自动急停已开启 - 松键瞬间自动停稳");
        }
    }

    // ==================== 魔法子弹（!mb / mb）====================
    // 原理：拦截 EventWeaponFire，在 ±10° 锥形内找最近敌人。
    // 非致死：直接扣血（引擎自动同步血量）。
    // 致死：记录击杀信息到 _pendingMagicBulletKills → CommitSuicide 让引擎走完整死亡流程
    // → EventPlayerDeath Pre-hook 反射修正击杀者为真实攻击者（非自杀）。
    // 暗金计数 OnPlayerDeathPost 在 Pre-hook 之后执行，读取到的已是修正后的 attacker。

    /// <summary>待修正的魔法子弹击杀：victimPawn.Index → (attackerSlot, weaponDesignerName, headshot)</summary>
    private readonly Dictionary<uint, (int AttackerSlot, string Weapon, bool Headshot)> _pendingMagicBulletKills = new();

    /// <summary>武器 DesignerName → 身体伤害（爆头 = 身体 × 4）</summary>
    private static readonly Dictionary<string, int> WeaponDamages = new()
    {
        {"weapon_ak47", 36}, {"weapon_m4a1", 33}, {"weapon_m4a1_silencer", 33},
        {"weapon_aug", 33}, {"weapon_sg556", 36}, {"weapon_galilar", 33}, {"weapon_famas", 33},
        {"weapon_awp", 115}, {"weapon_ssg08", 88}, {"weapon_scar20", 80}, {"weapon_g3sg1", 80},
        {"weapon_deagle", 53}, {"weapon_revolver", 115}, {"weapon_elite", 42},
        {"weapon_fiveseven", 36}, {"weapon_p250", 35}, {"weapon_usp_silencer", 35},
        {"weapon_glock", 30}, {"weapon_hkp2000", 35}, {"weapon_cz75a", 31}, {"weapon_tec9", 33},
        {"weapon_mp9", 29}, {"weapon_mac10", 26}, {"weapon_mp7", 26},
        {"weapon_mp5sd", 26}, {"weapon_ump45", 35}, {"weapon_p90", 26}, {"weapon_bizon", 26},
        {"weapon_nova", 26}, {"weapon_xm1014", 20}, {"weapon_mag7", 30}, {"weapon_sawedoff", 32},
        {"weapon_m249", 36}, {"weapon_negev", 35},
    };

    private void OnMagicBulletCommand(CCSPlayerController? player, CommandInfo info)
    {
        if (player == null || !player.IsValid) return;
        int slot = player.Slot;

        if (_magicBulletPlayers.Contains(slot))
        {
            _magicBulletPlayers.Remove(slot);
            player.PrintToChat(" [MagicBullet] 魔法子弹已关闭");
        }
        else
        {
            _magicBulletPlayers.Add(slot);
            player.PrintToChat(" [MagicBullet] 魔法子弹已开启 - 瞄准大致方向自动命中最近敌人");
        }
    }

    private void OnMagicBulletConsoleCommand(CCSPlayerController? player, CommandInfo info)
    {
        if (player == null || !player.IsValid) return;
        int slot = player.Slot;

        if (info.ArgCount < 2)
        {
            player.PrintToChat(" [MagicBullet] 用法: mb 1 开启 / mb 0 关闭");
            return;
        }

        if (int.TryParse(info.GetArg(1), out int val) && val == 0)
        {
            _magicBulletPlayers.Remove(slot);
            player.PrintToChat(" [MagicBullet] 魔法子弹已关闭");
        }
        else
        {
            _magicBulletPlayers.Add(slot);
            player.PrintToChat(" [MagicBullet] 魔法子弹已开启 - 瞄准大致方向自动命中最近敌人");
        }
    }

    /// <summary>
    /// EventPlayerDeath Pre-hook：修正魔法子弹击杀的死亡事件，
    /// 将自杀（CommitSuicide）改为正确的攻击者/武器/爆头信息。
    /// </summary>
    private HookResult OnPlayerDeathMagicFix(EventPlayerDeath @event, GameEventInfo info)
    {
        var victim = @event.Userid;
        if (victim == null || !victim.IsValid) return HookResult.Continue;

        var victimPawn = victim.PlayerPawn?.Value;
        if (victimPawn == null || !victimPawn.IsValid) return HookResult.Continue;

        if (!_pendingMagicBulletKills.TryGetValue(victimPawn.Index, out var killInfo))
            return HookResult.Continue;

        _pendingMagicBulletKills.Remove(victimPawn.Index);

        // 修正死亡事件中的击杀者（SetInt 是 protected，用反射调用）
        var attacker = Utilities.GetPlayerFromSlot(killInfo.AttackerSlot);
        if (attacker != null)
        {
            var setIntMethod = typeof(EventPlayerDeath).BaseType!.GetMethod("SetInt",
                BindingFlags.NonPublic | BindingFlags.Instance);
            setIntMethod?.Invoke(@event, new object[] { "attacker", (int)attacker.UserId! });
        }

        @event.Weapon = killInfo.Weapon;
        @event.Headshot = killInfo.Headshot;

        return HookResult.Continue;
    }

    private HookResult OnWeaponFireMagicBullet(EventWeaponFire @event, GameEventInfo info)
    {
        var player = @event.Userid;
        if (player == null || !player.IsValid) return HookResult.Continue;
        if (!_magicBulletPlayers.Contains(player.Slot)) return HookResult.Continue;

        var pawn = player.PlayerPawn?.Value;
        if (pawn == null || !pawn.IsValid) return HookResult.Continue;

        // 获取玩家眼睛位置和瞄准方向
        var eyePos = pawn.CBodyComponent?.SceneNode?.AbsOrigin;
        if (eyePos == null) return HookResult.Continue;

        var eyeAngles = pawn.EyeAngles;
        float pitch = (float)(eyeAngles.X * Math.PI / 180.0);
        float yaw   = (float)(eyeAngles.Y * Math.PI / 180.0);
        var aimDir = new Vector(
            (float)(Math.Cos(pitch) * Math.Cos(yaw)),
            (float)(Math.Cos(pitch) * Math.Sin(yaw)),
            (float)(-Math.Sin(pitch))
        );

        // 在 ±10° 锥形内找最近的活敌人
        const float maxAngle = 10f;
        CCSPlayerController? bestTarget = null;
        float bestAngle = maxAngle + 1f;

        foreach (var target in Utilities.GetPlayers())
        {
            if (target == null || !target.IsValid) continue;
            if (target == player) continue;
            if (target.Team == player.Team) continue;

            var targetPawn = target.PlayerPawn?.Value;
            if (targetPawn is not { IsValid: true, Health: > 0 }) continue;

            var targetPos = targetPawn.CBodyComponent?.SceneNode?.AbsOrigin;
            if (targetPos == null) continue;

            float dx = targetPos.X - eyePos.X;
            float dy = targetPos.Y - eyePos.Y;
            float dz = targetPos.Z - eyePos.Z;
            float dist = (float)Math.Sqrt(dx * dx + dy * dy + dz * dz);
            if (dist < 0.01f) continue;

            var toTarget = new Vector(dx / dist, dy / dist, dz / dist);
            float dot = aimDir.X * toTarget.X + aimDir.Y * toTarget.Y + aimDir.Z * toTarget.Z;
            float angle = (float)(Math.Acos(Math.Clamp(dot, -1.0, 1.0)) * 180.0 / Math.PI);

            if (angle < bestAngle)
            {
                bestAngle = angle;
                bestTarget = target;
            }
        }

        if (bestTarget == null) return HookResult.Continue;

        var bestPawn = bestTarget.PlayerPawn?.Value;
        if (bestPawn == null || !bestPawn.IsValid) return HookResult.Continue;

        // 随机头部（65%）或身体（35%）
        bool headshot = Random.Shared.Next(100) < 65;

        // 查武器伤害
        string rawWeapon = @event.Weapon;
        string designerName = rawWeapon.StartsWith("weapon_") ? rawWeapon : "weapon_" + rawWeapon;
        if (!WeaponDamages.TryGetValue(designerName, out int bodyDmg))
            bodyDmg = 30;
        int damage = headshot ? bodyDmg * 4 : bodyDmg;

        int newHealth = Math.Max(0, bestPawn.Health - damage);

        if (newHealth <= 0)
        {
            // 致死：记录待修正信息，走 CommitSuicide 触发完整死亡流程
            _pendingMagicBulletKills[bestPawn.Index] = (player.Slot, designerName, headshot);
            bestPawn.Health = 0;
            Utilities.SetStateChanged(bestPawn, "CBaseEntity", "m_iHealth");
            bestPawn.CommitSuicide(false, true);
            // OnPlayerDeathMagicFix（Pre-hook）会在死亡事件触发前修正 attacker
        }
        else
        {
            // 非致死：直接扣血，引擎自动同步血量显示
            bestPawn.Health = newHealth;
            Utilities.SetStateChanged(bestPawn, "CBaseEntity", "m_iHealth");
        }

        return HookResult.Continue;
    }

    // ==================== 暗金计数器 ====================

    private void OnStatTrakCommand(CCSPlayerController? player, CommandInfo info)
    {
        if (player == null || !player.IsValid) return;

        if (info.ArgCount < 2)
        {
            player.PrintToChat(" [StatTrak] 用法: !st <数字> 或 st <数字>");
            return;
        }

        TrySetStatTrak(player, info.GetArg(1));
    }

    private void OnStatTrakConsoleCommand(CCSPlayerController? player, CommandInfo info)
    {
        if (player == null || !player.IsValid) return;

        if (info.ArgCount < 2)
        {
            player.PrintToChat(" [StatTrak] 用法: st <数字>");
            return;
        }

        TrySetStatTrak(player, info.GetArg(1));
    }

    private void TrySetStatTrak(CCSPlayerController player, string arg)
    {
        var weapon = player.PlayerPawn?.Value?.WeaponServices?.ActiveWeapon?.Value;
        if (weapon == null || !weapon.IsValid)
        {
            player.PrintToChat(" [StatTrak] 当前没有持有武器");
            return;
        }

        if (!WeaponHasStatTrak(weapon))
        {
            player.PrintToChat(" [StatTrak] 当前武器没有暗金计数器(StatTrak)");
            return;
        }

        if (!int.TryParse(arg, out int value) || value < 0)
        {
            player.PrintToChat(" [StatTrak] 请输入有效的非负整数 (0 ~ 2147483647)");
            return;
        }

        int slot = player.Slot;
        string designerName = weapon.DesignerName;

        // 合并武器真实暗金值，确保设定值不小于武器已有真实值
        int realValue = weapon.FallbackStatTrak >= 0 ? weapon.FallbackStatTrak : 0;
        int mergedValue = Math.Max(value, realValue);

        if (!_statTrakValues.TryGetValue(slot, out var typeDict))
        {
            typeDict = new Dictionary<string, int>();
            _statTrakValues[slot] = typeDict;
        }
        typeDict[designerName] = mergedValue;

        ApplyStatTrakValue(weapon, mergedValue);
        player.PrintToChat(mergedValue != value
            ? $" [StatTrak] 暗金计数已修改为: {mergedValue} (真实值 {realValue} 更高，已自动合并)"
            : $" [StatTrak] 暗金计数已修改为: {value} (击杀会在设定值上递增)");
    }

    /// <summary>
    /// 判断武器是否拥有暗金计数器。
    /// 1) 原生武器：m_nFallbackStatTrak >= 0
    /// 2) InvSim 换肤：EntityQuality == 9（动态属性中含 "kill eater"）
    /// </summary>
    private static bool WeaponHasStatTrak(CBasePlayerWeapon weapon)
    {
        if (weapon.FallbackStatTrak >= 0)
            return true;

        // InvSim 路径：EntityQuality == 9 即为暗金（kill eater 属性由 InvSim 保证）
        return weapon.AttributeManager?.Item?.EntityQuality == 9;
    }

    /// <summary>
    /// 将暗金计数值写入武器。双写 FallbackStatTrak + kill eater 属性，
    /// 且延迟 0.0f 再写一次 FallbackStatTrak，确保在所有 Tick 处理完后最后写入。
    /// </summary>
    private void ApplyStatTrakValue(CBasePlayerWeapon weapon, int value)
    {
        // 立即写入 FallbackStatTrak（显示）
        weapon.FallbackStatTrak = value;
        Utilities.SetStateChanged(weapon, "CCSWeaponBase", "m_nFallbackStatTrak");

        // 立即写入 kill eater 动态属性（"真实击杀"源头）
        var dynAttrs = weapon.AttributeManager?.Item?.NetworkedDynamicAttributes;
        if (dynAttrs != null)
        {
            nint handle = ((NativeObject)(object)dynAttrs).Handle;
            if (handle != nint.Zero)
            {
                float floatValue = BitConverter.Int32BitsToSingle(value);
                _setAttributeValueByName.Invoke(handle, "kill eater", floatValue);
                _setAttributeValueByName.Invoke(handle, "kill eater score type", 0f);
            }
        }

        // 0.0f 延迟再写一次 FallbackStatTrak —— 确保在所有 OnTick 之后执行，不被 InvSim 覆盖
        var w = weapon;
        AddTimer(0.0f, () =>
        {
            if (w is not { IsValid: true }) return;
            w.FallbackStatTrak = value;
            Utilities.SetStateChanged(w, "CCSWeaponBase", "m_nFallbackStatTrak");
        });
    }

    /// <summary>
    /// 武器实体创建事件：捕获 buy / give 等所有途径获取的武器。
    /// 若玩家对该武器类型设过 ST 值，合并 max(存储值, 武器真实暗金值) 后写入。
    /// 防止换武器后存储值低于真实击杀数导致"倒回"。
    /// </summary>
    private void OnWeaponEntityCreated(CEntityInstance entity)
    {
        if (entity is not CBasePlayerWeapon weapon || !weapon.IsValid) return;

        var owner = weapon.OwnerEntity?.Value;
        if (owner is not CCSPlayerController player || !player.IsValid) return;

        int slot = player.Slot;
        string designerName = weapon.DesignerName;

        if (!_statTrakValues.TryGetValue(slot, out var typeDict)
            || !typeDict.TryGetValue(designerName, out int storedValue))
            return;

        // 合并武器真实暗金值（InvSim 写入的），确保存储值不会低于真实值
        int realValue = weapon.FallbackStatTrak >= 0 ? weapon.FallbackStatTrak : 0;
        int mergedValue = Math.Max(storedValue, realValue);
        if (mergedValue != storedValue)
            typeDict[designerName] = mergedValue;

        // 穿墙功能：新武器入手时立即修改穿透属性
        ApplyWeaponPenFeatures(player, weapon);

        // 暗金武器立即写值覆盖真实击杀数
        if (WeaponHasStatTrak(weapon))
            ApplyStatTrakValue(weapon, mergedValue);
    }

    /// <summary>
    /// PostHook：玩家击杀时，若攻击者对击杀武器类型设过 ST 值，始终递增并持久化。
    /// 不再检查 WeaponHasStatTrak —— 换武器后 InvSim 可能短暂重置暗金状态导致误判。
    /// ApplyStatTrakValue 内部已用 null-conditional 安全处理非暗金武器。
    /// </summary>
    private HookResult OnPlayerDeathPost(EventPlayerDeath @event, GameEventInfo info)
    {
        var attacker = @event.Attacker;
        if (attacker == null || !attacker.IsValid) return HookResult.Continue;
        if (@event.Userid != null && attacker.Slot == @event.Userid.Slot)
            return HookResult.Continue;

        int slot = attacker.Slot;

        string evWeapon = @event.Weapon;
        if (string.IsNullOrEmpty(evWeapon)) return HookResult.Continue;
        string designerName = evWeapon.StartsWith("weapon_") ? evWeapon : "weapon_" + evWeapon;

        // 玩家对该武器类型设过 ST 值才递增
        if (!_statTrakValues.TryGetValue(slot, out var typeDict)
            || !typeDict.TryGetValue(designerName, out int currentValue))
            return HookResult.Continue;

        // 从物品栏找到真正造成击杀的武器实体，写入新值
        var weapons = attacker.PlayerPawn?.Value?.WeaponServices?.MyWeapons;
        CBasePlayerWeapon? killWeapon = null;
        if (weapons != null)
        {
            foreach (var wh in weapons)
            {
                var w = wh.Value;
                if (w is { IsValid: true } && w.DesignerName == designerName)
                {
                    killWeapon = w;
                    break;
                }
            }
        }

        if (killWeapon == null)
            return HookResult.Continue;

        int newValue = currentValue + 1;
        typeDict[designerName] = newValue;
        ApplyStatTrakValue(killWeapon, newValue);

        // JSON 持久化记录
        IncrementJsonKillCount(attacker, designerName);

        return HookResult.Continue;
    }

    // ==================== 游戏事件 ====================

    private HookResult OnPlayerSpawn(EventPlayerSpawn @event, GameEventInfo info)
    {
        var player = @event.Userid;
        if (player == null || !player.IsValid) return HookResult.Continue;
        int slot = player.Slot;
        AddTimer(0.15f, () =>
        {
            CreatePlayerGlow(player);
            RegisterPawnMapping(player);
            if (_godPlayers.Contains(slot))
                SetupGodMode(player);
            if (_invisiblePlayers.Contains(slot))
                ApplyInvisibility(player);
        });
        return HookResult.Continue;
    }

    /// <summary>
    /// 玩家断开时仅清理瞬态数据（glow 实体、pawn 映射）。
    /// 不清理功能开关状态（_xrayUsers/_godPlayers/_noFlashPlayers/_statTrakValues），
    /// 因为换局时引擎可能重建玩家实体，触发虚假 disconnect。
    /// 玩家主动退出时这些状态会随 Unload 或服务器关闭自然清理。
    /// </summary>
    private HookResult OnPlayerDisconnect(EventPlayerDisconnect @event, GameEventInfo info)
    {
        var player = @event.Userid;
        if (player == null) return HookResult.Continue;
        RemovePlayerGlow(player);
        UnregisterPawnMapping(player);
        return HookResult.Continue;
    }

    private HookResult OnRoundStart(EventRoundStart @event, GameEventInfo info)
    {
        ApplyAllLimits();

        // 新回合所有武器重置，清空穿透修改备份
        _weaponOrigValues.Clear();

        // 清空掉落物和C4的glow
        DestroyAllDropGlows();
        DestroyC4Glow();

        // 多级重试兜底：pawn、CBodyComponent、SceneNode 都可能在换局后延迟就绪
        AddTimer(0.2f, () => RebuildAllPlayerStates());
        AddTimer(0.5f, () => RebuildAllPlayerStates());
        AddTimer(1.0f, () => RebuildAllPlayerStates());
        AddTimer(2.0f, () => RebuildAllPlayerStates());
        return HookResult.Continue;
    }

    /// <summary>
    /// 重建所有玩家的发光实体和 Pawn 映射，并恢复无敌玩家状态。
    /// 单个玩家失败时不阻塞其他人，且失败玩家自动排入重试。
    /// </summary>
    private void RebuildAllPlayerStates()
    {
        foreach (var p in Utilities.GetPlayers())
        {
            if (p is not { IsValid: true }) continue;
            TryCreatePlayerGlowWithRetry(p, 0);
            RegisterPawnMapping(p);
            if (_godPlayers.Contains(p.Slot))
                SetupGodMode(p);
        }
    }

    /// <summary>
    /// 带重试的发光实体创建。若 pawn 未就绪则延迟重试，最多 5 次。
    /// </summary>
    private void TryCreatePlayerGlowWithRetry(CCSPlayerController player, int attempt)
    {
        if (player is not { IsValid: true }) return;
        if (attempt >= 5) return;

        var pawn = player.PlayerPawn?.Value;
        if (pawn is not { IsValid: true })
        {
            AddTimer(0.15f, () => TryCreatePlayerGlowWithRetry(player, attempt + 1));
            return;
        }

        var body = pawn.CBodyComponent;
        if (body?.SceneNode == null)
        {
            AddTimer(0.15f, () => TryCreatePlayerGlowWithRetry(player, attempt + 1));
            return;
        }

        CreatePlayerGlow(player);
    }

    // ==================== 无敌核心：引擎层伤害拦截 ====================

    private HookResult OnPlayerTakeDamagePre(CCSPlayerPawn pawn, CTakeDamageInfo info)
    {
        if (pawn == null || !pawn.IsValid) return HookResult.Continue;

        // 优先通过 pawn → Controller 查找，绕过 _pawnToSlot 映射可能超时的问题（切后台时）
        var controller = pawn.Controller?.Value;
        int slot;

        if (controller is CCSPlayerController player && player.IsValid)
        {
            slot = player.Slot;
            // 修复映射（如果通过 Controller 找到了但映射里没有）
            if (!_pawnToSlot.ContainsKey(pawn.Index))
                _pawnToSlot[pawn.Index] = slot;
        }
        else if (!_pawnToSlot.TryGetValue(pawn.Index, out slot))
        {
            return HookResult.Continue;
        }

        if (!_godPlayers.Contains(slot)) return HookResult.Continue;

        info.Damage = 0f;

        return HookResult.Continue;
    }

    // ==================== OnTick：兜底状态同步 ====================

    private int _tickCounter;

    /// <summary>
    /// 每帧：God无敌 HP/Armor 兜底 + NoFlash 归零 + StatTrak 最高权重覆盖 + 64帧周期 XRay glow 修复。
    /// StatTrak 采用"最高权重"设计：每帧无条件强制写回内存中的值，InvSim/原版暗金无法干涉。
    /// </summary>
    private void OnTick()
    {
        bool hasWallPen = _fullPenPlayers.Count > 0;
        bool hasNoWallDmg = _noWallDmgReductionPlayers.Count > 0;
        bool hasOrphanedWeapons = _weaponOrigValues.Count > 0; // 有被修改的武器待恢复（丢地上的枪等）
        bool hasCounterStrafe = _counterStrafePlayers.Count > 0;
        bool hasGod = _godPlayers.Count > 0;
        bool hasXray = _xrayUsers.Count > 0;
        bool hasStatTrak = _statTrakValues.Count > 0;
        bool hasNoFlash = _noFlashPlayers.Count > 0;
        bool hasStamina = _noStaminaPlayers.Count > 0;
        bool hasInvisible = _invisiblePlayers.Count > 0;
        if (!hasGod && !hasXray && !hasStatTrak && !hasNoFlash && !hasStamina && !hasWallPen && !hasNoWallDmg && !hasOrphanedWeapons && !hasCounterStrafe && !hasInvisible) return;

        _tickCounter++;

        // 隐身：每帧重建隐藏实体集合，避免残留失效实体（换武器/换局后自动清理）
        if (hasInvisible) _hiddenEntities.Clear();

        foreach (var player in Utilities.GetPlayers())
        {
            if (player == null || !player.IsValid) continue;
            int slot = player.Slot;

            if (hasGod && _godPlayers.Contains(slot))
            {
                var pawn = player.PlayerPawn?.Value;
                if (pawn is { IsValid: true })
                {
                    pawn.Health = 100;
                    pawn.ArmorValue = 100;
                }
            }

            // 防闪光：每帧将 FlashDuration 归零
            if (hasNoFlash && _noFlashPlayers.Contains(slot))
            {
                var pawn = player.PlayerPawn?.Value;
                if (pawn is { IsValid: true })
                    pawn.FlashDuration = 0f;
            }

            // 无限蹲起：每帧重置 DuckSpeed，绕过引擎硬编码的蹲起速度递减
            if (hasStamina && _noStaminaPlayers.Contains(slot))
            {
                var pawn = player.PlayerPawn?.Value;
                if (pawn is { IsValid: true } && pawn.MovementServices != null)
                {
                    // m_flDuckSpeed 偏移 1040，引擎每蹲一次就减小，强制恢复满值
                    Schema.SetSchemaValue<float>(pawn.MovementServices.Handle, "CCSPlayer_MovementServices", "m_flDuckSpeed", 6.0f);
                }
            }

            // ===== 穿墙功能：全图可穿 + 无衰减 =====
            // 对每个玩家：开启功能 → 确保武器已修改；未开启 → 确保武器已恢复默认
            {
                var weapon = player.PlayerPawn?.Value?.WeaponServices?.ActiveWeapon?.Value;
                if (weapon is { IsValid: true })
                {
                    bool hasFullPen = _fullPenPlayers.Contains(slot);
                    bool hasNoDmg = _noWallDmgReductionPlayers.Contains(slot);

                    if (hasFullPen || hasNoDmg)
                    {
                        // 玩家开启了穿墙功能 → 确保武器被修改
                        ModifyWeaponForFeatures(weapon, slot);
                    }
                    else
                    {
                        // 玩家未开启穿墙 → 如果武器曾被修改，恢复默认
                        RestoreWeaponIfModified(weapon);
                    }
                }
            }

            // 暗金覆盖：玩家对某武器类型设过值 → 每帧强制写回
            if (hasStatTrak && _statTrakValues.TryGetValue(slot, out var typeDict))
            {
                var weapon = player.PlayerPawn?.Value?.WeaponServices?.ActiveWeapon?.Value;
                if (weapon is { IsValid: true } && WeaponHasStatTrak(weapon))
                {
                    string designerName = weapon.DesignerName;
                    if (typeDict.TryGetValue(designerName, out int storedValue))
                    {
                        ApplyStatTrakValue(weapon, storedValue);
                    }
                }
            }

            // ===== 自动急停：松键瞬间水平速度归零 =====
            // 仅当 WASD 四个方向键全部松开且在地面时触发，不影响 peek/连跳/空中移动
            if (hasCounterStrafe && _counterStrafePlayers.Contains(slot))
            {
                var cspawn = player.PlayerPawn?.Value;
                if (cspawn is { IsValid: true } && (cspawn.Flags & 1) != 0) // FL_ONGROUND
                {
                    var buttons = player.Buttons;
                    bool hasHorizontalInput = (buttons & PlayerButtons.Forward) != 0
                                           || (buttons & PlayerButtons.Back) != 0
                                           || (buttons & PlayerButtons.Moveleft) != 0
                                           || (buttons & PlayerButtons.Moveright) != 0;

                    if (!hasHorizontalInput)
                    {
                        var vel = cspawn.AbsVelocity;
                        if (vel != null && (vel.X != 0 || vel.Y != 0))
                            cspawn.Teleport(null, null, new Vector(0, 0, vel.Z));
                    }
                }
            }

            // 每 64 帧：XRay glow 缺失修复
            if (_tickCounter % 64 == 0)
            {
                if (hasXray && _xrayUsers.Contains(slot))
                {
                    if (!_playerGlows.TryGetValue(slot, out var pair)
                        || pair.relay is not { IsValid: true }
                        || pair.glow is not { IsValid: true })
                    {
                        TryCreatePlayerGlowWithRetry(player, 0);
                    }
                }
            }

            // 隐身：每帧重新应用，保证换武器/换局后依旧不可见
            if (hasInvisible && _invisiblePlayers.Contains(slot))
                ApplyInvisibility(player);
        }

        // ===== 掉落物透视：每 32 帧扫描更新 =====
        if (_dropXrayUsers.Count > 0 && _tickCounter % 32 == 0)
            UpdateDropGlows();

        // ===== C4透视闪烁 =====
        if (_c4GlowPair is { } c4p && c4p.glow is { IsValid: true } && _c4XrayUsers.Count > 0)
        {
            _c4FlashTick++;
            if (_c4FlashTick % 8 == 0)
                c4p.glow.Render = c4p.glow.Render.A == 1
                    ? Color.FromArgb(255, 255, 255, 255)
                    : Color.FromArgb(1, 255, 255, 255);
        }
    }

    // ==================== 无敌辅助 ====================

    private void SetupGodMode(CCSPlayerController player)
    {
        var pawn = player.PlayerPawn?.Value;
        if (pawn == null || !pawn.IsValid) return;

        pawn.Health = 100;
        pawn.ArmorValue = 100;
    }

    // ==================== Pawn ↔ Slot 映射 ====================

    private void RegisterPawnMapping(CCSPlayerController player)
    {
        var pawn = player.PlayerPawn?.Value;
        if (pawn == null || !pawn.IsValid) return;

        // 清理同一玩家的旧 pawn 映射（换局时 pawn 重置，旧 Index 不再有效）
        int slot = player.Slot;
        var staleKeys = _pawnToSlot.Where(kv => kv.Value == slot && kv.Key != pawn.Index)
                                   .Select(kv => kv.Key).ToList();
        foreach (var key in staleKeys)
            _pawnToSlot.Remove(key);

        _pawnToSlot[pawn.Index] = slot;
    }

    private void UnregisterPawnMapping(CCSPlayerController player)
    {
        var pawn = player.PlayerPawn?.Value;
        if (pawn != null)
            _pawnToSlot.Remove(pawn.Index);
    }

    // ==================== CheckTransmit：选择性传输 ====================

    private void OnCheckTransmit(CCheckTransmitInfoList infoList)
    {
        foreach ((CCheckTransmitInfo info, CCSPlayerController? player) in infoList)
        {
            if (player == null || !player.IsValid) continue;

            // 隐身：阻止其他玩家（含 BOT）接收隐身玩家的实体（模型 + 所有武器）
            foreach (var (entity, ownerSlot) in _hiddenEntities)
            {
                if (!entity.IsValid) continue;
                if (ownerSlot != player.Slot)
                    info.TransmitEntities.Remove(entity);
            }

            bool isXrayUser = _xrayUsers.Contains(player.Slot);

            foreach (var (slot, (relay, glow)) in _playerGlows)
            {
                if (relay == null || !relay.IsValid) continue;
                if (glow == null || !glow.IsValid) continue;
                if (slot == player.Slot) continue;

                if (isXrayUser)
                {
                    info.TransmitEntities.Add(relay);
                    info.TransmitEntities.Add(glow);
                }
                else
                {
                    info.TransmitEntities.Remove(relay);
                    info.TransmitEntities.Remove(glow);
                }
            }

            // 掉落物透视
            bool isDropXrayUser = _dropXrayUsers.Contains(player.Slot);
            foreach (var pair in _dropGlows.Values)
            {
                if (pair.glow is not { IsValid: true }) continue;
                if (isDropXrayUser)
                {
                    info.TransmitEntities.Add(pair.relay);
                    info.TransmitEntities.Add(pair.glow);
                }
                else
                {
                    info.TransmitEntities.Remove(pair.relay);
                    info.TransmitEntities.Remove(pair.glow);
                }
            }

            // C4透视
            bool isC4XrayUser = _c4XrayUsers.Contains(player.Slot);
            if (_c4GlowPair is { } c4p && c4p.glow is { IsValid: true })
            {
                if (isC4XrayUser)
                {
                    info.TransmitEntities.Add(c4p.relay);
                    info.TransmitEntities.Add(c4p.glow);
                }
                else
                {
                    info.TransmitEntities.Remove(c4p.relay);
                    info.TransmitEntities.Remove(c4p.glow);
                }
            }
        }
    }

    // ==================== 玩家发光实体管理 ====================

    private void CreatePlayerGlow(CCSPlayerController player)
    {
        if (player == null || !player.IsValid) return;

        var pawn = player.PlayerPawn?.Value;
        if (pawn == null || !pawn.IsValid) return;

        var body = pawn.CBodyComponent;
        var scene = body?.SceneNode;
        if (scene == null) return;

        string modelName;
        try { modelName = scene.GetSkeletonInstance().ModelState.ModelName; }
        catch { return; }
        if (string.IsNullOrEmpty(modelName)) return;

        int slot = player.Slot;
        // 仅在确认能成功创建新实体后才清理旧实体，避免清空后无替代
        if (_playerGlows.ContainsKey(slot))
            RemovePlayerGlow(player);

        Color teamColor = player.Team switch
        {
            CsTeam.Terrorist => Color.OrangeRed,
            CsTeam.CounterTerrorist => Color.DodgerBlue,
            _ => Color.LimeGreen,
        };

        var relay = Utilities.CreateEntityByName<CDynamicProp>("prop_dynamic");
        if (relay == null || !relay.IsValid) return;

        relay.SetModel(modelName);
        relay.Spawnflags = 256u;
        relay.RenderMode = RenderMode_t.kRenderNone;
        relay.DispatchSpawn();

        var glow = Utilities.CreateEntityByName<CDynamicProp>("prop_dynamic");
        if (glow == null || !glow.IsValid) { relay.Remove(); return; }

        glow.SetModel(modelName);
        glow.Spawnflags = 256u;
        glow.Render = Color.FromArgb(1, 255, 255, 255);
        glow.DispatchSpawn();

        glow.Glow.GlowColorOverride = teamColor;
        glow.Glow.GlowRange = 5000;
        glow.Glow.GlowRangeMin = 0;
        glow.Glow.GlowTeam = -1;
        glow.Glow.GlowType = 3;

        relay.AcceptInput("FollowEntity", pawn, relay, "!activator");
        glow.AcceptInput("FollowEntity", relay, glow, "!activator");

        _playerGlows[slot] = (relay, glow);
    }

    private void RemovePlayerGlow(CCSPlayerController player)
    {
        if (!_playerGlows.TryGetValue(player.Slot, out var pair)) return;
        if (pair.glow is { IsValid: true }) pair.glow.Remove();
        if (pair.relay is { IsValid: true }) pair.relay.Remove();
        _playerGlows.Remove(player.Slot);
    }

    private void DestroyAllPlayerGlows()
    {
        foreach (var (_, (relay, glow)) in _playerGlows)
        {
            if (glow is { IsValid: true }) glow.Remove();
            if (relay is { IsValid: true }) relay.Remove();
        }
        _playerGlows.Clear();
    }

    // ==================== BOT数量解锁 ====================

    private void OnMapStart(string mapName)
    {
        AddTimer(1.0f, () =>
        {
            EnsureSpawnPoints();
            ApplyAllLimits();
        });
    }

    private HookResult OnBotAddCommand(CCSPlayerController? player, CommandInfo info)
    {
        ApplyAllLimits();
        return HookResult.Continue;
    }

    private void EnsureSpawnPoints()
    {
        RemoveCreatedSpawns();

        var tSpawns = Utilities.FindAllEntitiesByDesignerName<CBaseEntity>("info_player_terrorist").ToArray();
        var ctSpawns = Utilities.FindAllEntitiesByDesignerName<CBaseEntity>("info_player_counterterrorist").ToArray();

        int nativeT = tSpawns.Length;
        int nativeCT = ctSpawns.Length;

        int botQuota = 0;
        try
        {
            var botQuotaCvar = ConVar.Find("bot_quota");
            if (botQuotaCvar != null)
                botQuota = botQuotaCvar.GetPrimitiveValue<int>();
        }
        catch { }

        int neededPerTeam = Math.Max(Math.Min(botQuota / 2, MaxPerTeam), 0);
        // 至少保持原生数量
        neededPerTeam = Math.Max(neededPerTeam, nativeCT);
        neededPerTeam = Math.Max(neededPerTeam, nativeT);

        Console.WriteLine($"[BotNumber] 原生 T:{nativeT}, CT:{nativeCT} | bot_quota={botQuota} 每方上限{MaxPerTeam}");

        if (neededPerTeam > nativeCT)
        {
            int created = CreateExtraSpawns("info_player_counterterrorist", ctSpawns, neededPerTeam - nativeCT, 3);
            Console.WriteLine($"[BotNumber] CT出生点: +{created} → 共{nativeCT + created}");
        }
        if (neededPerTeam > nativeT)
        {
            int created = CreateExtraSpawns("info_player_terrorist", tSpawns, neededPerTeam - nativeT, 2);
            Console.WriteLine($"[BotNumber] T出生点: +{created} → 共{nativeT + created}");
        }
    }

    private int CreateExtraSpawns(string className, CBaseEntity[] templates, int needed, byte teamNum)
    {
        if (templates.Length == 0) return 0;

        var random = new Random();
        int created = 0;

        for (int i = 0; i < needed; i++)
        {
            var template = templates[random.Next(templates.Length)];
            if (template?.AbsOrigin == null) continue;

            float offsetX = (float)((random.NextDouble() - 0.5f) * 80f);
            float offsetY = (float)((random.NextDouble() - 0.5f) * 80f);

            var newEntity = Utilities.CreateEntityByName<CBaseEntity>(className);
            if (newEntity == null || !newEntity.IsValid) continue;

            newEntity.AbsOrigin!.X = template.AbsOrigin.X + offsetX;
            newEntity.AbsOrigin.Y = template.AbsOrigin.Y + offsetY;
            newEntity.AbsOrigin.Z = template.AbsOrigin.Z;
            if (template.AbsRotation != null)
            {
                newEntity.AbsRotation!.X = template.AbsRotation.X;
                newEntity.AbsRotation.Y = template.AbsRotation.Y;
                newEntity.AbsRotation.Z = template.AbsRotation.Z;
            }
            newEntity.TeamNum = teamNum;
            string gname = $"botnum_{className}_{i}";
            newEntity.Globalname = gname;
            newEntity.DispatchSpawn();
            _createdSpawnGlobalnames.Add(gname);
            created++;
        }

        return created;
    }

    private void RemoveCreatedSpawns()
    {
        foreach (var gname in _createdSpawnGlobalnames)
        {
            var entities = Utilities.FindAllEntitiesByDesignerName<CBaseEntity>(
                gname.Contains("terrorist") ? "info_player_terrorist" : "info_player_counterterrorist");
            foreach (var entity in entities)
            {
                if (entity.Globalname == gname && entity.IsValid)
                    entity.Remove();
            }
        }
        _createdSpawnGlobalnames.Clear();
    }

    private void ApplyAllLimits()
    {
        foreach (var proxy in Utilities.FindAllEntitiesByDesignerName<CCSGameRulesProxy>("cs_gamerules"))
        {
            if (proxy.GameRules == null) continue;
            proxy.GameRules.NumSpawnableCT = MaxPerTeam;
            proxy.GameRules.NumSpawnableTerrorist = MaxPerTeam;
            proxy.GameRules.MaxNumCTs = MaxPerTeam;
            proxy.GameRules.MaxNumTerrorists = MaxPerTeam;
        }

        Server.ExecuteCommand("mp_autoteambalance 0");
        Server.ExecuteCommand("mp_limitteams 0");
    }

    // ==================== 穿墙功能：Schema 修改辅助 ====================
    // 核心思路：修改武器前先备份原始值 → 丢枪/换人时恢复 → 捡枪后再判断新主人是否需要
    // _weaponOrigValues: weaponIndex → (origPen, origDmg)，只存已被修改的武器

    /// <summary>
    /// 武器入手时调用（OnWeaponEntityCreated）。如果主人开启了穿墙功能则立即修改。
    /// </summary>
    private void ApplyWeaponPenFeatures(CCSPlayerController player, CBasePlayerWeapon weapon)
    {
        if (weapon is not { IsValid: true }) return;
        ModifyWeaponForFeatures(weapon, player.Slot);
    }

    /// <summary>
    /// 根据 slot 对应的玩家功能状态，修改武器穿透属性。
    /// 首次修改该武器时自动读取并备份原始值。
    /// 已在 _weaponOrigValues 中存在则跳过（已修改过，避免重复 Schema 操作）。
    /// 当玩家两种功能都没开时不调用此方法。
    /// </summary>
    private void ModifyWeaponForFeatures(CBasePlayerWeapon weapon, int slot)
    {
        if (weapon is not { IsValid: true }) return;
        uint idx = weapon.Index;

        bool hasFullPen = _fullPenPlayers.Contains(slot);
        bool hasNoDmg = _noWallDmgReductionPlayers.Contains(slot);
        if (!hasFullPen && !hasNoDmg) return; // 玩家没开任何穿墙功能

        // 已修改过的武器跳过（避免每帧重复 Schema 调用）
        if (_weaponOrigValues.ContainsKey(idx)) return;

        // 首次修改：先读取并备份原始值
        int origPen = TryGetWeaponPenetration(weapon);
        float origDmg = TryGetWeaponPenDmgModifier(weapon);
        _weaponOrigValues[idx] = (origPen, origDmg);

        // 写入目标值
        if (hasFullPen) TrySetWeaponPenetration(weapon, 128);   // 极高穿透次数
        if (hasNoDmg)   TrySetWeaponPenDmgModifier(weapon, 1.0f); // 100%伤害保留
    }

    /// <summary>
    /// 如果武器曾被修改（存在 _weaponOrigValues 中），恢复其原始穿透属性并移除备份。
    /// 用于武器落入未开启穿墙功能的玩家手中时自动恢复。
    /// </summary>
    private void RestoreWeaponIfModified(CBasePlayerWeapon weapon)
    {
        if (weapon is not { IsValid: true }) return;
        uint idx = weapon.Index;

        if (!_weaponOrigValues.TryGetValue(idx, out var orig)) return;

        TrySetWeaponPenetration(weapon, orig.origPen);
        TrySetWeaponPenDmgModifier(weapon, orig.origDmg);
        _weaponOrigValues.Remove(idx);
    }

    /// <summary>
    /// 玩家关闭穿墙功能时调用：恢复其当前持有武器的默认属性
    /// （仅当该武器没有被其他开启功能的玩家也持有时才恢复）
    /// </summary>
    private void RestorePlayerActiveWeaponIfNeeded(CCSPlayerController player)
    {
        var weapon = player.PlayerPawn?.Value?.WeaponServices?.ActiveWeapon?.Value;
        if (weapon is not { IsValid: true }) return;

        uint idx = weapon.Index;
        if (!_weaponOrigValues.ContainsKey(idx)) return; // 未被修改过，无需恢复

        // 检查该武器是否还有其他人需要穿透功能
        // 遍历所有玩家，看是否有开启功能的玩家持有同一把枪
        bool stillNeeded = false;
        foreach (var p in Utilities.GetPlayers())
        {
            if (p is not { IsValid: true }) continue;
            if (p.Slot == player.Slot) continue;
            if (!_fullPenPlayers.Contains(p.Slot) && !_noWallDmgReductionPlayers.Contains(p.Slot)) continue;

            var w = p.PlayerPawn?.Value?.WeaponServices?.ActiveWeapon?.Value;
            if (w is { IsValid: true } && w.Index == idx)
            {
                stillNeeded = true;
                break;
            }
        }

        if (!stillNeeded)
            RestoreWeaponIfModified(weapon);
    }

    // ---- 底层 Schema 读写：优先 VData（武器真实参数所在），实体字段作备选 ----
    // VData 是所有同款武器共享的模板数据，修改 VData 会影响全局（本地服可接受）
    // 恢复时用硬编码默认值（2 穿透 / 0.5 伤害倍率），因 Schema.GetSchemaValue 不一定可用

    /// <summary>获取武器 VData 句柄。CS2 武器穿透/射程等参数存储在 VData 而非实体上。</summary>
    private static nint TryGetWeaponVData(CBasePlayerWeapon weapon)
    {
        try { return Schema.GetSchemaValue<nint>(weapon.Handle, "CEntityInstance", "m_pVData"); }
        catch
        {
            try { return Schema.GetSchemaValue<nint>(weapon.Handle, "CBaseEntity", "m_pVData"); }
            catch { return nint.Zero; }
        }
    }

    /// <summary>读取武器当前穿透次数，用于备份。读取失败返回 2。</summary>
    private static int TryGetWeaponPenetration(CBasePlayerWeapon weapon)
    {
        nint vdata = TryGetWeaponVData(weapon);
        if (vdata != nint.Zero)
        {
            try { return Schema.GetSchemaValue<int>(vdata, "CCSWeaponBaseVData", "m_nPenetrationCount"); }
            catch { }
        }
        try { return Schema.GetSchemaValue<int>(weapon.Handle, "CCSWeaponBase", "m_nPenetrationCount"); }
        catch { return 2; }
    }

    /// <summary>读取武器穿透伤害倍率，用于备份。读取失败返回 0.5f。</summary>
    private static float TryGetWeaponPenDmgModifier(CBasePlayerWeapon weapon)
    {
        nint vdata = TryGetWeaponVData(weapon);
        if (vdata != nint.Zero)
        {
            try { return Schema.GetSchemaValue<float>(vdata, "CCSWeaponBaseVData", "m_flPenetrationDamageModifier"); }
            catch { }
        }
        try { return Schema.GetSchemaValue<float>(weapon.Handle, "CCSWeaponBase", "m_flPenetrationDamageModifier"); }
        catch { return 0.5f; }
    }

    /// <summary>
    /// 设置武器穿透次数（全图可穿）—— 带调试日志版。
    /// 穷举所有可能字段名，控制台会打印命中情况，方便排查。命中一次后跳过后续尝试。
    /// </summary>
    private static void TrySetWeaponPenetration(CBasePlayerWeapon weapon, int value)
    {
        nint vdata = TryGetWeaponVData(weapon);
        var handle = weapon.Handle;
        string dn = weapon.DesignerName;
        Console.WriteLine($"[WP Debug] {dn} | entity handle={handle} | vdata={vdata}");

        // === VData 上的穿透字段（穷举） ===
        if (vdata != nint.Zero)
        {
            TrySchemaInt(vdata, "CCSWeaponBaseVData", "m_nPenetrationCount", value,
                $"VData m_nPenetrationCount={value}");
            TrySchemaInt(vdata, "CCSWeaponBaseVData", "m_nPenetration", value,
                $"VData m_nPenetration={value}");
            TrySchemaInt(vdata, "CWeaponCSBaseVData", "m_nPenetrationCount", value,
                $"VData(CWeapon) m_nPenetrationCount={value}");
            TrySchemaInt(vdata, "CCSWeaponBaseVData", "m_iPenetrationCount", value,
                $"VData m_iPenetrationCount={value}");
            TrySchemaFloat(vdata, "CCSWeaponBaseVData", "m_flPenetration", (float)value,
                $"VData m_flPenetration={value}");
            TrySchemaFloat(vdata, "CCSWeaponBaseVData", "m_flPenetrationPower", (float)value,
                $"VData m_flPenetrationPower={value}");

            // VData 射程
            TrySchemaFloat(vdata, "CCSWeaponBaseVData", "m_flRange", 99999f,
                $"VData m_flRange=99999");
            TrySchemaFloat(vdata, "CCSWeaponBaseVData", "m_flMaxRange", 99999f,
                $"VData m_flMaxRange=99999");
            TrySchemaFloat(vdata, "CWeaponCSBaseVData", "m_flRange", 99999f,
                $"VData(CWeapon) m_flRange=99999");
        }

        // === 实体上的穿透字段（穷举） ===
        TrySchemaInt(handle, "CCSWeaponBase", "m_nPenetrationCount", value,
            $"Entity m_nPenetrationCount={value}");
        TrySchemaInt(handle, "CBasePlayerWeapon", "m_nPenetrationCount", value,
            $"Entity(CBase) m_nPenetrationCount={value}");
        TrySchemaInt(handle, "CWeaponCSBase", "m_nPenetrationCount", value,
            $"Entity(CWeapon) m_nPenetrationCount={value}");
        TrySchemaFloat(handle, "CCSWeaponBase", "m_flPenetration", (float)value,
            $"Entity m_flPenetration={value}");
    }

    /// <summary>设置武器穿墙伤害保留倍率（无衰减 = 1.0）—— 带调试日志版。</summary>
    private static void TrySetWeaponPenDmgModifier(CBasePlayerWeapon weapon, float value)
    {
        nint vdata = TryGetWeaponVData(weapon);
        var handle = weapon.Handle;

        if (vdata != nint.Zero)
        {
            TrySchemaFloat(vdata, "CCSWeaponBaseVData", "m_flPenetrationDamageModifier", value,
                $"VData m_flPenetrationDamageModifier={value}");
            TrySchemaFloat(vdata, "CCSWeaponBaseVData", "m_flPenetrationDamage", value,
                $"VData m_flPenetrationDamage={value}");
            TrySchemaFloat(vdata, "CCSWeaponBaseVData", "m_flDamagePenetration", value,
                $"VData m_flDamagePenetration={value}");
        }

        TrySchemaFloat(handle, "CCSWeaponBase", "m_flPenetrationDamageModifier", value,
            $"Entity m_flPenetrationDamageModifier={value}");
        TrySchemaFloat(handle, "CBasePlayerWeapon", "m_flPenetrationDamageModifier", value,
            $"Entity(CBase) m_flPenetrationDamageModifier={value}");
        TrySchemaFloat(handle, "CCSWeaponBase", "m_flPenetrationDamage", value,
            $"Entity m_flPenetrationDamage={value}");
        TrySchemaFloat(handle, "CCSWeaponBase", "m_flDamageWallModifier", value,
            $"Entity m_flDamageWallModifier={value}");
    }

    // ---- Schema 写值小助手（带日志）----
    private static void TrySchemaInt(nint handle, string className, string fieldName, int value, string logMsg)
    {
        try { Schema.SetSchemaValue<int>(handle, className, fieldName, value); Console.WriteLine($"[WP OK] {logMsg}"); }
        catch (Exception ex) { Console.WriteLine($"[WP MISS] {className}::{fieldName} — {ex.Message}"); }
    }
    private static void TrySchemaFloat(nint handle, string className, string fieldName, float value, string logMsg)
    {
        try { Schema.SetSchemaValue<float>(handle, className, fieldName, value); Console.WriteLine($"[WP OK] {logMsg}"); }
        catch (Exception ex) { Console.WriteLine($"[WP MISS] {className}::{fieldName} — {ex.Message}"); }
    }

    // ==================== 暗金JSON持久化 ====================

    private void LoadStatTrakData()
    {
        try
        {
            if (File.Exists(_stattrakJsonPath))
            {
                var json = File.ReadAllText(_stattrakJsonPath);
                _savedKillCounts = JsonSerializer.Deserialize<Dictionary<string, Dictionary<string, int>>>(json) ?? new();
            }
        }
        catch { }
    }

    private void SaveStatTrakData()
    {
        try
        {
            File.WriteAllText(_stattrakJsonPath,
                JsonSerializer.Serialize(_savedKillCounts, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch { }
    }

    private void IncrementJsonKillCount(CCSPlayerController attacker, string designerName)
    {
        string steamId = attacker.SteamID.ToString();
        if (!_savedKillCounts.ContainsKey(steamId))
            _savedKillCounts[steamId] = new Dictionary<string, int>();

        _savedKillCounts[steamId].TryGetValue(designerName, out int cur);
        _savedKillCounts[steamId][designerName] = cur + 1;
        SaveStatTrakData();
    }

    private void OnStattrakJsonCommand(CCSPlayerController? player, CommandInfo info)
    {
        if (player == null || !player.IsValid) return;
        string steamId = player.SteamID.ToString();

        // !stattrak show [武器名]
        if (info.ArgCount >= 2 && info.ArgByIndex(1).ToLower() == "show")
        {
            if (info.ArgCount < 3)
            {
                var pw = player.PlayerPawn?.Value;
                var w = pw?.WeaponServices?.ActiveWeapon?.Value;
                if (w == null || !w.IsValid) { player.PrintToChat(" 无法获取当前武器"); return; }
                string wn = w.DesignerName;
                if (string.IsNullOrEmpty(wn)) return;

                _savedKillCounts.TryGetValue(steamId, out var dict);
                int cnt = 0;
                dict?.TryGetValue(wn, out cnt);
                player.PrintToChat($" {DisplayName(wn)} 累计击杀: {cnt}");
                return;
            }

            string weaponArg = NormalizeWeaponName(info.ArgByIndex(2));
            _savedKillCounts.TryGetValue(steamId, out var allWeapons);
            int count = 0;
            allWeapons?.TryGetValue(weaponArg, out count);
            player.PrintToChat($" {DisplayName(weaponArg)} 累计击杀: {count}");
            return;
        }

        // !stattrak <数字>
        if (info.ArgCount < 2)
        {
            player.PrintToChat(" !stattrak show [武器名]  查看累计击杀");
            player.PrintToChat(" !stattrak <数字>  设置当前武器计数基准");
            return;
        }

        if (!int.TryParse(info.ArgByIndex(1), out int newValue) || newValue < 0)
        {
            player.PrintToChat(" 请输入有效数字（0或正整数）");
            return;
        }

        var pawn = player.PlayerPawn?.Value;
        var weapon = pawn?.WeaponServices?.ActiveWeapon?.Value;
        if (weapon == null || !weapon.IsValid) { player.PrintToChat(" 无法获取当前武器"); return; }

        string weaponName = weapon.DesignerName;
        if (string.IsNullOrEmpty(weaponName)) return;

        if (!_savedKillCounts.ContainsKey(steamId))
            _savedKillCounts[steamId] = new Dictionary<string, int>();
        _savedKillCounts[steamId][weaponName] = newValue;
        SaveStatTrakData();

        player.PrintToChat($" {DisplayName(weaponName)} 计数基准已设为 {newValue}");
    }

    private static string DisplayName(string n) => (n.StartsWith("weapon_") ? n[7..] : n).ToUpperInvariant();
    private static string NormalizeWeaponName(string raw) => raw.ToLowerInvariant().StartsWith("weapon_") ? raw.ToLowerInvariant() : "weapon_" + raw.ToLowerInvariant();
}
