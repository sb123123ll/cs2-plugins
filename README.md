# 为完善CS2 InventorySimulator 和 CS2-Bot-Improver 功能而开发的插件

> **这不是外挂。** 所有功能仅限本地自建房使用，无法在官方服务器或社区服务器生效。用途：装高手、整蛊朋友😂。

需要以CounterStrikeSharp为前置插件，下载地址：[CounterStrikeSharp](https://github.com/roflmuffin/CounterStrikeSharp/releases)

| 插件 | 功能 |
|------|------|
| **XRayUnlocker** | `!x` 透视 / `!god` 无敌 / `!st` 修改暗金计数 / `!nf` 防闪 / `!sc` 无限蹲起 / `!wp` 全图可穿 / `!wd` 穿墙无衰减 / `!stattrak` 暗金持久化 / BOT数量解锁 |

---

## 重要：请搭配 InventorySimulator 和 CS2-Bot-Improver 使用
**换个说法，本插件就是为了这个插件而开发的。修复了InventorySimulator不能自定义暗金计数器击杀数的弊端（仅限当局，新开可以重新输入指令），并且加入了本地上的无敌透视和防闪光白屏功能，防止你被增强人机打红温。或者你也可以用它来录个视频装高手、整蛊朋友😂**
---

## 环境要求

- **[CounterStrikeSharp](https://github.com/roflmuffin/CounterStrikeSharp/releases)**（MinimumApiVersion 80+）
  - 安装到 CS2 的 `game/csgo/` 目录
- **[InventorySimulator](https://github.com/ianlucas/cs2-inventory-simulator)**（本插件为此开发）
  - 给它赋予了暗金计数修改功能
- **[CS2-Bot-Improver](https://github.com/ed0ard/CS2-Bot-Improver)**（本插件为此开发）
(以上所说的两个插件安装好之后。直接粘贴并替换进 CS2 的 `game/csgo/` 目录）

---

## 安装

将 `bin/Release/net10.0/` 下的 `*.dll` 复制到对应目录：

```
game/csgo/addons/counterstrikesharp/plugins/
└── XRayUnlocker/
    ├── XRayUnlocker.dll
    └── stattrak_data.json
```

---

## 指令

### XRayUnlocker

| 指令 | 说明 |
|------|------|
| `!x` 或 `x 1/0` | 开关 X 光透视（敌我全体可见） |
| `!god` 或 `god 1/0` | 开关无敌模式（不掉血、不抖动、不减速） |
| `!st <数字>` 或 `st <数字>` | 修改当前武器暗金计数器，击杀在设定值上递增 |
| `!nf` 或 `nf 1/0` | 开关防闪光白屏 |
| `!sc` 或 `sc 1/0` | 开关无限蹲起（无体力无冷却，如瓦罗兰特） |
| `!wp` 或 `wp 1/0` | 开关全图可穿（子弹穿透任何掩体/材质/地板） |
| `!wd` 或 `wd 1/0` | 开关穿墙无衰减（穿墙不减伤害，仅保留距离衰减） |
| `!stattrak show` | 查看当前武器累计击杀（JSON 持久化） |
| `!stattrak show <武器名>` | 查看指定武器累计击杀（如 `!stattrak show ak47`） |
| `!stattrak <数字>` | 设置当前武器 JSON 计数基准 |
| — | BOT 数量自动解锁（制作额外出生点，每方最多 32 人） |

---

## 声明

- **不是外挂，不是作弊器。** 所有功能依赖 CounterStrikeSharp 服务端插件框架，仅本地自建房有效。
- 官方服务器、社区服务器、Faceit、完美、5e 等平台均无法使用，也没有任何绕过 VAC 的能力。
- 用途：开人机房自娱自乐、录素材装高手、或者喊朋友来本地房然后开透视掏出999999击杀的淬火ak暗金661贴5个titan的贴纸吓他一跳😂。
- 有问题发issue反馈

## 如何恢复

- 虽然插件开源无毒，如果要是实在担心有病毒或者玩腻了想打正常匹配了但是进不去了，只需要把**game/csgo/**内的**addons**文件整个删除，然后让steam验证游戏的文件完整性

## 构建

```bash
dotnet build -c Release
```
当然你去Release下构建好的就行，安装上面也有写👍

`.csproj` 中 CounterStrikeSharp API 的 `HintPath` 按需修改。
