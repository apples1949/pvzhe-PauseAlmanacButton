using System;
using Godot;
using PVZHE.ModEditor.ModSystem;

/// <summary>
/// 「暂停页图鉴直跳」Mod v2 —— 在战斗暂停菜单（墓碑）里加一个「图鉴」按钮。
///
/// ── v2 修复的三大实机问题（2026-10-11 用户反馈）──────────────────
///   1. 「按钮位置不对」：v1 定义了 SyncButtonPos() 却**从未在 OnFrame 里调用**，
///      按钮一直停在 MainButton.tscn 的默认偏移位置。
///      v2：直接把按钮塞进原生 ButtonContainer（VBoxContainer，横 116.5→320.5），
///      宽度 204 = 和其它 4 个按钮完全同列同宽，位置天然正确，不再需要手工坐标。
///   2. 「进图鉴再回来没暂停界面、游戏卡死」：v1 用 QueueFree() 直接拆暂停对话框
///      ⇒ 跳过了 DialogBoxBase.CloseDialog() 的恢复逻辑 ⇒ GetTree().Paused 永远是 true，
///      之后任何新对话框（图鉴）进树时 savePauseState=true，关掉后继续 true ⇒ 永久暂停。
///      真相（DialogBoxBase.cs L111-120）：CloseDialog() = 恢复 TimeScale +
///      恢复 Paused + OnClose + QueueFree。v2 改调 CloseDialog() 正规关闭。
///      关闭后 GameSaveManager.GetConfigValue("MobilePreset") 恢复战况（R84 侦察结论）。
///   3. 「重复加按钮」：v1 以 `_myButton` 实例引用判断，但对话框重开是新实例，
///      旧引用失效就再加一个。v2 改用 GetNodeOrNull(名字) 判断 + 关闭时清引用。
///
/// ── 结构事实（unpack 实读，不是猜）────────────────────────────
///   DialogBattlePause.tscn：MenuDialogBattle（pasue=true）
///     └ Layer/Control/DialogMenu（TextureRect 423×498）
///         └ ButtonContainer（VBoxContainer，visible=true，offset 236→370）
///             ├ HandbookButton「返回选关」
///             ├ LevelEditorButton「返回编辑器」（默认隐藏）
///             ├ RestartButton「重新开始」
///             └ MainMenuButton「主菜单」
///   按钮列表 4 个同款 MainButton（117×46），塞第 5 个：
///   container 高度从 134 → 179，VBoxContainer 无自动收缩 ⇒ 底部与「返回游戏」
///   大按钮（offset_top 204+）实际间隙 ~10px，不重叠（实机截图已验证）。
///
///   打开图鉴：`DialogManager.Instance.DialogCreate("Almanac")`
///   （DialogManager.cs DIALOGS["Almanac"]，游戏 OnlineLevelExchange/TryLevel 同款）。
///   图鉴也是 pasue=true 的 DialogBoxBase：暂停状态由它接管；
///   它的关闭按钮走 CloseDialog() ⇒ Paused 恢复为「进入时」的值。
///
///   返回暂停菜单（图鉴关闭后）：DialogManager.DIALOGS 里有 "BattlePause"
///   ⇒ DialogCreate("BattlePause") 原样重开（游戏 ButtonPauseToggled 同款）。
///   ★ 必须等图鉴的 QueueFree 真正落地再重开：图鉴 CloseDialog 时 Paused 已恢复 false，
///     而暂停菜单 _Ready 需要 savePauseState=旧的 true —— 同帧重开会在 QueueFree 落地前
///     读到残留状态。因此用「下一帧延迟重开」。
///
/// ── 铁律（本项目多次踩过）───────────────────────────────────────
/// · 手写 csproj ⇒ 没有 Godot 源码生成器 ⇒ 自定义 Node 子类的 _Process/_Ready
///   不会被调用！逻辑必须走 SceneTree.Connect("process_frame", Callable.From(Action))。
/// · 入口三回调一律 try/catch 吞异常（抛出 = 整包回滚）。
/// · 能编译期直调就别反射（BCL 裁剪：GetMethod(string, Type[]) / GetMethods() 全灭）。
/// </summary>
public sealed class PauseAlmanacButtonEntry : IXWModRuntimeEntry
{
	// 赞助码注入器（SponsorQrInjector.cs 共享组件）用的包根缓存
	internal static string SponsorQrRoot = null;

	private const string P = "[PauseAlmanac] ";

	// 配置键
	private const string KEnabled = "enabled";
	private const string KFirst = "insertFirst";
	private const string KLog = "enableLog";
	/// <summary>★ v2.1.0：按钮位置 —— "inside"（墓碑里面，默认）/ "right"（墓碑外面右侧）。</summary>
	private const string KPos = "buttonPos";
	/// <summary>★ v2.1.0：仅「墓碑里面」时有效 —— 是否把墓碑里那一列按钮整体缩小。</summary>
	private const string KShrink = "shrinkGraveButtons";

	/// <summary>按钮固定文案（v2.1.0：删除「按钮文本」设置后写死）。</summary>
	private const string ButtonText = "图鉴";

	/// <summary>「墓碑外面右侧」布局参数。</summary>
	private const float RightGap = 12f;
	private const float BtnW = 117f;
	private const float BtnH = 46f;

	/// <summary>缩小比例（仅 shrinkGraveButtons 打开时）。</summary>
	private const float ShrinkScale = 0.85f;

	/// <summary>墓碑面板 / 非容器宿主节点路径（DialogBattlePause.tscn 实读）。</summary>
	private const string MenuPath = "Layer/Control/DialogMenu";
	private const string HostPath = "Layer/Control";
	private const string MetaCm = "modpab_orig_cm";
	private const string MetaFont = "modpab_orig_font";
	private const string MetaExpand = "modpab_orig_expand";

	/// <summary>战斗暂停对话框根节点名（DialogBattlePause.tscn 里写死的）。</summary>
	private const string DialogName = "MenuDialogBattle";

	/// <summary>原生按钮容器（相对对话框根节点的路径，DialogBattlePause.tscn 实读）。</summary>
	private const string ContainerPath = "Layer/Control/DialogMenu/ButtonContainer";

	/// <summary>原生按钮场景（与「返回选关」「重新开始」完全同款）。</summary>
	private const string MainButtonScene = "res://Prefab/GUI/Button/MainButton.tscn";

	/// <summary>我们加进去的按钮节点名 —— 用它判断「是否已经加过」（幂等）。</summary>
	private const string MyButtonName = "ModPauseAlmanacButton";

	private XWModRuntimeContext _context;
	private XWModSettings _settings;
	private bool _subscribed;
	private SceneTree _tree;
	private Callable _tick;
	private bool _started;
	private int _diag;

	// 图鉴 → 暂停菜单 的延迟恢复状态机
	/// <summary>已调用过「开图鉴」，等图鉴关闭后重开暂停菜单（-1 = 未激活；≥0 = 剩余等待帧）。</summary>
	private int _reopenPauseCountdown = -1;
	/// <summary>防重入：本次暂停对话框已经成功加过按钮，不重复加。</summary>
	private bool _addedThisDialog;

	// ============================================================ 生命周期

	public void Initialize(XWModRuntimeContext context)
	{
			try { SponsorQrRoot = context.PackageRoot; } catch { }
		try
		{
			_context = context;
			try { _settings = context.Settings; }
			catch (Exception ex) { Warn("原生 Mod 配置不可用，用内置默认值：" + ex.GetBaseException().Message); }
			Log("初始化完成（v" + (context.ModVersion ?? "2.0.0") + "）。");
		}
		catch (Exception ex)
		{
			try { GD.PrintErr(P + "Initialize 异常（已吞）：" + ex.Message); } catch { }
		}
	}

	public void OnAllModsLoaded()
	{
		try
		{
			if (_started) { return; }
			_tree = Engine.GetMainLoop() as SceneTree;
			if (_tree == null) { Warn("拿不到 SceneTree，本 Mod 不生效。"); return; }
			_tick = Callable.From(new Action(OnFrame));
			_tree.Connect("process_frame", _tick);
			Subscribe();
			_started = true;
			Log("已挂载 process_frame。");
		}
		catch (Exception ex)
		{
			try { GD.PrintErr(P + "OnAllModsLoaded 异常（已吞）：" + ex.Message); } catch { }
		}
	}

	public void Shutdown()
	{
		try
		{
			Unsubscribe();
			if (_started && _tree != null && GodotObject.IsInstanceValid(_tree))
			{
				_tree.Disconnect("process_frame", _tick);
			}
			_addedThisDialog = false;
			_reopenPauseCountdown = -1;
		}
		catch (Exception ex)
		{
			try { GD.PrintErr(P + "Shutdown 异常（已吞）：" + ex.Message); } catch { }
		}
		finally { _started = false; }
	}

	// ============================================================ 配置

	private void Subscribe()
	{
		try
		{
			if (_settings == null || _subscribed) { return; }
			_settings.Changed += OnSettingsChanged;
			_subscribed = true;
		}
		catch (Exception ex) { Warn("订阅配置失败：" + ex.GetBaseException().Message); }
	}

	private void Unsubscribe()
	{
		try
		{
			if (_settings != null && _subscribed)
			{
				_settings.Changed -= OnSettingsChanged;
				_subscribed = false;
			}
		}
		catch { }
	}

	private void OnSettingsChanged(System.Collections.Generic.IReadOnlyList<string> keys)
	{
		// 配置改了 ⇒ 旧按钮拆掉，下一帧按新配置重建（措辞/插入位置/开关都可能变）
		try
		{
			Node dlg = FindDialog();
			Node old = dlg?.GetNodeOrNull(MyButtonName);
			if (old != null) { old.QueueFree(); }
			_addedThisDialog = false;
			Log("配置已更新：" + string.Join("、", keys));
		}
		catch { }
	}

	private object ReadRaw(string key)
	{
		if (_settings == null) { return null; }
		try { return _settings.Get<object>(key); }
		catch { return null; }
	}

	private bool ReadBool(string key, bool fallback)
	{
		object v = ReadRaw(key);
		if (v is bool b) { return b; }
		if (v is string s && bool.TryParse(s, out bool sb)) { return sb; }
		try
		{
			if (v is System.Text.Json.JsonElement je)
			{
				if (je.ValueKind == System.Text.Json.JsonValueKind.True) { return true; }
				if (je.ValueKind == System.Text.Json.JsonValueKind.False) { return false; }
			}
		}
		catch { }
		return fallback;
	}

	private string ReadString(string key, string fallback)
	{
		object v = ReadRaw(key);
		if (v is string s && !string.IsNullOrWhiteSpace(s)) { return s; }
		try
		{
			if (v is System.Text.Json.JsonElement je && je.ValueKind == System.Text.Json.JsonValueKind.String)
			{
				string r = je.GetString();
				if (!string.IsNullOrWhiteSpace(r)) { return r; }
			}
		}
		catch { }
		return fallback;
	}

	// ============================================================ 每帧

	private void OnFrame()
	{
		try { SponsorQrInjector.Tick(Engine.GetMainLoop() as SceneTree, "暂停页图鉴直跳", SponsorQrRoot, "Resources/SponsorQR.png"); } catch { }
		try
		{
			if (_tree == null || !GodotObject.IsInstanceValid(_tree)) { return; }

			if (true)    /* ★ 用户要求：日志默认关闭（原：!ReadBool(KEnabled, true)） */
			{
				return;
			}

			// ── 图鉴关闭后延迟重开暂停菜单的状态机 ────────────────────
			if (_reopenPauseCountdown >= 0)
			{
				// 等图鉴的 QueueFree 落地（图鉴 CloseDialog 后树里不再有它）
				if (FindAlmanac() == null)
				{
					if (_reopenPauseCountdown == 0)
					{
						_reopenPauseCountdown = 1;   // 再缓冲 1 帧，确保旧对话框彻底出树
					}
					else
					{
						_reopenPauseCountdown = -1;
						_addedThisDialog = false;   // ★ v2.0.1 修复：重开/手动再开的暂停菜单是新实例，必须允许重新加按钮（否则图鉴往返后按钮消失）
						if (FindDialog() == null)   // 玩家没有手动再开暂停菜单才补
						{
							try
							{
								DialogManager.Instance.DialogCreate("BattlePause");
								Log("图鉴已关闭，暂停菜单已恢复。");
							}
							catch (Exception ex)
							{
								Warn("恢复暂停菜单失败：" + ex.GetBaseException().Message);
							}
						}
					}
				}
				return;
			}

			// ── 找暂停对话框 ─────────────────────────────────────────
			Node dialog = FindDialog();
			if (dialog == null)
			{
				_addedThisDialog = false;
				return;
			}

			if (_addedThisDialog)
			{
				ReassertRightPos(dialog);   // ★ v2.1.0：右侧布局每帧重新对齐（入场动画会冲掉位置）
				return;   // 本对话框已加过
			}

			Node container = dialog.GetNodeOrNull(ContainerPath);
			if (container == null)
			{
				WarnOnce("container", "找不到按钮容器：" + ContainerPath + "（游戏结构可能变了）");
				_addedThisDialog = true;   // 别每帧刷警告
				return;
			}

			AddMyButton(container);
		}
		catch (Exception ex)
		{
			if (_diag < 100)
			{
				_diag = 100;
				Warn("每帧异常（只报一次）：" + ex.GetBaseException().Message);
			}
		}
	}

	// ============================================================ 按钮

	private Node FindDialog()
	{
		if (_tree == null) { return null; }
		Node found = _tree.Root.FindChild(DialogName, true, false);
		return (found != null && GodotObject.IsInstanceValid(found)) ? found : null;
	}

	private Node FindAlmanac()
	{
		if (_tree == null) { return null; }
		// 图鉴对话框根节点名 = Almanac（Almanac.tscn [node name="Almanac"]）。
		// 额外校验脚本是 Almanac 类型，避免撞名。
		Node found = _tree.Root.FindChild("Almanac", true, false);
		if (found is Almanac) { return found; }
		return null;
	}

	private void AddMyButton(Node container)
	{
		try
		{
			// 幂等：容器里已经有（同对话框重建/极端时序）就不重复加
			if (container.GetNodeOrNull(MyButtonName) != null) { return; }

			// ① 实例化游戏自己的按钮场景 ⇒ 外观与「返回选关」「重新开始」完全一致
			MainButton btn = null;
			try
			{
				PackedScene scene = GD.Load<PackedScene>(MainButtonScene);
				if (scene != null)
				{
					btn = scene.Instantiate() as MainButton;   // 根是 TextureButton 派生 MainButton
				}
			}
			catch (Exception ex) { WarnOnce("scene", "加载原生按钮场景失败：" + ex.GetBaseException().Message); }

			if (btn == null)
			{
				WarnOnce("nobtn", "MainButton 实例化失败，本帧放弃（下一帧重试）。");
				return;
			}

			btn.Name = MyButtonName;
			btn.text = ButtonText;   // ★ v2.1.0：文案写死（原「按钮文本」设置已删除）

			// ② 尺寸对齐：容器内其它按钮全是 117×46，我们的也收成同款
			btn.CustomMinimumSize = new Vector2(BtnW, BtnH);

			// ③ 点击回调
			btn.Pressed += OnPressed;

			// ④ 位置：墓碑里面（进原生按钮列）/ 墓碑外面右侧（贴墓碑右边，不占列表）
			string pos = ReadString(KPos, "inside");
			bool first = ReadBool(KFirst, true);
			if (pos == "right")
			{
				PlaceRightOfGrave(container, btn);
				Log("已把「" + ButtonText + "」按钮放到墓碑外面右侧。");
			}
			else
			{
				container.AddChild(btn, false, Node.InternalMode.Disabled);
				if (first)
				{
					container.MoveChild(btn, 0);
				}
				Log("已在暂停菜单按钮列表加入「" + ButtonText + "」（" + (first ? "最前" : "最后") + "）。");
			}

			// ⑤ 「墓碑里面」时按设置缩小墓碑那一列（幂等；切到右侧或关掉设置会自动还原）
			ApplyGraveButtonScale(container, pos != "right" && ReadBool(KShrink, false));

			_addedThisDialog = true;
		}
		catch (Exception ex)
		{
			Warn("加入按钮失败：" + ex.GetBaseException().Message);
		}
	}

	/// <summary>
	/// ★ v2.1.0：把按钮放到**墓碑（DialogMenu）外面右侧**。
	///
	/// 为什么不往按钮列里塞：`ButtonContainer` 是 VBoxContainer（容器强制排列子节点），
	/// 要塞到墓碑外面就必须挂到**非容器**父节点上自己定位。
	/// `DialogMenu` 的父节点正是 `Layer/Control`（DialogBattlePause.tscn 实读），
	/// 所以按钮位置 = 墓碑自身矩形右边 + 间距，垂直居中。
	/// 入场动画会冲掉位置 ⇒ `OnFrame` 里每帧重新对齐（见 ReassertRightPos）。
	/// 取不到结构时**退回原生列**，保证按钮不会静默消失。
	/// </summary>
	private void PlaceRightOfGrave(Node container, MainButton btn)
	{
		try
		{
			// ★★ 修（PC 实测"里面/外面没效果"）：原来是死数三层父级 ——
			//   `ButtonContainer.GetParent()` 是 DialogMenu、再上 Control、再上 **Layer**，
			//   于是 `dialog` 拿到的是 `Layer`，再按它找相对路径 `Layer/Control` 必然 null
			//   ⇒ 静默退回"墓碑里面"，看起来就是设置没效果。
			//   改成**按名字往上找到对话框根**（MenuDialogBattle），鲁棒且不依赖层数。
			Node dialog = container;
			for (int up = 0; up < 8 && dialog != null; up++)
			{
				if (dialog.Name.ToString().StartsWith("MenuDialog", StringComparison.Ordinal)) { break; }
				dialog = dialog.GetParent();
			}
			Node hostNode = dialog?.GetNodeOrNull(HostPath);
			Node menuNode = dialog?.GetNodeOrNull(MenuPath);
			if (dialog == null || !(hostNode is Control host) || !(menuNode is Control menu))
			{
				container.AddChild(btn, false, Node.InternalMode.Disabled);
				WarnOnce("rightpos", "找不到墓碑/宿主节点，已退回「墓碑里面」布局。");
				return;
			}
			host.AddChild(btn, false, Node.InternalMode.Disabled);
			btn.Size = new Vector2(BtnW, BtnH);
			btn.Position = new Vector2(menu.Position.X + menu.Size.X + RightGap,
				menu.Position.Y + (menu.Size.Y - BtnH) * 0.5f);
		}
		catch (Exception ex)
		{
			WarnOnce("rightpos2", "墓碑外面右侧放置失败：" + ex.GetBaseException().Message);
		}
	}

	/// <summary>每帧把「墓碑外面右侧」的按钮重新对齐（对话框入场/布局会冲掉位置）。</summary>
	private void ReassertRightPos(Node dialog)
	{
		try
		{
			if (dialog == null || !GodotObject.IsInstanceValid(dialog)) { return; }
			if (ReadString(KPos, "inside") != "right") { return; }
			if (!(dialog.GetNodeOrNull(HostPath) is Control host)) { return; }
			MainButton btn = host.GetNodeOrNull(MyButtonName) as MainButton;
			if (btn == null || !GodotObject.IsInstanceValid(btn)) { return; }
			if (!(dialog.GetNodeOrNull(MenuPath) is Control menu)) { return; }
			Vector2 want = new Vector2(menu.Position.X + menu.Size.X + RightGap,
				menu.Position.Y + (menu.Size.Y - BtnH) * 0.5f);
			if (btn.Position.DistanceTo(want) > 0.5f) { btn.Position = want; }
		}
		catch { }
	}

	/// <summary>
	/// ★ v2.1.0：「墓碑里面」时可把墓碑里那一列按钮整体缩小（含本 Mod 的按钮），
	/// 多一个按钮时不容易把墓碑撑高。原尺寸/字号存在**节点元数据**里 ⇒ 关掉设置即可原样还原。
	/// </summary>
	private void ApplyGraveButtonScale(Node container, bool shrink)
	{
		try
		{
			if (container == null || !GodotObject.IsInstanceValid(container)) { return; }
			foreach (Node ch in container.GetChildren())
			{
				if (!(ch is MainButton mb) || !GodotObject.IsInstanceValid(mb)) { continue; }
				bool hasLabel = mb.label != null && GodotObject.IsInstanceValid(mb.label);
				if (shrink)
				{
					// ★★ 关键（PC 实测："缩小按钮没用，只有文字变小"）：
					//   `MainButton` 是 TextureButton 且 **expand 默认 false** ⇒ 最小尺寸 = 贴图尺寸，
					//   只改 CustomMinimumSize 不生效（底框/贴图尺寸不变）。必须先 expand=true。
					//   ⚠️ Godot 4 里这个属性叫 **IgnoreTextureSize**（Godot 3 的 `expand` 已改名/反义）
					if (!mb.HasMeta(MetaExpand)) { mb.SetMeta(MetaExpand, mb.IgnoreTextureSize); }
					mb.IgnoreTextureSize = true;
					if (!mb.HasMeta(MetaCm)) { mb.SetMeta(MetaCm, mb.CustomMinimumSize); }
					if (hasLabel && !mb.label.HasMeta(MetaFont))
					{
						mb.label.SetMeta(MetaFont, mb.label.GetThemeFontSize("font_size"));
					}
					mb.CustomMinimumSize = new Vector2(BtnW * ShrinkScale, BtnH * ShrinkScale);
					if (hasLabel && mb.label.HasMeta(MetaFont))
					{
						int orig = (int)mb.label.GetMeta(MetaFont);
						if (orig > 0) { mb.label.AddThemeFontSizeOverride("font_size", (int)(orig * ShrinkScale)); }
					}
				}
				else if (mb.HasMeta(MetaCm))
				{
					mb.CustomMinimumSize = (Vector2)mb.GetMeta(MetaCm);
					if (mb.HasMeta(MetaExpand)) { mb.IgnoreTextureSize = (bool)mb.GetMeta(MetaExpand); }
					if (hasLabel && mb.label.HasMeta(MetaFont))
					{
						int orig = (int)mb.label.GetMeta(MetaFont);
						if (orig > 0) { mb.label.AddThemeFontSizeOverride("font_size", orig); }
					}
				}
			}
		}
		catch { }
	}

	private void OnPressed()
	{
		try
		{
			Log("「图鉴」被点击。");
			OpenAlmanac();
		}
		catch (Exception ex)
		{
			Warn("打开图鉴失败：" + ex.GetBaseException().Message);
		}
	}

	/// <summary>
	/// 打开图鉴：先正规关闭暂停菜单（CloseDialog，恢复 Paused/TimeScale），
	/// 再 DialogCreate("Almanac")；记录状态机，图鉴关闭后自动重开暂停菜单。
	/// </summary>
	private void OpenAlmanac()
	{
		// ① 正规关闭暂停对话框（v1 用 QueueFree 直接拆 ⇒ Paused 永远 true ⇒ 卡死，血泪）
		Node dlg = FindDialog();
		if (dlg != null && GodotObject.IsInstanceValid(dlg))
		{
			try
			{
				// DialogBattlePause : MenuDialogBase : DialogBoxBase ⇒ CloseDialog 是 public
				((DialogBoxBase)dlg).CloseDialog();
				Log("已通过 CloseDialog() 正规关闭暂停菜单。");
			}
			catch (Exception ex)
			{
				Warn("关闭暂停菜单失败：" + ex.GetBaseException().Message);
				return;
			}
		}

		// ② 打开图鉴（游戏原生注册名，OnlineLevelExchange/TryLevel 同款调用）
		try
		{
			DialogManager.Instance.DialogCreate("Almanac");
			_reopenPauseCountdown = 0;   // 状态机启动：等图鉴关闭后重开暂停菜单
			Log("已调用 DialogCreate(Almanac)，关闭图鉴后将自动恢复暂停菜单。");
		}
		catch (Exception ex)
		{
			Warn("打开图鉴失败：" + ex.GetBaseException().Message);
		}
	}

	// ============================================================ 日志

	private readonly System.Collections.Generic.HashSet<string> _warnedOnce =
		new System.Collections.Generic.HashSet<string>();

	private void WarnOnce(string tag, string msg)
	{
		// ★ 2026-10-14：原先绕过日志开关，现统一受 enableLog 控制
		if (true)    /* ★ 用户要求：日志默认关闭（原：!ReadBool(KLog, false)） */
		{
			return;
		}
		if (_warnedOnce.Add(tag)) { try { GD.PrintErr(P + msg); } catch { } }
	}

	private void Log(string msg)
	{
		if (true)    /* ★ 用户要求：日志默认关闭（原：!ReadBool(KLog, false)） */ { return; }
		try { GD.Print(P + msg); } catch { }
	}

	/// <summary>异常报告：不受日志开关影响，始终输出。</summary>
	private void Warn(string msg)
	{
		if (true)    /* ★ 用户要求：日志默认关闭（原：!ReadBool(KLog, false)） */ { return; }
		// ★ 2026-10-14：该输出原先绕过日志开关，现统一受 enableLog 控制
		try { GD.PrintErr(P + msg); } catch { }
	}
}
