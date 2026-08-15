# XRayUnlocker

> 为完善 [InventorySimulator](https://github.com/ianlucas/cs2-inventory-simulator) 和 [CS2-Bot-Improver](https://github.com/ed0ard/CS2-Bot-Improver) 而开发的本地自建房辅助插件。
>
> **这不是外挂。** 所有功能仅限本地自建房使用，无法在官方服务器或社区服务器生效。用途：装高手、整蛊朋友 😂。

## 功能总览

| 插件 | 功能 |
|------|------|
| **XRayUnlocker** | `!x` 透视 / `!god` 无敌 / `!st` 修改暗金计数 / `!nf` 防闪 / `!sc` 无限蹲起 / `!wp` 全图可穿 / `!wd` 穿墙无衰减 / `!cs` 自动急停 / `!mb` 魔法子弹 / `!dx` 掉落物透视 / `!cx` C4透视 / `!inv` 隐身 / `!fb` 秒下包秒拆弹 / `!stattrak` 暗金持久化 / BOT数量解锁 |

本插件修复了 InventorySimulator 不能自定义暗金计数器击杀数的问题，并加入无敌、透视、防闪等功能，防止被增强人机打红温。

## 环境要求

- **[CounterStrikeSharp](https://github.com/roflmuffin/CounterStrikeSharp/releases)**（MinimumApiVersion 80+），安装到 CS2 的 `game/csgo/` 目录
- **[InventorySimulator](https://github.com/ianlucas/cs2-inventory-simulator)**
- **[CS2-Bot-Improver](https://github.com/ed0ard/CS2-Bot-Improver)**

将以上插件安装后，把本插件的构建产物直接粘贴替换进 `game/csgo/` 目录即可。

## 安装

将 `bin/Release/net10.0/` 下的 `XRayUnlocker.dll` 复制到：

```
game/csgo/addons/counterstrikesharp/plugins/XRayUnlocker/
├── XRayUnlocker.dll
└── stattrak_data.json
```

## 指令

| 指令 | 说明 |
|------|------|
| `!x` / `x 1/0` | 开关 X 光透视（敌我全体可见） |
| `!god` / `god 1/0` | 开关无敌模式（不掉血、不抖动、不减速） |
| `!st <数字>` / `st <数字>` | 修改当前武器暗金计数器，击杀在设定值上递增 |
| `!nf` / `nf 1/0` | 开关防闪光白屏 |
| `!sc` / `sc 1/0` | 开关无限蹲起（无体力无冷却，如瓦罗兰特） |
| `!wp` / `wp 1/0` | 开关全图可穿（子弹穿透任何掩体/材质/地板） |
| `!wd` / `wd 1/0` | 开关穿墙无衰减（穿墙不减伤害，仅保留距离衰减） |
| `!cs` / `cs 1/0` | 开关自动急停（松键瞬间停稳，无需反方向键） |
| `!mb` / `mb 1/0` | 开关魔法子弹（瞄准大致方向自动命中最近敌人，65% 爆头） |
| `!dx` / `dx 1/0` | 开关掉落物透视（地上武器/道具/拆弹器 淡黄边框） |
| `!cx` / `cx 1/0` | 开关 C4 透视（已激活 C4 绿色闪烁边框） |
| `!inv` / `inv 1/0` | 开关隐身（模型/枪械/手套/影子消失，BOT 尽量不察觉） |
| `!fb` / `fb 1/0` | 开关秒下包/秒拆弹（0.1 秒，不分阵营，CT 拿 C4 也能秒下） |
| `!stattrak show` | 查看当前武器累计击杀（JSON 持久化） |
| `!stattrak show <武器名>` | 查看指定武器累计击杀（如 `!stattrak show ak47`） |
| `!stattrak <数字>` | 设置当前武器 JSON 计数基准 |
| — | BOT 数量自动解锁（制作额外出生点，每方最多 32 人） |

## 已知问题

> 以下功能在部分场景（如创意工坊地图）下可能不稳定，请谨慎使用：

- **魔法子弹 `!mb`** — 有些问题，很出戏
- **掉落物透视 `!dx`** — 同上
- **C4 透视 `!cx`** — 同上
- **隐身 `!inv`** — 视觉层面彻底消失（模型/枪械/手套/影子全隐藏）；服务端已尝试让 BOT 忽略你（FL_NOTARGET），但人机感知机制复杂，仍可能被察觉，只能保证骗过真人玩家

## 声明与恢复

- **不是外挂，不是作弊器。** 所有功能依赖 CounterStrikeSharp 服务端插件框架，仅本地自建房有效；官方、社区、Faceit、完美、5e 等平台均无法使用，也没有任何绕过 VAC 的能力。
- 用途：开人机房自娱自乐、录素材装高手、喊朋友来本地房整蛊 😂。
- 有问题发 issue 反馈。

**恢复：** 若想打正常匹配，删除 `game/csgo/` 内的 `addons` 文件夹，再让 Steam 验证游戏文件完整性即可。
