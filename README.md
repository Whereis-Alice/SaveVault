# Save Vault

一个 Playnite 扩展，把游戏存档以**带版本的快照**形式集中备份到一个文件夹里。重点不在「复制文件」这一步——重点在**替一个 Playnite 游戏库自动找出存档到底在哪**，包括那些没人给它们建过数据库的日系小众作品与同人游戏。

> 适用于 Playnite 10.x（Desktop 模式）。当前版本 1.0.0。中文名「存档方舟」。

---

## 为什么要再写一个

|  | Ludusavi / GameSave Manager | Steam 云 | 依赖外部工具的存档插件 | **Save Vault** |
|---|---|---|---|---|
| 覆盖范围 | 数据库里有的标题 | 支持云存档的 Steam 游戏 | 同其后端工具 | **由检测决定，不依赖数据库** |
| 数据库里没有时 | 什么也不做 | 不适用 | 什么也不做 | **五层启发式 + 游玩时学习** |
| 版本化快照 | 有 | 只有最新 | 视工具而定 | 有（zip + 清单） |
| 空间上限 | 无 | 云配额 | 无 | **全局配额，超了删最旧** |
| 详情页面板 | 无 | 无 | 少数有 | 有，且样式可被主题接管 |
| 独立运行 | 需要装 Ludusavi | — | 需要外部工具 | **零外部依赖** |
| 中文界面 | 部分 | 有 | 部分 | 有（en_US / zh_CN） |

Ludusavi 这类工具的能力上限就是它的清单。实测本机那份清单是 17.5 MB、53 067 个标题、13 422 个有存档数据，但 9-nine、秽翼的尤斯蒂娅、ティンクル☆くるせいだーす、anemoi、Duel Savior Justice、家族計画 一个都查不到。对一个 galgame 库来说，这类工具基本等于不存在。

所以这个插件把力气全花在**检测**上，把 Ludusavi 的清单降级成「六层线索里的其中一层」。

---

## 存档位置怎么找出来的

六层来源，按可信度从高到低合并。同一路径由高可信来源覆盖低可信来源，**已经在列表里的位置永远不会被后续扫描删掉**：

| # | 来源 | 说明 |
|---|---|---|
| 0 | **手动添加** | 你自己选的路径，最高优先 |
| 1 | **游玩时学习** | 游戏运行期间监视安装目录与常见存档根目录，把真正发生写入的文件夹记下来 |
| 2 | **Ludusavi 清单** | 只读取那个 yaml 文件，不调用 Ludusavi，也不要求安装它 |
| 3 | **安装目录** | 向下三层查找 save / saves / savedata / セーブ / 存档 / 저장 等目录名 |
| 4 | **用户目录** | 在 文档 / AppData（Roaming + Local + LocalLow）/ Saved Games 等位置向下两层匹配游戏名 |
| 5 | **注册表推测** | HKCU\Software\<发行商>\<游戏名> 一类的常见位置 |

补充规则：

- **散装存档文件**：安装根目录里出现 `save*.dat` / `*.sav` / `*.svd` / `*.qsv` / `*.ksd` / `envdata` / `global.dat` 之类的文件时，记下的是「安装根目录 + 文件过滤器」，**只备份匹配的文件，绝不会把整个游戏目录打包**。
- **名字归一化**：游戏名、排序名、安装目录名都参与匹配，并且各自再生成一个剥掉 `【】` 装饰、在 `~` 处截断的版本。归一化只保留字母数字，CJK 原样保留。
- **词边界前缀**：`ARK Survival Evolved` 会生成 `ark` / `arksurvival` / `arksurvivalevolved` 三个可命中前缀（最短 4 字），于是 `ARK` 目录能命中，`arknights` 不会。
- **候选体积闸门**：超过 256 MB 或 5 000 个文件的文件夹被判定为游戏本体而不是存档，直接跳过。
- **噪声名单**：`assets` `build` `dist` `obj` `src` `venv` `__pycache__` `backup` `downloads` `screenshots` `crash` 等目录，以及任何以 `.` `_` `%` `$` 开头的目录，一律不认。
- **VirtualStore 镜像**：被 UAC 重定向过的路径会一并记录。

### 实测效果与已知局限

416 个游戏的库，全库扫描约 60 秒，**112 个游戏找到了存档位置**。

启发式必然有代价。目前已知的误判全部属于「同系列同名」这一类：`The Forest` 会命中 `forestia`、`The Censor` 命中 `TheCensorer`、`Fate Seeker` 与 `FateSeekerII` 互相命中、`Monster Train` 与 `MonsterTrain2` 互相命中、BALDRSKY 系列内部互相命中。继续收紧后缀规则就会开始漏掉真存档，所以选择保留这个代价——在「管理…」窗口里手动移除一行即可，移掉之后不会被下次扫描加回来。

---

## 备份

### 时机

- **退出游戏后**（默认开启）
- 启动游戏前（默认关闭）
- **定时**（默认开启，6 小时一次；Playnite 启动 2 分钟后开始计时，之后每 5 分钟检查一次是否到点）
- 手动：详情页面板的「备份」、游戏右键菜单「立即备份」、主菜单「备份整个游戏库」
- 还原前自动打一个撤销点

**存档没变化就不会产生新快照。** 变化判定是把目标里所有文件按 `相对路径 | 体积 | 修改时间` 排序后算 SHA1，与上次的哈希比较。

### 快照长什么样

`<备份根>\<游戏名> (<8 位哈希>)\<yyyyMMdd-HHmmss>_<触发原因>.zip`，里面是：

```
snapshot.json          # 时间、触发原因、每个目标的原始绝对路径、文件数、体积
targets/0_<目录名>/…    # 按目标编号分开存放，还原时能对应回去
registry/0.reg         # 仅在开启注册表备份时
```

索引在 `<备份根>\.savevault\index.json`，**全局只有这一个索引文件**。手动删掉某个游戏的文件夹后，下次会自动对账。

### 保留策略与 2 GB 配额

先按游戏级规则筛：最新的一份 + 所有**已固定**的 + 最近 7 天每天一份 + 最近 4 周每周一份，再削到「每游戏最多 10 份」。之后跑全局配额：默认 **2048 MB**，超了就从最旧的非固定快照开始删，**任何游戏的最后一份永远不删**。

### 还原

还原前先把当前状态自动存成一个快照，所以这一步随时可以撤销。默认会弹确认框。注册表还原前会把原键导出到 `<备份根>\.savevault\registry-undo\<时间戳>\`。

---

## 界面

**详情页面板**（挂在主题里的一个 `ContentControl`）：存档位置列表（带来源角标、路径是否存在、是否参与备份的勾选）、最近快照列表（时间、触发原因、体积，以及固定 / 还原 / 删除）、外加「备份 / 检测 / 管理…」三个按钮。没有已知位置时不会静默隐藏，而是写明「还不知道这个游戏的存档在哪」并给出三条出路。

**管理窗口**：左侧游戏列表可按名称筛选，右侧是所选游戏的完整位置与快照，底部有全局统计（游戏数 · 快照数 · 已用 / 上限）与「全部检测」「全部备份」「执行清理」。

**主菜单 → 扩展 → 存档方舟**：打开存档方舟… / 备份整个游戏库 / 为所有游戏查找存档位置 / 执行保留策略清理。

**游戏右键菜单**：立即备份 / 查找存档位置 / 添加存档位置… / 打开备份文件夹 / 在全库备份中跳过。

---

## 设置

| 分区 | 项 | 默认 |
|---|---|---|
| 备份 | 备份文件夹 | `D:\GAMES\存档备份汇总` |
| | 压缩快照 | 开 |
| | 还原前先确认 | 开 |
| | 每次备份后显示通知 | 开 |
| | 同时备份注册表项 | **关** |
| 备份时机 | 退出游戏后 / 启动游戏前 / 定时（间隔） | 开 / 关 / 开（360 分钟） |
| 保留策略 | 总体积上限 | **2048 MB** |
| | 每个游戏保留快照数 / 按天 / 按周 | 10 / 7 / 4 |
| 检测 | 游玩时学习存档位置 | **开** |
| | 搜索安装目录 / 搜索用户目录 | 开 / 开 |
| | 使用 Ludusavi 清单（可指定路径） | 开 |
| | 自动为新游戏检测 | 开 |
| | 候选最大体积 / 最大文件数 | 256 MB / 5000 |

备份根目录可以随便换，设置页里是文件夹选择器。**建议放在系统盘以外**——快照会长期累积，而且插件的临时解压目录也在备份根下（刻意不用 `%TEMP%`，免得撑爆 C 盘）。

---

## 安装

从 [Releases](https://github.com/Whereis-Alice/SaveVault/releases) 下载 `.pext` 双击安装，或者克隆本仓库后：

```powershell
./build/build.ps1 -PlayniteDir "E:\Software\Playnite"
```

产物在 `dist\`。

---

## 给主题作者

插件把自己所有可视样式暴露成 16 个资源键，主题只要定义同名 `x:Key` 就会被优先采用；没定义的由插件按主题现有键推导（`TextBrush` / `TextBrushDarker` / `GlyphBrush` / `GridItemBackgroundBrush` / `ControlCornerRadius` 等），所以不改主题也能融进配色。

**Brush**

```
SaveVaultTextBrush               SaveVaultSubTextBrush
SaveVaultAccentBrush             SaveVaultSectionBackgroundBrush
SaveVaultCardBackgroundBrush     SaveVaultCardHoverBackgroundBrush
SaveVaultCardBorderBrush         SaveVaultChipBackgroundBrush
SaveVaultProtectedBrush          SaveVaultPendingBrush
SaveVaultUnknownBrush
```

**Double**

```
SaveVaultHeaderFontSize (15)     SaveVaultTextFontSize (12)
SaveVaultSmallFontSize (11)      SaveVaultSectionSpacing (10)
```

**CornerRadius**

```
SaveVaultCornerRadius
```

挂载点（`ContentControl x:Name`）：

- `SaveVault_GameVaultControl` — 完整面板（存档位置 + 快照 + 操作按钮）

[Helium Nova](https://github.com/Whereis-Alice/Helium-Nova) 已经把它挂在详情视图与网格视图里，并在[主题工坊](https://github.com/Whereis-Alice/PlayniteThemeForge)里注册了上面这批键，可以直接在 GUI 里调。

---

## 开发

```powershell
dotnet build source/SaveVault.csproj -c Release -v m
```

需要 .NET Framework 4.6.2 开发包。`Playnite.SDK` 与 `Newtonsoft.Json` 都以 `ExcludeAssets="runtime"` 引用——Playnite 自带这两个程序集，插件不能重复分发。

图标可以用 `build/make-icon.ps1` 重新生成。

---

## 许可与致谢

存档路径线索的其中一层来自 [Ludusavi](https://github.com/mtkennerly/ludusavi) 的开放清单，感谢该项目长期维护这份数据。插件只读取清单文件，不调用其可执行文件，也不要求你安装它。
