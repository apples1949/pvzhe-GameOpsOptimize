using System;
using System.Collections.Generic;
using System.Reflection;
using Godot;
using PVZHE.ModEditor.ModSystem;

/// <summary>
/// 「手机操作优化」Mod 的托管运行时入口。
///
/// ★ 本包**只保留两个功能**：「③关卡/章节按压反馈」与「④图鉴自动拾取开关」
///   已于 2026-09-30 **拆到独立新包**「点击反馈与自动拾取」（`MobileTapFeedback`）
///   —— 用户要求把这两项分离开，本包不再包含它们。
///
/// ── 两个功能（对应手机版实测问题，2026-09-29）────────────────────
///
/// ① 选卡界面"想上下滑动却选中了植物"
///    根因：卡池卡的 `Pressed()` 在**松手时**触发（Godot `BaseButton` 默认
///    `ACTION_MODE_BUTTON_RELEASE`），滚动与点击同时发生 ⇒ 滑一下就误选一张。
///    方案：记录按下位置，松手时位移 ≥ 阈值 ⇒ 调 `PacketChoose(card)` 把刚误选的撤掉
///    （与玩家手动点两下取消走完全相同的原生路径）。
///    ①b 自制关卡的"编辑卡牌库"是**另一套卡池**（`LevelEditorPacketBank`），
///    同一问题也在 ⇒ 那里撤销 = `PacketPickRelease()` + `card.Reset()`。
///
/// ② 战斗中误触植物卡后无法取消
///    根因：`PacketPickControl.PickPacket()` 只有 `if (_packet.select)` 一个分支，**没有 else**
///    ⇒ 再点一次已拿起的卡等于什么都没发生。
///    方案：记录"点击前" `packetPick` 是哪张卡；再点同一张 ⇒ 调 `PacketPickRelease()`
///    （游戏自己"放弃选择"的路径；不用 `Release()`，那会连带收掉铲子/手套工具）。
///
/// ── 铁律（前几个 Mod 已定案）────────────────────────────────────
/// · 手写 csproj 没有 Godot 源码生成器 ⇒ 自定义 Node 子类的引擎回调不会被调用，
///   全部逻辑走 `SceneTree.Connect("process_frame", Callable.From(Action))` 信号通道。
/// · Initialize / OnAllModsLoaded / Shutdown 一律 try/catch 吞异常（抛出 = 整包回滚）。
/// · `TowerDefenseInGamePacketShow` 是被到处复用的卡类（卡池/卡槽/图鉴/商店），
///   对卡做任何操作前必须先用祖先链确认它在哪个界面。
/// · 卡走对象池，`ResetForPool()` 会 `ClearEventHandlers()` ⇒ 事件钩子必须按
///   "在不在目标容器里"逐帧重挂，不能只挂一次（更稳的做法见 ①：干脆不用事件钩子，
///   改为逐帧读状态边沿）。
/// </summary>
/// <summary>★ 2026-10-14 由原 Mod「MobileUXFixesEntry」整体并入本 Mod（逻辑未改动，只改类名与可见性）。</summary>
internal static class MobileUxFixesPart
{
	// 赞助码注入器（SponsorQrInjector.cs 共享组件）用的包根缓存
	internal static string SponsorQrRoot = null;

	private const string P = "[MobileUXFixes] ";

	// ================================================================ 内部开关（默认全开）

	/// <summary>① 选卡界面滑动误选撤销（含 ①b 自制关卡编辑卡牌库）。</summary>

	/// <summary>② 战斗中再点同一张卡 = 取消选择。</summary>
	internal static bool FixPickCancel = true;   // 由「Mod 配置」覆盖（默认开）

	/// <summary>
	/// 诊断日志（★ v1.2.0 真机复验通过后关闭）：①② 已确认为"抬起判定 + 手势簿记"，
	/// 不再输出正常运行日志。需要排查时临时置 true。
	/// </summary>
	private static bool EnableLog;   // 由 Initialize 从「Mod 配置」读取（原为编译期常量）

	/// <summary>逐事件日志（窗口输入逐条打点；噪音很大，排查具体时序时才置 true）。</summary>
	private static readonly bool DiagVerbose = false;

	/// <summary>① 判定"是滑动不是点击"的位移阈值（视口逻辑像素）。</summary>
	private const float DragThresholdPx = 24f;

	private static SceneTree _tree;
		// ── 滚动条优化（2026-10-13 自独立 Mod「滚动条优化 ScrollbarTune」并入）──
		private const string KScrollOn = "scrollTune";
		private const string KScrollW = "scrollWidth";
		private const string KScrollColor = "scrollColor";
		private const string KScrollAlpha = "scrollAlpha";
		private const string KGrabColor = "grabberColor";
		private const string KGrabW = "grabberWidth";

		/// <summary>滚动条总开关。</summary>
		private static bool _scrollOn = true;
		/// <summary>滚动条粗细（像素）。</summary>
		private static int _scrollW = 24;
		/// <summary>轨道（底槽）颜色（含 scrollAlpha 的不透明度）。</summary>
		private static Color _trackColor = new Color(0f, 0f, 0f, 0.08f);
		/// <summary>滑块颜色。</summary>
		private static Color _grabColor = new Color(0.16f, 0.42f, 0.26f, 0.55f);
		/// <summary>滑块粗细；0 = 跟随滚动条宽度。**总会钳到 ≤ 滚动条宽度**。</summary>
		private static int _grabW;

		/// <summary>已处理过的滚动条 instanceId（避免重复克隆样式盒）。</summary>
		private static readonly System.Collections.Generic.HashSet<ulong> _scrollDone =
			new System.Collections.Generic.HashSet<ulong>();
		private static bool _scrollHooked;
		private static bool _scrollWalked;

		/// <summary>
		/// 读颜色配置。★ 宿主 `type=color` 推过来的是 **Godot Color**（不是字符串）；
		/// 兼容字符串（#RRGGBB / #RRGGBBAA）与 JsonElement，便于手改 mod.json 调试。
		/// </summary>
		private static Color ReadColorSetting(XWModRuntimeContext context, string key, Color fallback)
		{
			try
			{
				object v = (context == null) ? null : context.Settings.Get<object>(key);
				if (v == null) { return fallback; }
				if (v is Color c) { return c; }
				if (v is string str) { return Color.FromHtml(str.Trim()); }
				if (v is System.Text.Json.JsonElement je
					&& je.ValueKind == System.Text.Json.JsonValueKind.String)
				{
					return Color.FromHtml(je.GetString().Trim());
				}
			}
			catch { }
			return fallback;
		}

		/// <summary>#RRGGBB / #RRGGBBAA → Color；alphaOverride ≥ 0 时用它覆盖透明度。</summary>
		private static Color ParseHex(string hex, Color fallback, float alphaOverride)
		{
			try
			{
				if (string.IsNullOrWhiteSpace(hex)) { return fallback; }
				Color c = Color.FromHtml(hex.Trim());
				if (alphaOverride >= 0f) { c.A = alphaOverride; }
				return c;
			}
			catch { return fallback; }
		}

		/// <summary>接管滚动条：启动扫一次 + 只接 NodeAdded / NodeRemoved（零轮询）。</summary>
		private static void HookScrollTune()
		{
			if (_scrollHooked) { return; }
			_scrollHooked = true;
			try
			{
				_tree.NodeAdded += OnAnyNodeAdded;
				_tree.NodeRemoved += OnAnyNodeRemoved;
			}
			catch { }
		}

		private static void OnAnyNodeAdded(Node node)
		{
			try
			{
				if (!_scrollOn || node == null) { return; }
				if (node is ScrollBar bar) { ApplyScrollBar(bar); }
			}
			catch { }
		}

		private static void OnAnyNodeRemoved(Node node)
		{
			try
			{
				if (node is ScrollBar bar) { _scrollDone.Remove(bar.GetInstanceId()); }
			}
			catch { }
		}

		/// <summary>启动时全树扫一次（之后新节点由信号接管）。</summary>
		private static void WalkScrollOnce(Node node, int depth)
		{
			try
			{
				if (node == null || depth > 24) { return; }
				if (node is ScrollBar bar) { ApplyScrollBar(bar); }
				int n = node.GetChildCount();
				for (int i = 0; i < n; i++) { WalkScrollOnce(node.GetChild(i), depth + 1); }
			}
			catch { }
		}

		/// <summary>
		/// 套用到一个滚动条：
		///  · 粗细 = `custom_minimum_size`（★ Godot 4 已移除 Godot 3 的 `scroll_width` 常量，覆写无效）
		///  · 颜色 = **克隆**样式盒后改 `BgColor`（不污染主题里的共享实例）
		///  · 滑块更窄 = 给滑块样式盒加内容内缩（居中收窄）
		/// </summary>
		private static void ApplyScrollBar(ScrollBar bar)
		{
			try
			{
				if (bar == null || !GodotObject.IsInstanceValid(bar)) { return; }
				ulong id = bar.GetInstanceId();
				if (!_scrollDone.Add(id)) { return; }
				bool vertical = bar is VScrollBar;

				Vector2 cm = bar.CustomMinimumSize;
				if (vertical) { cm.X = _scrollW; } else { cm.Y = _scrollW; }
				bar.CustomMinimumSize = cm;
				try { bar.AddThemeConstantOverride("scroll_width", _scrollW); } catch { }   // 无害兜底

				Style(bar, "grabber", _grabColor, vertical, true);
				Style(bar, "grabber_highlight", _grabColor, vertical, true);
				Style(bar, "grabber_pressed", _grabColor, vertical, true);
				Style(bar, "scroll", _trackColor, vertical, false);
			}
			catch { }
		}

		/// <summary>克隆指定样式盒：改底色 + 滑块圆角 + （滑块更窄时）内容内缩居中。</summary>
		private static void Style(ScrollBar bar, string name, Color color, bool vertical, bool isGrabber)
		{
			try
			{
				StyleBox sb = bar.GetThemeStylebox(name);
				if (sb == null) { return; }
				StyleBox copy = (StyleBox)sb.Duplicate();
				if (copy is StyleBoxFlat flat)
				{
					flat.BgColor = color;
					if (isGrabber)
					{
						int r = Math.Max(2, _scrollW / 2);
						flat.CornerRadiusTopLeft = r; flat.CornerRadiusTopRight = r;
						flat.CornerRadiusBottomLeft = r; flat.CornerRadiusBottomRight = r;
						if (_grabW > 0 && _grabW < _scrollW)
						{
							float inset = (_scrollW - _grabW) * 0.5f;
							if (vertical)
							{
								flat.ContentMarginLeft = inset; flat.ContentMarginRight = inset;
							}
							else
							{
								flat.ContentMarginTop = inset; flat.ContentMarginBottom = inset;
							}
						}
					}
				}
				else if (copy is StyleBoxTexture tex)
				{
					tex.ModulateColor = color;   // 贴图型样式盒（⚠ 基类没有 ModulateColor）
				}
				else
				{
					return;   // StyleBoxEmpty 等：无底色可改
				}
				bar.AddThemeStyleboxOverride(name, copy);
			}
			catch { }
		}

		private const string KFixModListDrag = "fixModListDrag";
		private static bool _fixModListDrag = true;
		private static Tree _modTree;
		private static ScrollContainer _modScroll;
		private static bool _treeDrag;
		private static Vector2 _treePress;
		private static TreeItem _treeSelBefore;
		private static bool _modTreeHooked;
	private static Callable _tick;
	private static bool _started;
	private static int _diag;

	/// <summary>① 按下时的位置（视口坐标；由窗口输入事件驱动，见 OnWindowInputRelay）。</summary>
	private static Vector2 _downPos;
	private static bool _downValid;
	/// <summary>① 卡池卡上一帧的 `select` 状态（检测"刚被选上"的边沿；按实例 ID 索引）。</summary>
	private static readonly Dictionary<ulong, bool> _prevBankSel = new Dictionary<ulong, bool>();

	/// <summary>① 当前指针位置（视口坐标；抬起/拖动/移动都会刷新）。</summary>
	private static Vector2 _lastPos;
	private static bool _lastValid;

	/// <summary>指针此刻是否按着（按下/抬起各刷一次）。</summary>
	private static bool _pointerDown;

	/// <summary>② 上一帧"当前拿起的卡"（用于识别"再点同一张"）。</summary>
	private static ulong _prevPickId;

	// ── ② 手势簿记（★ v1.2.0）──────────────────────────────────────
	//
	// ★★ 真机日志实证（Android，2026-09-30）：**卡片是在"按下"那一刻就被选中的**，
	//    不是松手时。日志：
	//        指针按下 vp=493,398
	//        ①卡池卡被按下：... 起点=493,398 终点=493,398      ← 按下瞬间就判完了，位移必然 0
	//        指针抬起 vp=583,152                                 ← 其实滑了 200+px
	//    ⇒ 原先"按下→当前"的位移判定**永远算成 0**，① 从不撤销。
	//    同一个事实也把 ② 弄坏了：按下捡起卡 → 同一次点击的**抬起**被我们当成"再点一次"
	//    ⇒ 刚拿起的卡立刻被取消 ⇒ 表现为"单击拿不起卡，只能拖走"（拖走时抬起点不在卡上，才躲过）。
	//
	//    修法：
	//      · ① 选上时只**记下待判定**（起点 + 卡），到**抬起**时才算 起点→抬起点 的真实位移；
	//      · ② 只在"**本手势按下之前**手上就已经拿着这张卡"时才取消 ⇒ 用 `_pressStartPickId`。

	/// <summary>② 本手势"按下那一刻"手上已拿着的卡（0 = 没拿）。只有它才允许被"再点一次"取消。</summary>
	private static ulong _pressStartPickId;
	private static bool _pressStartValid;
	/// <summary>本手势是否已开始（压掉"触摸→鼠标"重复派发造成的重复按下）。</summary>
	private static bool _gestureOpen;
	private static ulong _lastPressMs;

	/// <summary>① 刚被选上、等抬起判定位移的卡池卡。</summary>

	/// <summary>把"重复按下"和"真正的第二次点击"分开的时间窗（毫秒）。</summary>
	private const ulong GestureGapMs = 250;

	/// <summary>① 待判定超时兜底（毫秒）：万一没收到抬起事件，到点用当前位置结案。</summary>
	private const ulong PendingDragTimeoutMs = 2000;

	// ================================================================ 生命周期

	
	// ============================================================ 配置面板接线
	//
	// mod.json 里声明了 enableLog（bool，默认 false）。
	// 这里在 Initialize 读一次 —— 老宿主没有 context.Settings 时吞异常、按 false 处理。
	// 用 Get<object> 而不是 Get<bool>：宿主把值存成 Dictionary<string, object>，
	// 直接 Get<bool> 可能抛 InvalidCastException。

	private static bool ReadLogSetting(XWModRuntimeContext context)
	{
		try
		{
			if (context == null)
			{
				return false;
			}
			object v = context.Settings.Get<object>("enableLog");
			if (v is bool b)
			{
				return b;
			}
			if (v is string s && bool.TryParse(s, out bool sb))
			{
				return sb;
			}
			if (v is System.Text.Json.JsonElement je)
			{
				if (je.ValueKind == System.Text.Json.JsonValueKind.True) { return true; }
				if (je.ValueKind == System.Text.Json.JsonValueKind.False) { return false; }
			}
			return false;
		}
		catch
		{
			return false;
		}
	}


	// ============================================================ 配置面板接线（功能开关）
	//
	// mod.json 声明了 fixPickCancel（点空处取消拿卡）+ 6 项滚动条配置。
	// ★ 2026-10-13：原 fixScrollMisPick（滑动误选撤销）与 fixModListDrag 已按用户需求删除。
	// 值可能是 bool / string / JsonElement（宿主存的是 Dictionary<string, object>），
	// 统一用 Get<object> 再稳健转换；老宿主没有 context.Settings 时吞异常、回退默认值。
	/// <summary>读整数配置（兼容 long / double / JsonElement / string）。</summary>
	private static int ReadIntSetting(XWModRuntimeContext context, string key, int fallback)
	{
		try
		{
			object v = (context == null) ? null : context.Settings.Get<object>(key);
			if (v == null) { return fallback; }
			if (v is System.Text.Json.JsonElement je)
			{
				if (je.ValueKind == System.Text.Json.JsonValueKind.Number && je.TryGetInt32(out int n)) { return n; }
				if (je.ValueKind == System.Text.Json.JsonValueKind.String && int.TryParse(je.GetString(), out int ns)) { return ns; }
				return fallback;
			}
			if (v is int i) { return i; }
			if (v is long l) { return (int)l; }
			if (v is double d) { return (int)d; }
			if (v is string str && int.TryParse(str, out int ps)) { return ps; }
		}
		catch { }
		return fallback;
	}

	/// <summary>读字符串配置（兼容 JsonElement 字符串）。</summary>
	private static string ReadStrSetting(XWModRuntimeContext context, string key, string fallback)
	{
		try
		{
			object v = (context == null) ? null : context.Settings.Get<object>(key);
			if (v == null) { return fallback; }
			if (v is string s2) { return s2; }
			if (v is System.Text.Json.JsonElement je && je.ValueKind == System.Text.Json.JsonValueKind.String)
			{
				return je.GetString();
			}
		}
		catch { }
		return fallback;
	}

	private static bool ReadBoolSetting(XWModRuntimeContext context, string key, bool fallback)
	{
		try
		{
			if (context == null)
			{
				return fallback;
			}
			object v = context.Settings.Get<object>(key);
			if (v is bool b)
			{
				return b;
			}
			if (v is string s && bool.TryParse(s, out bool sb))
			{
				return sb;
			}
			if (v is System.Text.Json.JsonElement je)
			{
				if (je.ValueKind == System.Text.Json.JsonValueKind.True) { return true; }
				if (je.ValueKind == System.Text.Json.JsonValueKind.False) { return false; }
			}
			return fallback;
		}
		catch
		{
			return fallback;
		}
	}

	/// <summary>按「Mod 配置」刷新两个功能开关（Initialize 与 Changed 都调）。</summary>
	// ============================================================ Mod 管理列表：拖动滚动
	//
	// 需求（用户）：「允许点击 mod 词条的同时下拉滑动」。
	//
	// 实况（读 XWModToolsPanel.tscn 得到）：Mod 管理的词条列表是一棵 **Tree**
	//   Root/ToolTabs/Mod 管理/ModManagerContent/ModTree
	//   （外层是 ScrollContainer「Mod 管理」）
	// ⚠️ Godot 的 Tree **原生不支持鼠标拖动滚动**（只支持滚轮），
	//    拖拽时它会去**选中行**、把事件吃掉 ⇒ 表现就是「想滑动却在选词条」。
	//
	// 做法：给 Tree 挂 gui_input，
	//   · 位移 < DragThresholdPx  → 当作正常点击（选中照旧）
	//   · 位移 ≥ 阈值            → 进入拖动：把位移换算成外层 ScrollContainer 的滚动，
	//                              并在松手时把"拖拽过程中被误选的行"回滚回去

	/// <summary>★ 性能：Mod 管理面板的整树搜索节流（见 TryHookModTree）。</summary>
	private static ulong _modTreeScanFrame;

	private static void TryHookModTree()
	{
		try
		{
			if (_modTreeHooked && _modTree != null && GodotObject.IsInstanceValid(_modTree))
			{
				return;
			}
			// ★ 性能（2026-10-13 真机探针：本 Mod 回调 2.00ms/帧）：战斗/主菜单里根本没有
			//   `PlayerModManager` 面板，于是这个 `FindChild` 每帧都白搜一遍整棵树。
			//   玩家打开「Mod 管理」界面本身就要几百毫秒，30 帧（0.5s）的发现延迟无感。
			ulong _mtF = Engine.GetProcessFrames();
			if (_modTreeScanFrame != 0UL && _mtF - _modTreeScanFrame < 30UL)
			{
				return;
			}
			_modTreeScanFrame = _mtF;
			Window root = (_tree != null && GodotObject.IsInstanceValid(_tree)) ? _tree.Root : null;
			if (root == null) { return; }
			Node panel = root.FindChild("PlayerModManager", true, false);
			if (panel == null || !GodotObject.IsInstanceValid(panel)) { return; }

			// 只在挂钩前读一次设置（改设置后重开 Mod 管理界面即生效）
			try
			{
				object v = (_ctxRef == null) ? null : _ctxRef.Settings.Get<object>(KFixModListDrag);
				if (v is bool b) { _fixModListDrag = b; }
				else if (v is string s && bool.TryParse(s, out bool sb)) { _fixModListDrag = sb; }
			}
			catch { }
			if (!_fixModListDrag)
			{
				// 设置关掉 ⇒ 若之前挂过就断开
				if (_modTree != null && GodotObject.IsInstanceValid(_modTree) && _modTreeHooked)
				{
					try { _modTree.Disconnect("gui_input", Callable.From<InputEvent>(OnModTreeInput)); } catch { }
					_modTreeHooked = false;
				}
				return;
			}

			Node nt = panel.GetNodeOrNull("Root/ToolTabs/Mod 管理/ModManagerContent/ModTree");
			if (!(nt is Tree tree) || !GodotObject.IsInstanceValid(tree)) { return; }
			_modTree = tree;
			_modScroll = panel.GetNodeOrNull("Root/ToolTabs/Mod 管理") as ScrollContainer;
			tree.Connect("gui_input", Callable.From<InputEvent>(OnModTreeInput));
			_modTreeHooked = true;
			Log("已给 Mod 管理列表挂上拖动滚动。");
		}
		catch { }
	}

	private static void OnModTreeInput(InputEvent ev)
	{
		try
		{
			if (ev is InputEventMouseButton mb && mb.ButtonIndex == MouseButton.Left)
			{
				if (mb.Pressed)
				{
					_treeDrag = false;
					_treePress = mb.Position;
					_treeSelBefore = (_modTree != null && GodotObject.IsInstanceValid(_modTree)) ? _modTree.GetSelected() : null;
				}
				else if (_treeDrag)
				{
					// 拖动结束：把拖拽过程中被 Tree 误选的行回滚
					if (_modTree != null && GodotObject.IsInstanceValid(_modTree) && _treeSelBefore != null
						&& GodotObject.IsInstanceValid(_treeSelBefore))
					{
						_modTree.SetSelected(_treeSelBefore, 0);   // Godot 4 签名是 (item, column:int)
					}
					_treeDrag = false;
				}
			}
			else if (ev is InputEventMouseMotion mm)
			{
				if (!Input.IsMouseButtonPressed(MouseButton.Left)) { return; }
				if (!_treeDrag && (mm.Position - _treePress).Length() >= DragThresholdPx)
				{
					_treeDrag = true;
				}
				if (_treeDrag && _modScroll != null && GodotObject.IsInstanceValid(_modScroll))
				{
					_modScroll.ScrollVertical -= (int)mm.Relative.Y;   // 内容跟着手指走
				}
			}
		}
		catch { }
	}

	private static void ApplySwitches(XWModRuntimeContext context)
	{
		FixPickCancel = ReadBoolSetting(context, "fixPickCancel", true);
		ReadScrollSettingsStatic(context);   // ★ v1.3.0：6 项滚动条配置
	}

	/// <summary>静态包装（ApplySwitches 是静态的，而读配置要写静态字段）。</summary>
	private static void ReadScrollSettingsStatic(XWModRuntimeContext context)
	{
		try
		{
			_scrollOn = ReadBoolSetting(context, KScrollOn, true);
			_scrollW = Math.Clamp(ReadIntSetting(context, KScrollW, 24), 8, 48);
			int alpha = Math.Clamp(ReadIntSetting(context, KScrollAlpha, 8), 0, 100);
			Color tc2 = ReadColorSetting(context, KScrollColor, new Color(0f, 0f, 0f, 1f));
			tc2.A = alpha / 100f;
			_trackColor = tc2;
			_grabColor = ReadColorSetting(context, KGrabColor, new Color(0.16f, 0.42f, 0.26f, 0.55f));
			_grabW = Math.Clamp(ReadIntSetting(context, KGrabW, 0), 0, 48);
			if (_grabW > _scrollW) { _grabW = _scrollW; }
		}
		catch { }
	}


	// 面板保存后重新读开关（这两个开关是"用到时读字段"，刷新后下一帧即生效）
	private static bool _settingsSubscribed;
	private static XWModRuntimeContext _ctxRef;

	/// <summary>★ 设置变更后要求"重刷已存在的滚动条"（否则要重启才能看到新宽度/颜色）。</summary>
	/// <summary>★ 铲子取消：抬起时登记、帧末判定。</summary>
	/// <summary>★ 按下那一刻铲子是否已经拿在手上（区分"拿起铲子"与"用铲子点空处"）。</summary>
	private static bool _pressStartShovelPick;
	private static bool _shovelCancelQueued;
	private static ulong _shovelCancelMs;
	/// <summary>★ 抬起点（视口坐标）：用于复刻 IsPlacementSurfacePoint 判定。</summary>
	private static Vector2 _shovelCancelPos;
	/// <summary>★ 抬起那一刻的植物总数（用于判定这一下是否真的铲到了）。</summary>
	private static int _shovelPlantCountBefore = -1;
	/// <summary>★ 抬起后等待多久再判定（毫秒）。给游戏物理帧完成铲除的时间。</summary>
	private const ulong ShovelWatchMs = 250UL;
	/// <summary>★ ShovelManager 缓存（避免每帧整树找）。</summary>
	private static object _shovelMgrCache;
	private static ulong _shovelMgrFrame;
	/// <summary>★ 铲子缓存预热节流（30 帧）。</summary>
	private static ulong _shovelWarmFrame;
	/// <summary>★ 铲子"刚拿起"的时刻（ms）；用于排除"拿起那一下"被误判为点空处。</summary>
	private static ulong _shovelPickedAtMs;
	/// <summary>★ 上一帧的 shovelPick（用于检测 false→true 边沿）。</summary>
	private static bool _prevShovelPick;

	/// <summary>每帧检测铲子由"未拿起"变"拿起"的边沿，记下时刻。</summary>
	private static void TrackShovelPickEdge()
	{
		try
		{
			object sm = GetShovelManagerFast();
			bool now = sm != null && ReadBoolMember(sm, "shovelPick");
			if (now && !_prevShovelPick) { _shovelPickedAtMs = Time.GetTicksMsec(); }
			_prevShovelPick = now;
		}
		catch { }
	}

	/// <summary>落点是否落在"铲子按钮"矩形内（UI 点击 ⇒ 不该触发取消）。</summary>
	private static bool IsPointOnShovelButton(object sm, Vector2 vp)
	{
		try
		{
			object btn = ReadObjectMember(sm, "shovelButton");
			if (btn is Control c && GodotObject.IsInstanceValid(c))
			{
				Rect2 r = c.GetGlobalRect();
				r = r.Grow(12f);          // 留一点余量（手指点偏也算 UI）
				if (r.HasPoint(vp)) { return true; }
			}
		}
		catch { }
		return false;
	}

	private static bool _scrollDirty;

	private static void OnSettingsChanged(System.Collections.Generic.IReadOnlyList<string> keys)
	{
		ApplySwitches(_ctxRef);
		_scrollDirty = true;   // 由 OnFrame 下一帧执行重刷
	}

internal static void Initialize(XWModRuntimeContext context)
	{
			try { SponsorQrRoot = context.PackageRoot; } catch { }
			EnableLog = false;   // ★ 2026-10-14 用户要求：默认关闭所有日志（原：EnableLog = ReadLogSetting(context)）

			_ctxRef = context;
			ApplySwitches(context);   // 从「Mod 配置」读取功能开关
			try
			{
				if (!_settingsSubscribed)
				{
					context.Settings.Changed += OnSettingsChanged;
					_settingsSubscribed = true;
				}
			}
			catch { }
		try
		{
			Log("初始化完成（滑动误选撤销 / 误触取消）。");
		}
		catch (Exception ex)
		{
			try { GD.PrintErr(P + "Initialize 异常（已吞）：" + ex.Message); } catch { }
		}
	}

	internal static void OnAllModsLoaded()
	{
		try
		{
			if (_started)
			{
				return;
			}
			_tree = Engine.GetMainLoop() as SceneTree;
			if (_tree == null)
			{
				Log("拿不到 SceneTree，本 Mod 不生效。");
				return;
			}
			_tick = Callable.From(new Action(OnFrame));
			_tree.Connect("process_frame", _tick);
			HookWindowInput();                  // ★ v1.0.3：触摸/鼠标按下位置采样
			_started = true;
			Log("已挂载 process_frame（BUILD-2235-SHOVELFIX：界外/UI 不取消铲子）。");
		}
		catch (Exception ex)
		{
			try { GD.PrintErr(P + "OnAllModsLoaded 异常（已吞）：" + ex.Message); } catch { }
		}
	}

	internal static void Shutdown()
	{
		try
		{
			if (_started && _tree != null && GodotObject.IsInstanceValid(_tree))
			{
				_tree.Disconnect("process_frame", _tick);
			}
		}
		catch (Exception ex)
		{
			try { GD.PrintErr(P + "Shutdown 异常（已吞）：" + ex.Message); } catch { }
		}
		finally
		{
			UnhookWindowInput();
			_started = false;
		}
	}

	// ================================================================ 每帧驱动

	private static void OnFrame()
	{
		try { SponsorQrInjector.Tick(Engine.GetMainLoop() as SceneTree, "手机操作优化", SponsorQrRoot, "Resources/SponsorQR.png"); } catch { }
			TryHookModTree();   // Mod 管理列表：拖动滚动（一次性挂钩）

		try
		{
			if (_tree == null || !GodotObject.IsInstanceValid(_tree))
			{
				return;
			}
			// ★ v1.3.0 滚动条优化：首帧挂钩信号 + 全树扫一次；之后纯事件驱动、零轮询
			try
			{
				if (!_scrollWalked)
				{
					_scrollWalked = true;
					HookScrollTune();
					if (_scrollOn && _tree.Root != null) { WalkScrollOnce(_tree.Root, 0); }
				}
				else if (_scrollDirty)
				{
					// ★ 配置改完**立刻重刷已有滚动条**（不必重启游戏）：清引用后重扫一次
					_scrollDirty = false;
					_scrollDone.Clear();
					if (_scrollOn && _tree.Root != null) { WalkScrollOnce(_tree.Root, 0); }
				}
			}
			catch { }
			FlushPendingPickCancel();                  // ② 帧末取消（在游戏 PickPacket 之后）
			TrackShovelPickEdge();                     // ★ 记录铲子"刚拿起"的时刻（false→true 边沿）
			FlushPendingShovelCancel();                // ★ 帧末取消铲子（空草坪/天空）
			// ★ 铲子缓存的低频维护：热路径（按下/抬起）只读缓存 ⇒ 必须有别处负责把缓存填上，
			//   否则玩家拿起铲子后第一次点空处会"没反应"（缓存还是空的）。
			try
			{
				ulong _sf = Engine.GetProcessFrames();
				if (_sf - _shovelWarmFrame >= 30UL)
				{
					_shovelWarmFrame = _sf;
					if (_shovelMgrCache == null || !(_shovelMgrCache is Node cn && GodotObject.IsInstanceValid(cn)))
					{
						GetShovelManager();   // 内部：仅在战斗中、且有 300 帧节流时才会真正整树搜
					}
				}
			}
			catch { }
			TrackMouseAndPick();                       // ①②共用的逐帧采样
			ScanPacketShows();                         // ①② 状态边沿维护
		}
		catch (Exception ex)
		{
			if (_diag < 100)
			{
				_diag = 100;
				Log("每帧驱动异常（本条只报一次）：" + ex.Message);
			}
		}
	}

	// ================================================================ ①② 逐帧采样

	/// <summary>
	/// ① 记录"左键刚按下"的位置（OnPressed 在松手时触发，需要按下的起点算位移）；
	/// ② 记录"本帧结束时当前拿起的卡"（下一帧的点击发生前，它就是点击前状态）。
	/// ★ 时序：输入派发（含 OnPressed 回调）发生在本帧 process_frame 之前，
	///   所以这里写的值对"下一帧的点击"而言就是点击前状态。
	///
	/// ★★ v1.0.3 变更：①的按下/当前位置**不再轮询鼠标**，改由 `Window.WindowInput`
	///    事件驱动（见 <see cref="OnWindowInputRelay"/>）——
	///    手机上"触摸→鼠标模拟"若未开启，`Input.IsMouseButtonPressed` 永远为 false，
	///    轮询拿不到按下位置 ⇒ 滑动撤销整个失效（实测）。
	///    `Window.WindowInput` 对触摸/鼠标**都**发，且窗口层不受节点 ProcessMode 门控。
	/// </summary>
	private static void TrackMouseAndPick()
	{
		try
		{
			// ── ① 按下位置：事件驱动，见 OnWindowInputRelay ─────────

			// ── ② 当前拿起的卡 ──────────────────────────────────────
			_prevPickId = 0;
			PacketPickControl ppc = GetPickControl();
			if (ppc != null)
			{
				TowerDefenseInGamePacketShow pick = ppc.packetPick;
				if (pick != null && GodotObject.IsInstanceValid(pick))
				{
					_prevPickId = pick.GetInstanceId();
				}
			}
		}
		catch { }
	}

	// ================================================================ 窗口输入：按下 / 当前位置采样

	private static bool _windowInputHooked;

	/// <summary>
	/// ② 抬起时记录"落点命中哪张卡槽卡"（若它 **在本手势按下之前就已经拿着** ⇒ 下一帧取消）。
	///
	/// ★★ v1.2.0 关键修正：原来只比"上一帧拿起的卡"，结果**同一次点击的抬起**也算命中
	///   （因为卡片是**按下**那一刻就被捡起来的）⇒ 刚拿起的卡立刻被取消，
	///   表现为"单击拿不起卡，只有拖走才有效"（真机实测）。
	///   现在必须满足 `_prevPickId == _pressStartPickId`：即"按下之前手上就已经是它"。
	/// 放在帧末执行的原因：游戏自己的 `OnPressed → PickPacket` 在同一帧的 GUI 派发里跑，
	/// 必须先让它跑完，我们再取消，否则会被它重新拿起。
	/// </summary>
	private static void NoteSlotRelease(Vector2 vpPos)
	{
		try
		{
			if (!FixPickCancel || _prevPickId == 0)
			{
				return;
			}
			// ★ 本手势按下之前手上拿的**就是这张**才允许取消；否则这次点它就是"把它捡起来"，不能撤。
			if (!_pressStartValid || _pressStartPickId != _prevPickId)
			{
				return;
			}
			TowerDefenseManager mgr = TowerDefenseManager.Instance;
			if (mgr == null || !GodotObject.IsInstanceValid(mgr))
			{
				return;
			}
			TowerDefenseInGameSeedBank sb = mgr.GetSeedBank();
			if (sb == null || !GodotObject.IsInstanceValid(sb) || sb.hasGameStarted != true)
			{
				return;
			}
			foreach (TowerDefenseInGamePacketShow c in sb.packetList)
			{
				if (c == null || !GodotObject.IsInstanceValid(c) || c.GetInstanceId() != _prevPickId)
				{
					continue;
				}
				// 命中判定：`GetRect()` 含 position、跨坐标系都不可靠 ⇒ 用自身矩形 + 局部鼠标
				Rect2 r = new Rect2(Vector2.Zero, c.Size);
				if (r.HasPoint(c.GetLocalMousePosition()))
				{
					_pendingPickCancel = _prevPickId;
					_pressStartPickId = 0;              // 防"触摸→鼠标"重复抬起再撤一次
					Log("②再点已拿起的卡：" + SafeKey(c) + " ⇒ 下一帧取消选择");
				}
				return;
			}
		}
		catch { }
	}

	/// <summary>② 帧末执行取消（在游戏 `PickPacket` 之后）。</summary>
	/// <summary>
	/// ★ 2026-10-13（手机实测新增）：**点空处取消铲子**。
	///
	/// 铲子由 `ShovelManager` 管理（`shovelPick` = 拿起中），与植物卡的 `packetPick` 完全独立，
	/// 所以原生只管卡片、我们的植物卡逻辑也管不到它 ✗。
	/// 判定方式：游戏自己每帧在 `ProcessShovelPick()` 里把"光标下的可铲植物"写进 `lastShovelPlant`，
	/// **它为空就说明落点没有可铲的东西**（空草坪/天空）⇒ 收起铲子；
	/// 有植物时**不动**（那一下是正常的铲除操作）。
	/// 放在帧末执行：让游戏自己的输入处理先跑完。
	/// </summary>
	private static void FlushPendingShovelCancel()
	{
		if (!_shovelCancelQueued)
		{
			return;
		}
		// ★ 只在战斗里处理（GetShovelManager 内部用 packetPickControl 判定，编辑器/菜单一律不做）
		//   注意：这里允许它刷新缓存（帧末、且已排队 ⇒ 频率极低）
		_shovelCancelQueued = false;
		try
		{
			object sm = GetShovelManager();
			if (sm == null) { return; }
			if (!ReadBoolMember(sm, "shovelPick")) { return; }   // 已经不是拿起状态
			// ★★ 自己铲（手机端点植物游戏不铲，见文件头注释）：
			//   铲到了(=落点确实有可铲植物) ⇒ 不取消；找不到植物 ⇒ 收起铲子。
			bool dug = TryShovelAt(sm, _shovelCancelPos);
			if (dug)
			{
				Log("铲子判定：落点有植物，已替你铲除（不取消铲子）");
				return;
			}
			InvokeNoArg(sm, "ShovelRelease");
			Log("铲子判定：落点没有可铲植物 ⇒ 收起铲子。");
		}
		catch { }
	}

	private static void FlushPendingPickCancel()
	{
		if (_pendingPickCancel == 0)
		{
			return;
		}
		ulong want = _pendingPickCancel;
		_pendingPickCancel = 0;
		try
		{
			PacketPickControl ppc = GetPickControl();
			if (ppc == null || !GodotObject.IsInstanceValid(ppc))
			{
				return;
			}
			TowerDefenseInGamePacketShow pick = ppc.packetPick;
			if (pick != null && GodotObject.IsInstanceValid(pick) && pick.GetInstanceId() == want)
			{
				ppc.PacketPickRelease();
				Log("②已取消误触的植物。");
			}
		}
		catch { }
	}

	/// <summary>② 待取消的卡槽卡实例 ID（0 = 无）。</summary>
	private static ulong _pendingPickCancel;

	private static void HookWindowInput()
	{
		try
		{
			if (_windowInputHooked)
			{
				return;
			}
			Window root = (_tree != null && GodotObject.IsInstanceValid(_tree)) ? _tree.Root : null;
			if (root == null)
			{
				return;
			}
			root.WindowInput += OnWindowInputRelay;
			_windowInputHooked = true;
			Log("窗口输入已挂载（WindowInput）—— 触摸/鼠标事件都会经过这里。");
		}
		catch (Exception ex)
		{
			try { GD.PrintErr(P + "挂载窗口输入异常：" + ex.Message); } catch { }
		}
	}

	private static void UnhookWindowInput()
	{
		try
		{
			if (!_windowInputHooked)
			{
				return;
			}
			Window root = (_tree != null && GodotObject.IsInstanceValid(_tree)) ? _tree.Root : null;
			if (root != null)
			{
				root.WindowInput -= OnWindowInputRelay;
			}
			_windowInputHooked = false;
		}
		catch { }
	}

	/// <summary>窗口坐标 → 视口（内容缩放后）坐标。组件收到的就是这个空间。</summary>
	private static Vector2 WindowToViewport(Vector2 windowPos)
	{
		try
		{
			Window root = (_tree != null && GodotObject.IsInstanceValid(_tree)) ? _tree.Root : null;
			if (root == null)
			{
				return windowPos;
			}
			return root.GetFinalTransform().AffineInverse() * windowPos;
		}
		catch
		{
			return windowPos;
		}
	}

	/// <summary>
	/// 窗口级输入采样：触摸/鼠标都走这里。
	/// · 按下（touch pressed / 左键按下）→ 记 `_downPos`（滑动起点）
	/// · 拖动 / 移动 / 抬起   → 记 `_lastPos`（当前点；抬起事件先于卡片 `Pressed` 派发）
	/// ★ `_downValid` 只在下一次按下时刷新，**抬起不清**——
	///   因为卡片 `Pressed` 是在抬起瞬间才派发的，那之后还要用 `_downPos` 算位移。
	/// </summary>
	private static int _evCount;

	private static void OnWindowInputRelay(InputEvent ev)
	{
		try
		{
			if (ev == null || ev.IsEcho())
			{
				return;
			}
			// ★ 按下分支：记录"按下那一刻铲子是否已经拿在手上"
			//   （用于区分"点图标拿起铲子"与"拿着铲子点空处"这两种点击）
			if ((ev is InputEventScreenTouch pt && pt.Pressed)
				|| (ev is InputEventMouseButton pm && pm.ButtonIndex == MouseButton.Left && pm.Pressed))
			{
				try
				{
					// ★ 热路径只读缓存（原来这里做整树搜索 + 反射查 AppDomain，是"战斗外点击卡死"的元凶）
					object sm0 = GetShovelManagerFast();
					_pressStartShovelPick = (sm0 != null) && ReadBoolMember(sm0, "shovelPick");
				}
				catch { _pressStartShovelPick = false; }
			}

			// ② 抬起 = 判定的时机（游戏 PickPacket 在同一帧 GUI 派发里，之后我们再取消）
			// ① 抬起 = 滑动误选的判定时机（卡片在**按下**时就已选中，必须等抬起才能算真实位移）
			if ((ev is InputEventScreenTouch rt && !rt.Pressed)
				|| (ev is InputEventMouseButton rm && rm.ButtonIndex == MouseButton.Left && !rm.Pressed))
			{
				Vector2 rp = (ev is InputEventScreenTouch t2) ? t2.Position : ((InputEventMouseButton)ev).Position;
				Vector2 vp = WindowToViewport(rp);
				NoteSlotRelease(vp);
				// ★ 2026-10-13（手机实测："点空草坪/天空仍保持铲子状态"）：
				//   铲子**不走** `packetPick`（植物卡）那套状态 ⇒ 原来完全没覆盖 ✗。
				//   这里在抬起时先登记"若铲子仍被拿起、且落点没有可铲植物 ⇒ 帧末放下铲子"。
				//   WARNING（PC 实测踩坑：铲子一点就被自动取消）：
				//   必须满足"**按下之前**铲子就已经拿在手上" —— 否则"点铲子图标把它拿起来"这个动作
				//   本身就会被当成"点空处"给取消掉（拿起瞬间 lastShovelPlant 必然为空）。
				//   依据：按下分支记录的 `_pressStartShovelPick`。
				try
				{
					object sm = GetShovelManagerFast();
					// ★ 刚拿起铲子的 300ms 内不判定（避免"拿起那一下"被当成点空处 —— "拿不了铲子"的另一半原因）
					bool freshPick = (Time.GetTicksMsec() - _shovelPickedAtMs) < 300UL && _shovelPickedAtMs != 0UL;
					bool onShovelBtn = IsPointOnShovelButton(sm, vp);
					if (sm != null && ReadBoolMember(sm, "shovelPick") && _pressStartShovelPick
						&& !freshPick && !onShovelBtn)
					{
						_shovelCancelQueued = true;
						_shovelCancelPos = vp;          // 保留（诊断用）
						_shovelPlantCountBefore = CountPlants();   // ★ 判定基准：植物数
						_shovelCancelMs = Time.GetTicksMsec();
					}
				}
				catch { }
			}
			_evCount++;
			if (DiagVerbose && (_evCount <= 40 || _evCount % 200 == 0))
			{
				string kind = ev.GetType().Name;
				Vector2 p = Vector2.Zero;
				bool pressed = false;
				if (ev is InputEventScreenTouch st) { p = st.Position; pressed = st.Pressed; }
				else if (ev is InputEventScreenDrag sd) { p = sd.Position; }
				else if (ev is InputEventMouseButton mb) { p = mb.Position; pressed = mb.Pressed; }
				else if (ev is InputEventMouseMotion mm) { p = mm.Position; }
				Log("输入#" + _evCount + " " + kind + " raw=" + (int)p.X + "," + (int)p.Y
					+ " vp=" + (int)WindowToViewport(p).X + "," + (int)WindowToViewport(p).Y
					+ (pressed ? " [按下]" : ""));
			}
			switch (ev)
			{
				case InputEventScreenTouch t:
					ApplyPointer(t.Position, t.Pressed);
					break;
				case InputEventScreenDrag d:
					_lastPos = WindowToViewport(d.Position);
					_lastValid = true;
					break;
				case InputEventMouseButton mb:
					if (mb.ButtonIndex == MouseButton.Left)
					{
						ApplyPointer(mb.Position, mb.Pressed);
					}
					break;
				case InputEventMouseMotion mm:
					_lastPos = WindowToViewport(mm.Position);
					_lastValid = true;
					break;
			}
		}
		catch { }
	}

	private static void ApplyPointer(Vector2 windowPos, bool pressed)
	{
		Vector2 p = WindowToViewport(windowPos);
		if (DiagVerbose)
		{
			Log("指针" + (pressed ? "按下" : "抬起") + " vp=" + (int)p.X + "," + (int)p.Y);
		}
		if (pressed)
		{
			// ★ v1.2.0：只在"本手势的第一次按下"时记录"按下之前手上拿的卡"。
			//   手机上同一次触摸会被派发两次（InputEventMouseButton + InputEventScreenTouch），
			//   若第二次按下也重记，就可能把"刚被这次点击捡起来的那张"记进去 ⇒ ② 会误取消。
			ulong now = Time.GetTicksMsec();
			if (!_gestureOpen || (now - _lastPressMs) > GestureGapMs)
			{
				_pressStartPickId = _prevPickId;
				_pressStartValid = true;
				_gestureOpen = true;
			}
			_lastPressMs = now;
			_pointerDown = true;
			_downPos = p;
			_downValid = true;
			_lastPos = p;
			_lastValid = true;
		}
		else
		{
			_pointerDown = false;
			_gestureOpen = false;
			_lastPos = p;
			_lastValid = true;
		}
	}

	/// <summary>①② 给卡挂事件钩子：按"这张卡现在在不在目标容器里"逐帧维护。</summary>
	// ── ★ 性能：卡节点集合缓存（整树递归采集很贵，5 帧复用一次）──
	private static readonly List<TowerDefenseInGamePacketShow> _cardPoolCache =
		new List<TowerDefenseInGamePacketShow>(64);
	private static ulong _cardPoolFrame;

	/// <summary>
	/// 取场上全部 `TowerDefenseInGamePacketShow`（卡池 / 卡槽 / 图鉴 / 编辑卡牌库）。
	/// 旧实现**每帧**整树递归 + 新建 List（真机 1.63ms/帧）。这里 5 帧复用一次，
	/// 并逐个 `IsInstanceValid` 校验——卡走对象池被回收、或换场景时都会立刻失效重扫。
	/// </summary>
	private static List<TowerDefenseInGamePacketShow> PacketShowsCached()
	{
		ulong f = Engine.GetProcessFrames();
		if (_cardPoolCache.Count > 0 && f - _cardPoolFrame < 5UL)
		{
			bool ok = true;
			for (int i = 0; i < _cardPoolCache.Count; i++)
			{
				TowerDefenseInGamePacketShow c = _cardPoolCache[i];
				if (c == null || !GodotObject.IsInstanceValid(c)) { ok = false; break; }
			}
			if (ok) { return _cardPoolCache; }
		}
		_cardPoolFrame = f;
		_cardPoolCache.Clear();
		CollectByType(_tree != null && GodotObject.IsInstanceValid(_tree) ? _tree.Root : null,
			0, _cardPoolCache, 500);
		return _cardPoolCache;
	}

	private static void ScanPacketShows()
	{
		try
		{
			TowerDefenseManager mgr = TowerDefenseManager.Instance;
			if (mgr == null || !GodotObject.IsInstanceValid(mgr))
			{
				return;
			}
			bool inChoose = false;
			TowerDefenseInGameSeedBank seedBank = mgr.GetSeedBank();
			if (seedBank != null && GodotObject.IsInstanceValid(seedBank))
			{
				inChoose = !seedBank.hasGameStarted;      // 选卡阶段
			}

			// ★ 性能（2026-10-13 真机探针：本 Mod 回调 均 1.63ms/帧、p95 3.3ms）：
			//   这里原先**每帧**都做一次 `CollectByType(tree.Root, …)` **整树递归** + `new List`。
			//   卡池成员不可能每帧都变 ⇒ 5 帧复用一次采集结果；下面逐帧的状态边沿逻辑不受影响。
			var cards = PacketShowsCached();

			// ★ 收集"目标容器成员"集合，容器外的同名卡清掉钩子标记
			//   （卡走对象池，ResetForPool → ClearEventHandlers 会把我们挂的事件清掉，
			//     若标记还留着就永远不会重挂 ⇒ 功能静默失效）。
			var bankMembers = new HashSet<ulong>();
			var slotMembers = new HashSet<ulong>();
			var editorMembers = new HashSet<ulong>();

			foreach (TowerDefenseInGamePacketShow c in cards)
			{
				if (c == null || !GodotObject.IsInstanceValid(c))
				{
					continue;
				}
				ulong id = c.GetInstanceId();

				// ★ 2026-10-13（用户需求 1）：**①「滑动误选撤销」已整块删除**
				//   （连同 fixScrollMisPick 配置项）。原实现在这里逐帧看卡池卡的
				//   `select` false→true 边沿、再按"按下点→抬起点"的位移判定是否撤销误选；
				//   本包改为滚动条功能后不再需要。下面只保留 ②「点空处取消拿卡」。
				if (FixPickCancel && inChoose == false && seedBank != null
					&& GodotObject.IsInstanceValid(seedBank) && InList(seedBank.packetList, c))
				{
					slotMembers.Add(id);
				}
			}

			// 容器外的同名卡：清标记（下次进容器会重新挂；此刻事件已被对象池清空）
			foreach (TowerDefenseInGamePacketShow c in cards)
			{
				if (c == null || !GodotObject.IsInstanceValid(c))
				{
					continue;
				}
				ulong id = c.GetInstanceId();
				if (!bankMembers.Contains(id) && !editorMembers.Contains(id))
				{
					_prevBankSel.Remove(id);       // 卡离开卡池 ⇒ 清掉它的选中状态记录
				}
			}
		}
		catch (Exception ex)
		{
			if (_diag < 101)
			{
				_diag = 101;
				Log("卡钩子维护异常（本条只报一次）：" + ex.Message);
			}
		}
	}

	private static Vector2 GetMousePos()
	{
		try
		{
			Viewport vp = _tree != null && GodotObject.IsInstanceValid(_tree) ? _tree.Root : null;
			if (vp != null && GodotObject.IsInstanceValid(vp))
			{
				return vp.GetMousePosition();
			}
		}
		catch { }
		return Vector2.Zero;
	}

	/// <summary>
	/// ★★ 2026-10-14 重写（用户实测两个问题后）：
	///   问题 A：PC 上功能失效 —— 上一版按"父链类型名是否含 TowerDefenseBattleFeature"判断，
	///           而树里节点类型名不一定含它 ⇒ 永远判 false ⇒ `GetShovelManager()` 恒为 null ✗。
	///   问题 B：**战斗外点击卡死** —— 每次按下/抬起都调本方法，而它内部做了
	///           「遍历 AppDomain 反射找 Global 类型」+「整树递归搜 ShovelManager」✗✗
	///           ⇒ 菜单/图鉴里每一次点击都很贵、还可能抛异常打断输入派发。
	///
	/// 现在：**只在战斗里干活，判据要便宜** ——
	///   `packetPickControl != null` 只在战斗场景里成立（主菜单/图鉴/商店/关卡编辑器都没有），
	///   一次反射即可；搜索最多每 300 帧一次，且**热路径（按下/抬起）只读缓存、不搜索**。
	/// </summary>
	private static object GetShovelManager()
	{
		try
		{
			// ★ 便宜且可靠的"在战斗中"判据：战斗才有 packetPickControl
			if (GetPickControl() == null) { return null; }

			ulong f = Engine.GetProcessFrames();
			if (_shovelMgrCache != null && f - _shovelMgrFrame < 300UL)
			{
				if (_shovelMgrCache is Node cached && GodotObject.IsInstanceValid(cached)) { return _shovelMgrCache; }
				_shovelMgrCache = null;
			}
			_shovelMgrFrame = f;
			Node root = (_tree != null && GodotObject.IsInstanceValid(_tree)) ? _tree.Root : null;
			if (root == null) { return null; }
			// 战斗中 ShovelManager 只有一个（关卡编辑器那个不在战斗场景里），直接找即可
			_shovelMgrCache = FindByTypeName(root, "ShovelManager", 0);
			return _shovelMgrCache;
		}
		catch { return null; }
	}

	/// <summary>
	/// ★ 热路径专用：**只读缓存**，绝不搜索（按下/抬起时调用）。
	/// 缓存为空就直接认为"没拿着铲子" —— 不为了这一次点击去做整树搜索。
	/// </summary>
	private static object GetShovelManagerFast()
	{
		try
		{
			if (_shovelMgrCache is Node n && GodotObject.IsInstanceValid(n)) { return _shovelMgrCache; }
		}
		catch { }
		return null;
	}

	/// <summary>按类型名整树查找（★ 只允许在低频路径调用：本 Mod 里最多每 300 帧一次）。</summary>
	/// <summary>
	/// 落点是否有可铲植物：调游戏自带
	/// `PacketPickControl.FindPlantAtPosition(Vector2 mousePos, CHARACTER_CAMP targetCamp = PLANT)`
	/// （源码 739 行，遍历角色按距离比较 —— 纯坐标，故喂触摸坐标即可，不依赖鼠标模拟）。
	/// 找不到方法就返回 false（= 当空处处理；最差是"点草坪也取消"，不会卡死）。
	/// </summary>
	/// <summary>
	/// ★★ 复刻游戏 `TowerDefenseBattleFeatureMap.ResolveViewportInputPosition`（源码 1267-1270）：
	///   `mapControl.GetCanvasTransform().AffineInverse() * viewportPos`
	///   ⇒ 把屏幕/视口坐标转成**地图世界坐标**（游戏内部 mousePos 就是这个坐标系）。
	/// 取不到 mapControl 就原样返回（退化为旧行为，至少不会崩）。
	/// </summary>
	private static Vector2 ViewportToMapWorld(PacketPickControl ppc, Vector2 vp)
	{
		try
		{
			object mc = ReadObjectMember(ppc, "mapControl");
			if (mc is CanvasItem ci && GodotObject.IsInstanceValid(ci))
			{
				return ci.GetCanvasTransform().AffineInverse() * vp;
			}
		}
		catch { }
		return vp;
	}

	private static bool HasPlantAt(Vector2 vp)
	{
		try
		{
			PacketPickControl ppc = GetPickControl();
			if (ppc == null || !GodotObject.IsInstanceValid(ppc)) { return false; }
			// ★★ 关键：先把"视口坐标"转成**地图世界坐标**（复刻游戏 ResolveViewportInputPosition）
			//   —— FindPlantAtPosition 比的是 GetLogicalGlobalPosition()，两者必须同坐标系。
			Vector2 world = ViewportToMapWorld(ppc, vp);
			foreach (System.Reflection.MethodInfo mi in ppc.GetType().GetMethods(
				System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance))
			{
				if (mi.Name != "FindPlantAtPosition") { continue; }
				System.Reflection.ParameterInfo[] ps = mi.GetParameters();
				if (ps.Length < 1) { continue; }
				object[] args = new object[ps.Length];
				args[0] = world;
				bool ok = true;
				for (int i = 1; i < ps.Length; i++)
				{
					System.Type pt = ps[i].ParameterType;
					try
					{
						if (pt.IsEnum) { args[i] = System.Enum.Parse(pt, "PLANT"); }
						else if (pt.IsValueType) { args[i] = System.Activator.CreateInstance(pt); }
						else { args[i] = null; }
					}
					catch { ok = false; break; }
				}
				if (!ok) { continue; }
				object r = mi.Invoke(ppc, args);
				if (r is GodotObject go) { return GodotObject.IsInstanceValid(go); }
				return r != null;
			}
		}
		catch { }
		return false;
	}

	/// <summary>
	/// 复刻游戏 `PacketPickControl.IsPlacementSurfacePoint(Vector2)`（源码 1439 行）：
	///   · 点在 `mapFeature.groundRect` 内 ⇒ 算可放置面 ✓
	///   · 否则按 `TowerDefenseManager.GetMapGridPosFromMouse` 换算格子，格子有效且该格有 cell ⇒ 可放置面 ✓
	///   · 都否 ⇒ "空处"（天空/草坪外）✗
	///
	/// 为什么必须复刻而不直接调它：那是 **private**，且它依赖"鼠标位置"；
	/// 手机上触摸→鼠标模拟未开启，所以本 Mod 自带坐标、自己算（语义与游戏一致 ✓）。
	/// 这里的坐标是**视口坐标**（与 Godot 的 mousePos 同一坐标系 ✓）。
	/// </summary>
	/// <summary>
	/// 当前场上植物数量（用于判定"这一下点击是否真的铲掉了植物"）。
	/// `TowerDefenseManager.GetPlant()` 返回 Array；取不到返回 -1（调用方据此放弃判定，不做取消）。
	/// </summary>
	/// <summary>
	/// ★★ 替游戏完成"点植物铲除"（手机端点植物不铲，见文件头说明）。
	/// 返回 true = 落点确实有可铲植物（并已铲掉）；false = 没有植物。
	/// 多候选坐标逐个试：任一候选定位到"有可铲植物的格子"就采用 —— 这样不必事先知道
	/// 手机上触摸坐标与游戏内部坐标系的准确换算（那个坑已栽过两次）。
	/// </summary>
	private static bool TryShovelAt(object sm, Vector2 vp)
	{
		// true = 落点格确实有植物（已铲）⇒ 不取消；false = 没选到植物 ⇒ 收起铲子
		try
		{
			TowerDefenseManager mgr = TowerDefenseManager.Instance;
			if (mgr == null || !GodotObject.IsInstanceValid(mgr)) { return false; }
			object cfg = ReadObjectMember(sm, "shovelConfig");
			object mf = ReadObjectMember(sm, "mapFeature") ?? ReadObjectMember(sm, "_mapFeature");

			Vector2 world = vp;
			Node mc = ReadObjectMember(sm, "mapControl") as Node;
			try { if (mc is CanvasItem ci) { world = ci.GetCanvasTransform().AffineInverse() * vp; } } catch { }

			// ① 落点格 + 真实高度百分比
			// ★★ 界外 ⇒ **不取消**（多半是在点 UI 按钮，例如铲子图标本身 —— 这正是"拿不了铲子"的根因）
			Vector2I g = mgr.GetMapGridPosFromMouse(world);
			if (!mgr.CheckMapGridPosIn(g))
			{
				Log("铲子判定：落点(" + (int)vp.X + "," + (int)vp.Y + ")在方格范围外 ⇒ 视为 UI 点击，不取消");
				return true;
			}
			TowerDefenseCellInstance cell = TowerDefenseManager.GetMapCell(g);
			if (cell == null || !GodotObject.IsInstanceValid(cell))
			{
				Log("铲子判定：格子(" + g.X + "," + g.Y + ")没有 cell ⇒ 视为非战场，不取消");
				return true;
			}

			double pct = 0.5;
			try
			{
				double ground = 0.0;
				object o = InvokeWith(mf, "GetGroundHeight", new object[] { cell });
				if (o is double dd) { ground = dd; } else if (o is float ff) { ground = ff; }
				Vector2 cellPos = mgr.GetMapCellPos(g);
				Vector2 gsize = mgr.GetMapGridSize();
				if (gsize.Y > 0.01f) { pct = (world - cellPos + new Vector2(0f, (float)ground)).Y / gsize.Y; }
			}
			catch { }
			if (pct < 0.0) { pct = 0.0; }
			if (pct > 1.0) { pct = 1.0; }

			// ② 该格里按"真实高度优先 + 0.1~0.9 逐档"探测（修：原来只探 0.5 ⇒ 常误判空格）
			double[] probes = new double[] { pct, 0.1, 0.3, 0.5, 0.7, 0.9 };
			double hit = -1.0;
			foreach (double p in probes)
			{
				if (p < 0.0 || p > 1.0) { continue; }
				try
				{
					object ch = InvokeWith(cell, "GetShovelCharacter", new object[] { p, cfg });
					if (ch is GodotObject cgo && GodotObject.IsInstanceValid(cgo)) { hit = p; break; }
				}
				catch { }
			}

			if (hit < 0.0)
			{
				// 该格确实没有植物 ⇒ 点空处 ⇒ 收起（附带报一下最近植物格，便于排查）
				Log("铲子判定：格子(" + g.X + "," + g.Y + ")各高度档都没有植物（战场内空处）⇒ 收起铲子");
				return false;
			}

			// ③ 命中 ⇒ 铲
			Log("铲子判定：格子(" + g.X + "," + g.Y + ")高度 " + hit.ToString("0.00") + " 有植物 ⇒ 执行铲除");
			try
			{
				object can = InvokeWith(cell, "CanShovel", new object[] { hit, cfg });
				if (can is bool cb && cb) { InvokeWith(cell, "Shovel", new object[] { cfg, hit }); }
			}
			catch { }
			return true;
		}
		catch (Exception ex)
		{
			try { Log("铲子判定异常（按未选到处理）：" + ex.Message); } catch { }
		}
		return false;
	}

	/// <summary>
	/// 执行铲除：优先用游戏的 `ShovelCommand`（权威入口），退路是"取出植物所在格 → cell.Shovel"。
	/// </summary>
	private static void InvokeShovel(TowerDefenseManager mgr, GodotObject plant, object cfg)
	{
		try
		{
			// ① 权威入口：ShovelCommand（静态方法，签名 reflect 探测）
			try
			{
				System.Type sc = null;
				foreach (System.Reflection.Assembly asm in AppDomain.CurrentDomain.GetAssemblies())
				{
					sc = asm.GetType("ShovelCommand", false);
					if (sc != null) { break; }
				}
				if (sc != null)
				{
					foreach (System.Reflection.MethodInfo mi in sc.GetMethods(
						System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic
						| System.Reflection.BindingFlags.Static))
					{
						System.Reflection.ParameterInfo[] ps = mi.GetParameters();
						if (ps.Length < 1) { continue; }
						// 只调用"第一个参数能接受该植物"的重载
						if (!ps[0].ParameterType.IsInstanceOfType(plant)) { continue; }
						object[] args = new object[ps.Length];
						args[0] = plant;
						bool ok = true;
						for (int i = 1; i < ps.Length; i++)
						{
							System.Type pt = ps[i].ParameterType;
							try
							{
								if (pt == typeof(double)) { args[i] = 0.5; }
								else if (pt == typeof(float)) { args[i] = 0.5f; }
								else if (pt.IsEnum) { args[i] = System.Enum.Parse(pt, "PLANT"); }
								else if (pt.IsValueType) { args[i] = System.Activator.CreateInstance(pt); }
								else { args[i] = null; }
							}
							catch { ok = false; break; }
						}
						if (!ok) { continue; }
						mi.Invoke(null, args);
						Log("铲子判定：已通过 ShovelCommand 铲除");
						return;
					}
				}
			}
			catch { }

			// ② 退路：把植物位置换算成格子 → 该格 Shovel
			try
			{
				object lp = InvokeWith(plant, "GetLogicalGlobalPosition", new object[0]);
				if (lp is Vector2 w)
				{
					Vector2I g = mgr.GetMapGridPosFromMouse(w);
					if (mgr.CheckMapGridPosIn(g))
					{
						TowerDefenseCellInstance cell = TowerDefenseManager.GetMapCell(g);
						if (cell != null && GodotObject.IsInstanceValid(cell))
						{
							InvokeWith(cell, "Shovel", new object[] { cfg, 0.5 });
							Log("铲子判定：已通过 cell.Shovel 铲除（格 " + g.X + "," + g.Y + "）");
						}
					}
				}
			}
			catch { }
		}
		catch { }
	}

	/// <summary>把视口坐标按 mapControl 的 CanvasTransform 逆变换（复刻游戏 ResolveViewportInputPosition）。</summary>
	private static Vector2 SafeWorld(CanvasItem ci, Vector2 vp)
	{
		try { return ci.GetCanvasTransform().AffineInverse() * vp; } catch { return vp; }
	}

	/// <summary>诊断：世界坐标 → 格子，并列出该格判定结果。</summary>
	private static void Probe(System.Text.StringBuilder sb, string label, TowerDefenseManager mgr, Vector2 world)
	{
		try
		{
			Vector2I g = mgr.GetMapGridPosFromMouse(world);
			bool inside = false, hasCell = false;
			try { inside = mgr.CheckMapGridPosIn(g); } catch { }
			try { hasCell = TowerDefenseManager.GetMapCell(g) != null; } catch { }
			sb.Append(" ").Append(label).Append("=(").Append((int)world.X).Append(",").Append((int)world.Y)
				.Append(")→grid(").Append(g.X).Append(",").Append(g.Y).Append(")界内=").Append(inside)
				.Append("有格=").Append(hasCell);
		}
		catch { }
	}

	/// <summary>视口坐标 → 地图坐标的多个候选（顺序：游戏自己用的换算优先）。</summary>
	private static System.Collections.Generic.List<Vector2> CoordCandidates(Node mc, Vector2 vp)
	{
		System.Collections.Generic.List<Vector2> list = new System.Collections.Generic.List<Vector2>();
		try
		{
			if (mc is CanvasItem ci && GodotObject.IsInstanceValid(ci))
			{
				// ★★ 首选：**游戏自己的鼠标位置**（Godot 维护；手机能正常"种植物"证明这条管线在手机上是对的）
				//   复刻 TowerDefenseBattleFeatureMap.ResolveViewportInputPosition：
				//     mousePos = mapControl.GetCanvasTransform().AffineInverse() * viewport.GetMousePosition()
				//   之前喂的是"触摸事件坐标"（另一套坐标）⇒ 4 个候选全落空 ✗。
				try
				{
					Vector2 gp = ci.GetViewport()?.GetMousePosition() ?? Vector2.Zero;
					if (gp != Vector2.Zero) { list.Add(ci.GetCanvasTransform().AffineInverse() * gp); }
				}
				catch { }
				try { list.Add(ci.GetCanvasTransform().AffineInverse() * vp); } catch { }
				try { list.Add(ci.GetGlobalTransformWithCanvas().AffineInverse() * vp); } catch { }
				try { list.Add(ci.GetGlobalTransform().AffineInverse() * vp); } catch { }
				try { if (ci is Control ctl) { list.Add(vp - ctl.GlobalPosition); } } catch { }
			}
		}
		catch { }
		list.Add(vp);
		return list;
	}

	/// <summary>按名字 + 参数个数反射调用；参数按目标形参类型尽力转换（供本 Mod 调游戏方法用）。</summary>
	private static object InvokeWith(object o, string name, object[] args)
	{
		if (o == null) { return null; }
		try
		{
			foreach (System.Reflection.MethodInfo mi in o.GetType().GetMethods(
				System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic
				| System.Reflection.BindingFlags.Instance))
			{
				if (mi.Name != name) { continue; }
				System.Reflection.ParameterInfo[] ps = mi.GetParameters();
				if (ps.Length != args.Length) { continue; }
				object[] conv = new object[ps.Length];
				bool ok = true;
				for (int i = 0; i < ps.Length; i++)
				{
					object a = args[i];
					System.Type pt = ps[i].ParameterType;
					if (a == null)
					{
						conv[i] = pt.IsValueType ? System.Activator.CreateInstance(pt) : null;
						continue;
					}
					if (pt.IsInstanceOfType(a)) { conv[i] = a; continue; }
					try
					{
						if (pt == typeof(double)) { conv[i] = System.Convert.ToDouble(a); }
						else if (pt == typeof(float)) { conv[i] = (float)System.Convert.ToDouble(a); }
						else { conv[i] = System.Convert.ChangeType(a, pt); }
					}
					catch { ok = false; break; }
				}
				if (!ok) { continue; }
				return mi.Invoke(o, conv);
			}
		}
		catch { }
		return null;
	}

	private static int CountPlants()
	{
		try
		{
			TowerDefenseManager mgr = TowerDefenseManager.Instance;
			if (mgr == null || !GodotObject.IsInstanceValid(mgr)) { return -1; }
			Godot.Collections.Array arr = mgr.GetPlant();
			return arr == null ? -1 : arr.Count;
		}
		catch { return -1; }
	}

	private static bool IsPlacementSurfacePoint(Vector2 vp)
	{
		try
		{
			PacketPickControl ppc = GetPickControl();
			if (ppc == null || !GodotObject.IsInstanceValid(ppc)) { return false; }
			// ① groundRect（草坪矩形）内即为可放置面
			try
			{
				object mf = ReadObjectMember(ppc, "mapFeature");
				if (mf != null)
				{
					object gr = ReadObjectMember(mf, "groundRect");
					if (gr is Rect2 r && r.HasPoint(vp)) { return true; }
				}
			}
			catch { }
			// ② 换算到格子：格子有效且有 cell ⇒ 可放置面
			try
			{
				TowerDefenseManager mgr = TowerDefenseManager.Instance;
				if (mgr == null || !GodotObject.IsInstanceValid(mgr)) { return false; }
				Vector2I g = mgr.GetMapGridPosFromMouse(vp);
				if (mgr.CheckMapGridPosIn(g))
				{
					return TowerDefenseManager.GetMapCell(g) != null;
				}
			}
			catch { }
		}
		catch { }
		return false;
	}

	private static Node FindByTypeName(Node node, string typeName, int depth)
	{
		try
		{
			if (node == null || depth > 24) { return null; }
			if (node.GetType().Name == typeName) { return node; }
			int c = node.GetChildCount();
			for (int i = 0; i < c; i++)
			{
				Node r = FindByTypeName(node.GetChild(i), typeName, depth + 1);
				if (r != null) { return r; }
			}
		}
		catch { }
		return null;
	}

	/// <summary>反射读 bool 字段/属性（兼容两种）。</summary>
	private static bool ReadBoolMember(object o, string name)
	{
		try
		{
			System.Type t = o.GetType();
			var fi = t.GetField(name);
			if (fi != null) { object v = fi.GetValue(o); return v is bool b && b; }
			var pi = t.GetProperty(name);
			if (pi != null) { object v = pi.GetValue(o); return v is bool b2 && b2; }
		}
		catch { }
		return false;
	}

	/// <summary>反射读对象字段（取不到返回 null）。</summary>
	private static object ReadObjectMember(object o, string name)
	{
		try
		{
			System.Type t = o.GetType();
			var fi = t.GetField(name);
			if (fi != null) { return fi.GetValue(o); }
			var pi = t.GetProperty(name);
			if (pi != null) { return pi.GetValue(o); }
		}
		catch { }
		return null;
	}

	/// <summary>反射调用无参方法。</summary>
	private static void InvokeNoArg(object o, string name)
	{
		try
		{
			System.Type t = o.GetType();
			var mi = t.GetMethod(name, System.Reflection.BindingFlags.Public
				| System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance,
				null, System.Type.EmptyTypes, null);
			if (mi != null) { mi.Invoke(o, null); }
		}
		catch { }
	}

	private static PacketPickControl GetPickControl()
	{
		try
		{
			TowerDefenseManager mgr = TowerDefenseManager.Instance;
			if (mgr == null || !GodotObject.IsInstanceValid(mgr))
			{
				return null;
			}
			return mgr.GetPacketPickControl();
		}
		catch
		{
			return null;
		}
	}

	/// <summary>
	/// ①b 自制关卡：撤销"滑动误选"。那里点卡 = `LevelEditorPacketBank.PacketChoose()`
	/// ⇒ `packetPickControl.PickPacket(packet)`（把卡"拿起来"准备往地图上摆）。
	/// 撤销 = `PacketPickRelease()`（放下）+ 把卡面 `Reset()`（清掉已选高亮）。
	/// </summary>
	private static void CancelEditorPick(TowerDefenseInGamePacketShow card)
	{
		try
		{
			LevelEditorPacketBank eb = LevelEditorPacketBank.Instance;
			if (eb == null || !GodotObject.IsInstanceValid(eb))
			{
				return;
			}
			TowerDefenseBattleFeatureMap mf = eb.mapFeature;
			if ((mf == null || !GodotObject.IsInstanceValid(mf))
				&& LevelEditorMapEditor.instance != null
				&& GodotObject.IsInstanceValid(LevelEditorMapEditor.instance))
			{
				mf = LevelEditorMapEditor.instance.mapFeature;
			}
			if (mf != null && GodotObject.IsInstanceValid(mf)
				&& mf.packetPickControl != null && GodotObject.IsInstanceValid(mf.packetPickControl))
			{
				mf.packetPickControl.PacketPickRelease();
			}
			if (card != null && GodotObject.IsInstanceValid(card))
			{
				try { card.Reset(); } catch { }
			}
			Log("①自制关卡：滑动误选已撤销（" + SafeKey(card) + "）");
		}
		catch (Exception ex)
		{
			if (_diag < 107)
			{
				_diag = 107;
				Log("自制关卡撤销异常（本条只报一次）：" + ex.Message);
			}
		}
	}

	/// <summary>这张卡是否挂在**选卡界面的卡池**（`TowerDefenseInGamePacketBank`）之下。</summary>
	private static bool IsInChooseBank(Node node)
	{
		try
		{
			for (Node p = node?.GetParent(); p != null; p = p.GetParent())
			{
				if (p.GetType().Name == "TowerDefenseInGamePacketBank")
				{
					return true;
				}
			}
		}
		catch { }
		return false;
	}

	private static bool InList(Godot.Collections.Array<TowerDefenseInGamePacketShow> list,
		TowerDefenseInGamePacketShow card)
	{
		try
		{
			if (list == null || card == null)
			{
				return false;
			}
			ulong id = card.GetInstanceId();
			foreach (TowerDefenseInGamePacketShow c in list)
			{
				if (c != null && GodotObject.IsInstanceValid(c) && c.GetInstanceId() == id)
				{
					return true;
				}
			}
		}
		catch { }
		return false;
	}

	private static string SafeKey(TowerDefenseInGamePacketShow card)
	{
		try
		{
			TowerDefensePacketConfig cfg = card.config;
			return (cfg != null && GodotObject.IsInstanceValid(cfg)) ? cfg.saveKey : "?";
		}
		catch
		{
			return "?";
		}
	}

	/// <summary>
	/// 按**类型**递归收集节点（深度优先，最多 max 个）。
	///
	/// ★★ v1.0.3 关键修复：`is T` 判定天然覆盖子类 ——
	///   `CollectByClassName` 用 `GetType().Name == 类名` **精确相等**，
	///   而关卡/章节入口的实际类型是 `DragMenuSelectItemlevel` / `DragMenuSelectItemChapter`
	///   （都是 `DragMenuSelectItem` 的**子类**）⇒ 永远匹配不上 ⇒ 按压反馈整个没生效（实测）。
	/// </summary>
	private static void CollectByType<T>(Node node, int depth, List<T> outList, int max) where T : class
	{
		try
		{
			if (node == null || !GodotObject.IsInstanceValid(node) || outList.Count >= max || depth > 40)
			{
				return;
			}
			if (node is T t)
			{
				outList.Add(t);
			}
			int n = node.GetChildCount();
			for (int i = 0; i < n; i++)
			{
				CollectByType(node.GetChild(i), depth + 1, outList, max);
			}
		}
		catch { }
	}

	private static void Log(string msg)
	{
		if (!EnableLog)
		{
			return;
		}
		try { GD.Print(P + msg); } catch { }
	}
}
