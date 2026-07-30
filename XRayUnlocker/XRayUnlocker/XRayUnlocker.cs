using System.Drawing;
using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Core.Attributes;
using CounterStrikeSharp.API.Core.Attributes.Registration;
using CounterStrikeSharp.API.Modules.Commands;
using CounterStrikeSharp.API.Modules.Memory;
using CounterStrikeSharp.API.Modules.Memory.DynamicFunctions;
using CounterStrikeSharp.API.Modules.Utils;

namespace XRayUnlocker;

/// <summary>
/// 本地透视 + 无敌插件 v1.3.2
/// 
/// 玩家透视（!x）：
///   创建不可见 prop_dynamic 幽灵发光实体跟随玩家模型，
///   通过 CheckTransmit 按玩家过滤，观战者不可见。
///
/// 无敌模式（!god）：
///   ——输入生效，与 CS2 内置 buddha 不同，
///   不掉血、不抖动、不减速，子弹正常命中，击中效果完整。
///   机制：OnPlayerTakeDamagePre 在引擎层面将伤害归零。
/// </summary>
[MinimumApiVersion(80)]
public class XRayUnlockerPlugin : BasePlugin
{
    public override string ModuleName => "XRayUnlocker";
    public override string ModuleVersion => "1.3.2";
    public override string ModuleAuthor => "CS2 Local Server";
    public override string ModuleDescription => "本地透视 !x + 无敌 !god + 暗金 !st";

    // ==================== 玩家透视 ====================
    private readonly Dictionary<int, (CDynamicProp relay, CDynamicProp glow)> _playerGlows = new();
    private readonly HashSet<int> _xrayUsers = new();

    // ==================== 无敌模式 ====================
    private readonly HashSet<int> _godPlayers = new();
    // pawn.Index → player.Slot 快速映射（OnPlayerTakeDamagePre 参数是 pawn 不是 controller）
    private readonly Dictionary<uint, int> _pawnToSlot = new();

    // ==================== 防闪光 ====================
    private readonly HashSet<int> _noFlashPlayers = new();

    // ==================== 暗金计数器 ====================
    // 双层存储：
    //   _statTrakByEntity：按武器实体追踪（回合内区分不同实体，捡队友枪不感染）
    //   _statTrakByType：   按武器型号兜底（跨回合/重买后恢复，同型号取最后一次设定值）
    private readonly Dictionary<int, Dictionary<uint, int>> _statTrakByEntity = new();
    private readonly Dictionary<int, Dictionary<string, int>> _statTrakByType = new();

    /// <summary>
    /// 获取指定武器实例的暗金计数值。
    /// 优先按实体 Index 查找（回合内区分不同实体），
    /// 找不到则按 DesignerName 兜底（跨回合/重买后恢复），
    /// 兜底成功时自动迁移到实体索引。
    /// </summary>
    private int? GetStatTrakValue(int slot, CBasePlayerWeapon weapon)
    {
        uint entityIndex = weapon.Index;
        int val;

        // 优先按实体查找
        if (_statTrakByEntity.TryGetValue(slot, out var entityDict)
            && entityDict.TryGetValue(entityIndex, out val))
            return val;

        // 跨回合兜底：按型号查找并迁移
        string designerName = weapon.DesignerName;
        if (_statTrakByType.TryGetValue(slot, out var typeDict)
            && typeDict.TryGetValue(designerName, out val))
        {
            if (!_statTrakByEntity.TryGetValue(slot, out entityDict))
            {
                entityDict = new Dictionary<uint, int>();
                _statTrakByEntity[slot] = entityDict;
            }
            entityDict[entityIndex] = val;
            return val;
        }

        return null;
    }

    /// <summary>
    /// 设置指定武器实例的暗金计数值，同时更新实体层和型号层。
    /// </summary>
    private void SetStatTrakValue(int slot, CBasePlayerWeapon weapon, int value)
    {
        uint entityIndex = weapon.Index;
        if (!_statTrakByEntity.TryGetValue(slot, out var entityDict))
        {
            entityDict = new Dictionary<uint, int>();
            _statTrakByEntity[slot] = entityDict;
        }
        entityDict[entityIndex] = value;

        string designerName = weapon.DesignerName;
        if (!_statTrakByType.TryGetValue(slot, out var typeDict))
        {
            typeDict = new Dictionary<string, int>();
            _statTrakByType[slot] = typeDict;
        }
        typeDict[designerName] = value;
    }

    // ==================== 暗金计数命令 ====================

    private static readonly MemoryFunctionWithReturn<nint, string, float, int> _setAttributeValueByName =
        new(GameData.GetSignature("CAttributeList::SetOrAddAttributeValueByName"));

    // ==================== 生命周期 ====================

    public override void Load(bool hotReload)
    {
        RegisterListener<Listeners.OnTick>(OnTick);
        RegisterListener<Listeners.CheckTransmit>(OnCheckTransmit);
        RegisterListener<Listeners.OnPlayerTakeDamagePre>(OnPlayerTakeDamagePre);
        RegisterEventHandler<EventPlayerSpawn>(OnPlayerSpawn);
        RegisterEventHandler<EventPlayerDisconnect>(OnPlayerDisconnect);
        RegisterEventHandler<EventRoundStart>(OnRoundStart);
        RegisterEventHandler<EventPlayerDeath>(OnPlayerDeathPost, HookMode.Post);

        AddCommand("css_x", "开关X光透视（敌我全体可见）", OnXRayCommand);
        AddCommand("css_god", "开关无敌模式（不掉血不抖动不减速）", OnGodCommand);
        AddCommand("x", "控制台透视开关: x 1 开启, x 0 关闭", OnXConsoleCommand);
        AddCommand("god", "控制台无敌开关: god 1 开启, god 0 关闭", OnGodConsoleCommand);
        AddCommand("css_st", "修改当前武器暗金计数器: st <数字>", OnStatTrakCommand);
        AddCommand("st", "控制台修改暗金计数器: st <数字>", OnStatTrakConsoleCommand);
        AddCommand("css_nf", "开关防闪光白屏", OnNoFlashCommand);
        AddCommand("nf", "控制台防闪光: nf 1 开启, nf 0 关闭", OnNoFlashConsoleCommand);

        Console.WriteLine("[XRayUnlocker] v1.3.2 已加载 | !x 透视 | !god 无敌 | !st 暗金 | !nf 防闪 | 控制台: x/god/st/nf");

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
        }
    }

    public override void Unload(bool hotReload)
    {
        DestroyAllPlayerGlows();
        _xrayUsers.Clear();
        _godPlayers.Clear();
        _noFlashPlayers.Clear();
        _pawnToSlot.Clear();
        _statTrakByEntity.Clear();
        _statTrakByType.Clear();
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

        SetStatTrakValue(player.Slot, weapon, value);
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
    /// 将暗金计数值写入武器，同时覆盖原生 FallbackStatTrak 和 InvSim 的 kill eater 属性。
    /// </summary>
    private void ApplyStatTrakValue(CBasePlayerWeapon weapon, int value)
    {
        weapon.FallbackStatTrak = value;
        Utilities.SetStateChanged(weapon, "CCSWeaponBase", "m_nFallbackStatTrak");

        var dynAttrs = weapon.AttributeManager?.Item?.NetworkedDynamicAttributes;
        if (dynAttrs != null)
        {
            nint handle = ((NativeObject)(object)dynAttrs).Handle;
            float floatValue = BitConverter.Int32BitsToSingle(value);
            _setAttributeValueByName.Invoke(handle, "kill eater", floatValue);
            _setAttributeValueByName.Invoke(handle, "kill eater score type", 0f);
            Utilities.SetStateChanged(weapon, "CBasePlayerWeapon", "m_AttributeManager");
        }
    }

    /// <summary>
    /// PostHook on EventPlayerDeath：InvSim/CS2 先递增真实值，
    /// 我们在其后用自定义值覆盖（自定义值 + 1），仅影响当前武器类型。
    /// </summary>
    private HookResult OnPlayerDeathPost(EventPlayerDeath @event, GameEventInfo info)
    {
        var attacker = @event.Attacker;
        if (attacker == null || !attacker.IsValid) return HookResult.Continue;
        // 攻击者和受害者不能是同一人（自杀、摔死不递增）
        if (@event.Userid != null && attacker.Slot == @event.Userid.Slot)
            return HookResult.Continue;

        int slot = attacker.Slot;

        // 延迟一帧确保 InvSim 已写入完成，再获取击杀武器并递增
        AddTimer(0.0f, () =>
        {
            var weapon = attacker.PlayerPawn?.Value?.WeaponServices?.ActiveWeapon?.Value;
            if (weapon is not { IsValid: true }) return;

            int? currentValue = GetStatTrakValue(slot, weapon);
            if (!currentValue.HasValue) return;

            int newValue = currentValue.Value + 1;
            SetStatTrakValue(slot, weapon, newValue);
            ApplyStatTrakValue(weapon, newValue);
        });

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
    /// 不清理功能开关状态（_xrayUsers/_godPlayers/_noFlashPlayers/_statTrakCustomValues），
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

        if (!_pawnToSlot.TryGetValue(pawn.Index, out int slot))
            return HookResult.Continue;

        if (!_godPlayers.Contains(slot)) return HookResult.Continue;

        info.Damage = 0f;

        return HookResult.Continue;
    }

    // ==================== OnTick：兜底状态同步 ====================

    private int _tickCounter;

    /// <summary>
    /// 每帧：无敌 HP/Armor 兜底 + NoFlash 归零 + 64帧周期 XRay glow 修复 / StatTrak 防覆盖。
    /// </summary>
    private void OnTick()
    {
        bool hasGod = _godPlayers.Count > 0;
        bool hasXray = _xrayUsers.Count > 0;
        bool hasStatTrak = _statTrakByType.Count > 0;
        bool hasNoFlash = _noFlashPlayers.Count > 0;
        if (!hasGod && !hasXray && !hasStatTrak && !hasNoFlash) return;

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

            // 防闪光：每帧将 FlashDuration 归零（引擎在持续递减，必须每帧覆盖）
            if (hasNoFlash && _noFlashPlayers.Contains(slot))
            {
                var pawn = player.PlayerPawn?.Value;
                if (pawn is { IsValid: true })
                    pawn.FlashDuration = 0f;
            }

            // 每 64 帧：XRay glow 缺失修复 / StatTrak 被 InvSim 覆盖时重新应用
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

                if (hasStatTrak)
                {
                    var weapon = player.PlayerPawn?.Value?.WeaponServices?.ActiveWeapon?.Value;
                    if (weapon is { IsValid: true } && WeaponHasStatTrak(weapon))
                    {
                        int? expected = GetStatTrakValue(slot, weapon);
                        if (expected.HasValue && weapon.FallbackStatTrak != expected.Value)
                            ApplyStatTrakValue(weapon, expected.Value);
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
}
