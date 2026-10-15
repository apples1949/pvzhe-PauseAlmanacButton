# 暂停页图鉴直跳（PauseAlmanacButton）

> 在战斗暂停菜单（墓碑）里加一个「图鉴」按钮，点一下直接打开图鉴。

| 项目 | 内容 |
| --- | --- |
| Mod ID | `pausealmanacbutton` |
| 程序集 | `JTYPauseAlmanacButton` → 包内 `Runtime/ModAssembly.dll` |
| 入口类 | `PauseAlmanacButtonEntry` |
| 当前版本 | 1.0.0 |
| 分类 | 工具与前置 |

## 版本历史


## 这个模组能做什么

战斗中按右上角「菜单」暂停 → 墓碑的按钮列表里会多出一个「图鉴」→ 点它**直接打开图鉴**，
不用先退回主菜单再进。

按钮**复用游戏自带的 `MainButton`**（`res://Prefab/GUI/Button/MainButton.tscn`，117x46）——
与「菜单」「Mod 管理」是同一个场景，外观天然一致。

## 内部开关（也可在「Mod 配置」面板里改）

| 配置项 | 类型 | 默认 | 说明 |
| --- | --- | --- | --- |
| `enabled` | bool | 开 | 总开关 |
| `buttonLabel` | string | `图鉴` | 按钮文字 |
| `insertFirst` | bool | 开 | 开=插在「返回选关」之前；关=追加到最后 |
| `enableLog` | bool | 关 | 输出诊断日志 |

**所有改动立即生效，不用重启游戏。**

## 实现要点（全部来自 0.30.0 游戏文件实地读取）

暂停菜单 = `Prefab/GUI/DialogBox/MenuDialog/DialogBattlePause/DialogBattlePause.tscn`
（基类 `MenuDialogBase.tscn`），结构：

```
MenuDialogBase(Control)
└── Layer(CanvasLayer layer=100)/Control
    └── DialogMenu   ← 墓碑本体：TextureRect 423x498（贴图 DialogMenu.png）
          ├── 各 CheckBox / Slider
          ├── BackButton「返回游戏」
          └── ButtonContainer (VBoxContainer)  ← 原来的 4 个按钮
                HandbookButton「返回选关」/ LevelEditorButton「返回编辑器」(默认隐藏)
                / RestartButton「重新开始」/ MainMenuButton「主菜单」
```

**不需要拉长墓碑素材**：墓碑高 498、按钮列表只到本地 y=370，**底部还有 128px 余量**，
加第 5 个按钮只要 +44px。

打开图鉴用游戏自己的公开入口：`DialogManager.Instance.DialogCreate("Almanac")`
（游戏自身在 `OnlineLevelExchange.cs:185`、`TryLevel.cs:248`、`TowerDefenseControlOld.cs:122` 就是这么调的；
注册表在 `DialogManager.cs:30` → `{ "Almanac", "uid://54lyi8ygt8gb" }`）。

⚠️ 本 Mod **只走反射**调 `DialogManager`，不做编译期引用 —— 它是**游戏自己的类**，
命名空间不确定，直接引用有编不过的风险。

## 铁律（本项目多次踩过）

1. **手写 csproj ⇒ 没有 Godot 源码生成器** ⇒ 自定义 Node 子类的 `_Process`/`_Ready`
   **不会被调用**。逻辑全部走 `SceneTree.Connect("process_frame", Callable.From(Action))`。
2. `Callable` **不能**隐式转 `Action`（只有反向可以）。
3. 入口三回调一律 try/catch 吞异常 —— 抛出会导致**整包回滚**。
4. **不要**用 `PropertyInfo.SetValue(object, object)` —— 该两参重载被游戏导出时裁剪，
   调用会抛 `MissingMethodException`（`LevelQuickJump` 踩过，每帧抛）。
   用 `MethodInfo.Invoke` / `PropertyInfo.GetValue`。

## 硬护栏（违反会整包被拒）

1. `mod.json` 必须在**根**且**唯一**
2. `Runtime/` 下**只允许** `ModAssembly.dll`
3. 包内**绝不允许**出现 `.cs`（`ModLoader.IsExecutablePackageFile` 白名单拒收）

## 构建

```powershell
python mods\PauseAlmanacButton\build_and_install.py             # 编译 + 打包
python mods\PauseAlmanacButton\build_and_install.py --install   # 继续装机
```

构建后**必须**跑验证（只看构建输出不算验证）：

```powershell
python pvz-hybrid-mod-skills\tools\settings-validate\...   # 见该工具 README
python pvz-hybrid-mod-skills\tools\verify_build.py PauseAlmanacButton
```

## 未实测说明

按项目约定，开发期不启动游戏 ⇒「按钮是否真的出现在暂停菜单、点了是否真的打开图鉴」
**属待实测**。已装机并加入 `enabled_mods.json`。

## 更新记录

| 版本 | 变更 |
| --- | --- |
| **2.1.0** | ① 删除「按钮文本」设置 —— 文本固定为「图鉴」。；② 新增「按钮位置」：墓碑里面（与原生按钮同列，原做法）/ 墓碑外面右侧（贴墓碑右侧外面，不占按钮列）。；③ 选「墓碑里面」时新增「缩小墓碑按钮」开关：把墓碑里那一列按钮整体缩小约 15%。 |
