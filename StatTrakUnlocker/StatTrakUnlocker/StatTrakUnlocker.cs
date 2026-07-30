using System.Text.Json;
using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Core.Attributes;
using CounterStrikeSharp.API.Core.Attributes.Registration;
using CounterStrikeSharp.API.Modules.Commands;
using CounterStrikeSharp.API.Modules.Extensions;

namespace StatTrakUnlocker;

/// <summary>
/// 暗金计数插件 - v3.5.0
/// 
/// 核心功能：
/// - invsim_stattrak_ignore_bots 0 → BOT击杀计入暗金LED（CS2原生通道）
/// - JSON持久化记录所有击杀数据，重开不丢
/// - !stattrak show 查看各武器累计击杀
/// - !stattrak 数字 手动设置JSON计数基准值
/// 
/// 已知限制：
/// - m_nFallbackStatTrak 不是网络同步字段，服务器无法直接修改客户端LED
/// - LED显示由CS2原生击杀模块自动维护（你自己武器的LED会随击杀增长）
/// - 捡来的人机暗金武器LED不会更新（人机无库存数据）
/// </summary>
[MinimumApiVersion(80)]
public class StatTrakUnlockerPlugin : BasePlugin
{
    public override string ModuleName => "StatTrakUnlocker";
    public override string ModuleVersion => "3.5.0";
    public override string ModuleAuthor => "CS2 Local Server";
    public override string ModuleDescription => "暗金计数：BOT击杀计入LED，JSON持久化，!stattrak show查看";

    private Dictionary<string, Dictionary<string, int>> _savedCounts = new();
    private string _dataFilePath = string.Empty;

    public override void Load(bool hotReload)
    {
        _dataFilePath = Path.Combine(ModuleDirectory, "stattrak_data.json");
        LoadData();

        Server.ExecuteCommand("invsim_stattrak_ignore_bots 0");

        RegisterEventHandler<EventPlayerDeath>(OnPlayerDeath, HookMode.Post);

        AddCommand("css_stattrak", "暗金计数管理", OnStattrakCommand);

        Console.WriteLine($"[StatTrakUnlocker] v3.5.0 已加载");
    }

    // ==================== 数据持久化 ====================

    private void LoadData()
    {
        try
        {
            if (File.Exists(_dataFilePath))
            {
                var json = File.ReadAllText(_dataFilePath);
                _savedCounts = JsonSerializer.Deserialize<Dictionary<string, Dictionary<string, int>>>(json) ?? new();
            }
        }
        catch { }
    }

    private void SaveData()
    {
        try
        {
            File.WriteAllText(_dataFilePath,
                JsonSerializer.Serialize(_savedCounts, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch { }
    }

    // ==================== !stattrak 命令 ====================

    private void OnStattrakCommand(CCSPlayerController? player, CommandInfo info)
    {
        if (player == null || !player.IsValid) return;
        string steamId = player.SteamID.ToString();

        // !stattrak show [武器名] —— 查看累计计数
        if (info.ArgCount >= 2 && info.ArgByIndex(1).ToLower() == "show")
        {
            if (info.ArgCount < 3)
            {
                // 显示当前手持武器
                var pw = player.PlayerPawn?.Value;
                var w = pw?.WeaponServices?.As<CCSPlayer_WeaponServices>()?.ActiveWeapon?.Value;
                if (w == null || !w.IsValid) { player.PrintToChat(" 无法获取当前武器"); return; }
                string wn;
                try { wn = CBasePlayerWeaponExtensions.GetWeaponName(w); }
                catch { player.PrintToChat(" 无法识别武器"); return; }
                if (string.IsNullOrEmpty(wn)) return;

                _savedCounts.TryGetValue(steamId, out var dict);
                int cnt = 0;
                dict?.TryGetValue(wn, out cnt);
                player.PrintToChat($" {DisplayName(wn)} 累计击杀: {cnt}");
                return;
            }

            string weaponArg = NormalizeWeaponName(info.ArgByIndex(2));
            _savedCounts.TryGetValue(steamId, out var allWeapons);
            int count = 0;
            allWeapons?.TryGetValue(weaponArg, out count);
            player.PrintToChat($" {DisplayName(weaponArg)} 累计击杀: {count}");
            return;
        }

        // !stattrak <数字> —— 设置JSON计数基准值
        if (info.ArgCount < 2)
        {
            player.PrintToChat(" bstattrak show [武器名]  查看累计击杀");
            player.PrintToChat(" bstattrak <数字>  设置当前武器计数基准");
            return;
        }

        if (!int.TryParse(info.ArgByIndex(1), out int newValue) || newValue < 0)
        {
            player.PrintToChat(" 请输入有效数字（0或正整数）");
            return;
        }

        var pawn = player.PlayerPawn?.Value;
        var weapon = pawn?.WeaponServices?.As<CCSPlayer_WeaponServices>()?.ActiveWeapon?.Value;
        if (weapon == null || !weapon.IsValid) { player.PrintToChat(" 无法获取当前武器"); return; }

        string weaponName;
        try { weaponName = CBasePlayerWeaponExtensions.GetWeaponName(weapon); }
        catch { player.PrintToChat(" 无法识别武器"); return; }
        if (string.IsNullOrEmpty(weaponName)) return;

        if (!_savedCounts.ContainsKey(steamId))
            _savedCounts[steamId] = new Dictionary<string, int>();
        _savedCounts[steamId][weaponName] = newValue;
        SaveData();

        string display = DisplayName(weaponName);
        Console.WriteLine($"[StatTrak] {player.PlayerName} JSON设置 {display} = {newValue}");
        player.PrintToChat($" {display} 计数基准已设为 {newValue}");
    }

    // ==================== 击杀事件 ====================

    private HookResult OnPlayerDeath(EventPlayerDeath @event, GameEventInfo info)
    {
        var attacker = @event.Attacker;
        var victim = @event.Userid;

        if (attacker == null || !attacker.IsValid || attacker.IsBot) return HookResult.Continue;
        if (victim == null || !victim.IsValid) return HookResult.Continue;
        if (attacker == victim) return HookResult.Continue;

        string weaponName = @event.Weapon;
        if (string.IsNullOrEmpty(weaponName)) return HookResult.Continue;

        string steamId = attacker.SteamID.ToString();
        if (!_savedCounts.ContainsKey(steamId))
            _savedCounts[steamId] = new Dictionary<string, int>();

        _savedCounts[steamId].TryGetValue(weaponName, out int cur);
        int newCount = cur + 1;
        _savedCounts[steamId][weaponName] = newCount;
        SaveData();

        string victimType = victim.IsBot ? "BOT" : "玩家";
        Console.WriteLine($"[StatTrak] {attacker.PlayerName} {DisplayName(weaponName)} 击杀{victimType}: {cur} -> {newCount}");
        return HookResult.Continue;
    }

    // ==================== 工具方法 ====================

    private static string DisplayName(string n)
    {
        return (n.StartsWith("weapon_") ? n[7..] : n).ToUpperInvariant();
    }

    private static string NormalizeWeaponName(string raw)
    {
        var lower = raw.ToLowerInvariant();
        return lower.StartsWith("weapon_") ? lower : "weapon_" + lower;
    }
}
