# CS2 InventorySimulator 配套插件

> **这不是外挂。** 所有功能仅限本地自建房使用，无法在官方服务器或社区服务器生效。用途：装高手、整蛊朋友😂。

需要以CounterStrikeSharp为前置插件，下载地址：[CounterStrikeSharp](https://github.com/roflmuffin/CounterStrikeSharp/releases)

| 插件 | 功能 |
|------|------|
| **XRayUnlocker** | `!x` 透视 / `!god` 无敌 / `!st` 修改暗金计数 / `!nf` 闪光不白屏 |
| **StatTrakUnlocker** | 打人机也增加暗金计数，JSON 持久化，`!stattrak show` 查看累计战绩 |

---

## 重要：请搭配 InventorySimulator 和 CS2-Bot-Improver 使用
如果你没下，那就去https://github.com/ed0ard/CS2-Bot-Improver和https://github.com/ianlucas/cs2-inventory-simulator

**换个说法，本插件就是为了这两个插件而开发的。解决了InventorySimulator不能自定义暗金计数器击杀数的弊端，并且加入了本地上的无敌透视和防闪光白屏功能，防止你被增强人机打红温。或者你也可以用它来录个视频装高手、整蛊朋友😂**
---

## 环境要求

- **CounterStrikeSharp**（MinimumApiVersion 80+）
  - 下载：https://github.com/roflmuffin/CounterStrikeSharp/releases
  - 安装到 CS2 的 `game/csgo/` 目录
- **InventorySimulator**（强烈推荐，暗金功能刚需）
  - 启用皮肤和暗金武器后，`!st` 才能真正在刀面/枪身上看到效果
- **[CS2-Bot-Improver](https://github.com/ed0ard/CS2-Bot-Improver)**（强烈推荐，让 BOT 更像真人）

---

## 安装

将 `bin/Release/net10.0/` 下的 `*.dll` 复制到对应目录：

```
game/csgo/addons/counterstrikesharp/plugins/
├── XRayUnlocker/
│   └── XRayUnlocker.dll
└── StatTrakUnlocker/
    └── StatTrakUnlocker.dll
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

### StatTrakUnlocker

| 指令 | 说明 |
|------|------|
| `!stattrak show` | 查看当前武器累计击杀 |
| `!stattrak show <武器名>` | 查看指定武器累计击杀（如 `!stattrak show ak47`） |
| `!stattrak <数字>` | 设置当前武器 JSON 计数基准 |

---

## 声明

- **不是外挂，不是作弊器。** 所有功能依赖 CounterStrikeSharp 服务端插件框架，仅本地自建房有效。
- 官方服务器、社区服务器、Faceit 等平台均无法使用，也没有任何绕过 VAC 的能力。
- 用途：自娱自乐、录素材装高手、喊朋友来本地房然后开透视吓他一跳。

## 构建

```bash
dotnet build -c Release
```

`.csproj` 中 CounterStrikeSharp API 的 `HintPath` 按需修改。
