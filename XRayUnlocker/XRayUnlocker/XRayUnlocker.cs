using System.Drawing;
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
/// 本地透视 + 无敌插件 v1.4.0
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
/// 暗金计数（!st）—— v1.4.0 重写为"最高权重"系统：
///   每帧强制覆盖，InvSim 和原版暗金代码均无法干涉。
///   数据结构：slot → DesignerName → value（单层，按武器型号存储）。
/// </summary>
[MinimumApiVersion(80)]
public class XRayUnlockerPlugin : BasePlugin
{
    public override string ModuleName => "XRayUnlocker";
    public override string ModuleVersion => "1.5.0";
    public override string ModuleAuthor => "CS2 Local Server";
    public override string ModuleDescription => "透视 !x + 无敌 !god + 暗金 !st + 防闪 !nf + 蹲起 !sc + BOT数量解锁 + 暗金JSON持久化 !stattrak";

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

    // ==================== BOT数量解锁 ====================
    private const int MaxPerTeam = 32;
    private readonly List<string> _createdSpawnGlobalnames = new();

    // ==================== 暗金计数器（v1.4.0 最高权重 + 实体防感染）====================
    // slot → DesignerName → (EntityIndex, Value)
    // EntityIndex 绑定"自己的枪"实体，击杀时仅匹配实体才递增，捡来的枪杀了不计
    // 每回合开始 EntityIndex 重置为 0，通过购买事件 + 背包唯一性双重验证自动绑定
    // OnTick 每帧强制写回值（显示），InvSim/原版暗金无法干涉
    private readonly Dictionary<int, Dictionary<string, (uint EntityIndex, int Value)>> _statTrakValues = new();
    // 追踪玩家本回合购买的武器（slot → designerName 集合）
    // 用于新回合认领：买过的枪一定属于自己，跳过所有歧义检查
    private readonly Dictionary<int, HashSet<string>> _purchasedThisRound = new();

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
        RegisterEventHandler<EventPlayerDeath>(OnPlayerDeathPost, HookMode.Post);
        RegisterEventHandler<EventItemPurchase>(OnItemPurchase);

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

        Console.WriteLine("[XRayUnlocker] v1.5.0 已加载 | !x !god !st !nf !sc !stattrak | BOT数量解锁已启用");

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
        _purchasedThisRound.Clear();
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
        uint entityIndex = weapon.Index;

        // 写入存储，绑定当前武器实体（用户的"自己的枪"）
        if (!_statTrakValues.TryGetValue(slot, out var typeDict))
        {
            typeDict = new Dictionary<string, (uint, int)>();
            _statTrakValues[slot] = typeDict;
        }
        typeDict[designerName] = (entityIndex, value);

        // !st 命令即认领，标记为本回合购买（最高优先级）
        if (!_purchasedThisRound.TryGetValue(slot, out var purchasedSet))
        {
            purchasedSet = new HashSet<string>();
            _purchasedThisRound[slot] = purchasedSet;
        }
        purchasedSet.Add(designerName);

        ApplyStatTrakValue(weapon, value);
        player.PrintToChat($" [StatTrak] 暗金计数已修改为: {value} (击杀会在设定值上递增)");
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
    /// 检查玩家背包中是否仍持有指定实体索引的武器。
    /// 用于判断旧绑定实体是否已被销毁/丢弃。
    /// </summary>
    private static bool PlayerHasWeaponEntity(CCSPlayerController player, uint entityIndex)
    {
        var weapons = player.PlayerPawn?.Value?.WeaponServices?.MyWeapons;
        if (weapons == null) return false;
        foreach (var w in weapons)
        {
            if (w?.Value?.Index == entityIndex)
                return true;
        }
        return false;
    }

    /// <summary>
    /// 购买事件：记录玩家本回合购买的武器型号。
    /// 购买 = 一定属于自己，后续绑定跳过所有歧义检查。
    /// </summary>
    private HookResult OnItemPurchase(EventItemPurchase @event, GameEventInfo info)
    {
        var player = @event.Userid;
        if (player == null || !player.IsValid) return HookResult.Continue;

        string rawWeapon = @event.Weapon;
        if (string.IsNullOrEmpty(rawWeapon)) return HookResult.Continue;

        int slot = player.Slot;
        string designerName = rawWeapon.StartsWith("weapon_") ? rawWeapon : "weapon_" + rawWeapon;

        if (!_purchasedThisRound.TryGetValue(slot, out var set))
        {
            set = new HashSet<string>();
            _purchasedThisRound[slot] = set;
        }
        set.Add(designerName);

        return HookResult.Continue;
    }

    /// <summary>
    /// 武器实体创建事件：捕获 buy / give 等所有途径获取的武器
    /// 只记录玩家有关心值（_statTrakValues 中有该型号）的武器，不依赖武器自身是否暗金
    /// </summary>
    private void OnWeaponEntityCreated(CEntityInstance entity)
    {
        if (entity is not CBasePlayerWeapon weapon || !weapon.IsValid) return;

        var owner = weapon.OwnerEntity?.Value;
        if (owner is not CCSPlayerController player || !player.IsValid) return;

        int slot = player.Slot;
        string designerName = weapon.DesignerName;

        // 只记录玩家有 ST 值的武器类型（不关心没设过的枪）
        if (!_statTrakValues.TryGetValue(slot, out var typeDict)
            || !typeDict.ContainsKey(designerName))
            return;

        if (!_purchasedThisRound.TryGetValue(slot, out var set))
        {
            set = new HashSet<string>();
            _purchasedThisRound[slot] = set;
        }
        set.Add(designerName);

        // 立即绑定并写值，不等 OnTick，防止切后台时 OnTick 迟迟不触发导致显示真实击杀值
        var entry = typeDict[designerName];
        typeDict[designerName] = (weapon.Index, entry.Value);
        // 如果武器已经是暗金（InvSim 已套皮），立即写值覆盖真实击杀数
        if (WeaponHasStatTrak(weapon))
            ApplyStatTrakValue(weapon, entry.Value);
    }

    /// <summary>
    /// 判断是否应该认领当前武器为自己的枪。
    /// 优先级：1) 本回合购买过 / !st 设置过 → 一定属于自己
    ///         2) EntityIndex 匹配 → 已绑定，无需认领
    ///         3) EntityIndex==0 且武器已带有我们的暗金值 → 从上回合保留
    ///         其余情况 → 不认领（歧义，可能是别人的枪）
    /// </summary>
    private bool ShouldClaimWeapon(CCSPlayerController player, CBasePlayerWeapon weapon, uint boundEntityIndex, string designerName, int storedValue)
    {
        int slot = player.Slot;

        // 本回合购买过 / !st 设置过 → 一定属于自己
        if (_purchasedThisRound.TryGetValue(slot, out var purchasedSet)
            && purchasedSet.Contains(designerName))
        {
            purchasedSet.Remove(designerName); // 消费掉，避免重复认领
            return true;
        }

        // 已绑定且匹配
        if (boundEntityIndex != 0 && boundEntityIndex == weapon.Index)
            return true;

        // 未绑定 + 武器已带有我们的暗金指纹 → 从上回合保留的自己的枪
        if (boundEntityIndex == 0 && weapon.FallbackStatTrak == storedValue)
            return true;

        return false;
    }

    /// <summary>
    /// PostHook：从事件获取击杀武器名，检查实体是否为自己绑定的枪。
    /// 捡来的枪杀了人不递增，只有"自己的枪"才递增。
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

        if (!_statTrakValues.TryGetValue(slot, out var typeDict)
            || !typeDict.TryGetValue(designerName, out var entry))
            return HookResult.Continue;

        var weapon = attacker.PlayerPawn?.Value?.WeaponServices?.ActiveWeapon?.Value;
        if (weapon is not { IsValid: true })
            return HookResult.Continue;

        // 多条件综合判断：这枪属于自己吗？
        if (!ShouldClaimWeapon(attacker, weapon, entry.EntityIndex, designerName, entry.Value))
            return HookResult.Continue;

        int newValue = entry.Value + 1;
        typeDict[designerName] = (weapon.Index, newValue);
        ApplyStatTrakValue(weapon, newValue);

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

        // 新回合开始，重置所有暗金实体绑定（玩家会买新枪，旧 EntityIndex 失效）
        foreach (var typeDict in _statTrakValues.Values)
        {
            var keys = typeDict.Keys.ToList();
            foreach (var key in keys)
            {
                var entry = typeDict[key];
                typeDict[key] = (0, entry.Value); // 保留值，清空实体绑定
            }
        }

        // 清空本回合购买记录
        _purchasedThisRound.Clear();

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
        bool hasGod = _godPlayers.Count > 0;
        bool hasXray = _xrayUsers.Count > 0;
        bool hasStatTrak = _statTrakValues.Count > 0;
        bool hasNoFlash = _noFlashPlayers.Count > 0;
        bool hasStamina = _noStaminaPlayers.Count > 0;
        if (!hasGod && !hasXray && !hasStatTrak && !hasNoFlash && !hasStamina) return;

        _tickCounter++;

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

            // 暗金覆盖与绑定：
            // 只对自己绑定的武器写值，不污染别人的枪
            if (hasStatTrak && _statTrakValues.TryGetValue(slot, out var typeDict))
            {
                var weapon = player.PlayerPawn?.Value?.WeaponServices?.ActiveWeapon?.Value;
                if (weapon is { IsValid: true } && WeaponHasStatTrak(weapon))
                {
                    string designerName = weapon.DesignerName;
                    if (typeDict.TryGetValue(designerName, out var entry))
                    {
                        // 尝试认领（购买记录 / 已绑定 / 暗金指纹匹配）
                        if (ShouldClaimWeapon(player, weapon, entry.EntityIndex, designerName, entry.Value))
                        {
                            // 认领成功 → 绑定 + 写值
                            if (entry.EntityIndex != weapon.Index)
                                typeDict[designerName] = (weapon.Index, entry.Value);
                            ApplyStatTrakValue(weapon, entry.Value);
                        }
                        // 未认领 → 不写值（不污染别人的枪）
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
