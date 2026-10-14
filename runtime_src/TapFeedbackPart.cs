using System;
using System.Collections.Generic;
using Godot;
using PVZHE.ModEditor.ModSystem;

/// <summary>
/// 「点击反馈与自动拾取」Mod 的托管运行时入口。
///
/// ★ 本 Mod 是从「手机操作优化」（MobileUXFixes）**拆出来**的：原 Mod 只保留
///   ①选卡界面滑动误选撤销 / ②战斗中误触取消；原来的 ③④ 搬到这里独立成一个包。
///
/// ── 两个功能（对应手机版实测问题，2026-09-29）────────────────────
///
/// ③ 关卡/章节点击无反馈
///    根因：`DragMenuSelectItem`（关卡 `DragMenuSelectItemlevel`、章节
///    `DragMenuSelectItemChapter` 的共同基类）只挂了 `button.Pressed`，**没有按压视觉**。
///    方案：挂 `Button.ButtonDown / ButtonUp / MouseExited`，把 `graphics` 压暗 + 缩小，
///    抬起还原 —— 与游戏自家 `NinePatchButtonBase` 用 `ButtonDown` 做按压视觉同一套路。
///    ⚠️ 手机端"触摸→鼠标"是**同一帧里按下+抬起**（还会紧跟一个 MouseExited），
///       若立刻还原，变暗那一帧根本不会被渲染 ⇒ 玩家看不到任何反馈（实测就是这个原因）
///       ⇒ 按下后至少保持 `PressVisibleMs` 毫秒再还原。
///
/// ④ 无法关闭自动拾取阳光 / 金币
///    根因（源码实证，两个独立存档特性键）：
///        阳光 `TowerDefenseSunBase`:  autoCollect = GetFeatureValue("SunCollect") != 0
///        金币 `TowerDefenseCoinBase`: autoCollect = GetFeatureValue("CoinCollect") > 0 && 场上无吸金磁
///    游戏没给玩家任何入口去改（只有商店"买一次=永久开"）。
///    ★ v1.3.0 定稿语义：**Mod 配置页的两个开关 = 游戏那个值本身**。
///        开 ⇒ 写 1（自动拾取生效，且商店里该商品显示"已购买"）
///        关 ⇒ 写 0（不自动拾取，且商店里重新显示"可购买"）
///      每 30 帧双向对齐一次 ⇒ 配置页 / 实战 / 商店三处永远一致
///      （玩家在商店里买了 ⇒ 配置自动变"开"，绝不抹掉他花钱买来的状态）。
///    ⚠️ v1.1.x~v1.2.0 曾经有"没在商店买过就不许开"的门槛，那是"配置开着却完全不生效"
///       的直接原因（门槛要求值>0，而配置关会把值写成 0 ⇒ 死锁）。已删除。
///
/// ── 铁律（前几个 Mod 已定案）────────────────────────────────────
/// · 手写 csproj 没有 Godot 源码生成器 ⇒ 自定义 Node 子类的引擎回调不会被调用，
///   全部逻辑走 `SceneTree.Connect("process_frame", Callable.From(Action))` 信号通道。
/// · Initialize / OnAllModsLoaded / Shutdown 一律 try/catch 吞异常（抛出 = 整包回滚）。
/// · `TowerDefenseInGamePacketShow` 是被到处复用的卡类（卡池/卡槽/图鉴/商店），
///   对卡做任何操作前必须先用祖先链确认它在哪个界面。
/// </summary>
/// <summary>★ 2026-10-14 由原 Mod「MobileTapFeedbackEntry」整体并入本 Mod（逻辑未改动，只改类名与可见性）。</summary>
internal static class TapFeedbackPart
{
	// 赞助码注入器（SponsorQrInjector.cs 共享组件）用的包根缓存
	internal static string SponsorQrRoot = null;

	private const string P = "[TapFeedback] ";

	/// <summary>运行上下文（读取「Mod 配置」用）。</summary>
	private static XWModRuntimeContext _context;

	// ================================================================ 内部开关（默认全开）

	/// <summary>③ 关卡/章节/入口按压视觉反馈。</summary>
	internal static bool FixPressVisual = true;   // 由「Mod 配置」覆盖（默认开）

	/// <summary>④ 图鉴-工具页"自动拾取"开关框（阳光 / 金币 两个独立开关）。</summary>
	internal static bool FixAutoCollectSun = true;    // ★ v1.1.0：阳光自动拾取功能开关（由「Mod 配置」覆盖）
	internal static bool FixAutoCollectCoin = true;   // ★ v1.1.0：金币自动拾取功能开关

	/// <summary>诊断日志（默认关闭；排查时置 true 重新打包即可）。</summary>
	private static bool EnableLog;   // 由 Initialize 从「Mod 配置」读取（原为编译期常量）

	// ================================================================ 常量 / 字段

	/// <summary>
	/// ★ 按压态的**最短可见时长**（毫秒）。
	/// 手机上"触摸→鼠标"是同一帧里按下+抬起（还会紧跟一个 MouseExited），
	/// 若立刻还原，变暗那一帧根本不会被渲染 ⇒ 玩家看不到任何反馈（实测就是这个原因）。
	/// ⇒ 按下后至少保持这么久再还原。
	/// </summary>
	private const ulong PressVisibleMs = 160;

	private const string MetaItemHooked = "MTF_ItemHooked";
	private const string MetaAlmanacBox = "MTF_AlmanacBox";

	private static SceneTree _tree;
	private static Callable _tick;
	private static bool _started;
	private static int _diag;

	/// <summary>③ 已挂钩的关卡/章节入口（强引用，便于节点销毁后清理）。</summary>
	private static readonly List<DragMenuSelectItem> _hookedItems = new List<DragMenuSelectItem>();

	/// <summary>各条目"最早可以还原"的时间戳（Time.GetTicksMsec()）。</summary>
	private static readonly Dictionary<ulong, ulong> _dimUntil = new Dictionary<ulong, ulong>();

	/// <summary>被推迟的还原动作（到点后由 OnFrame 执行）。</summary>
	private static readonly Dictionary<ulong, Action> _pendingRestore = new Dictionary<ulong, Action>();

	/// <summary>③ 挂钩时记录的 graphics 原始 Modulate / Scale（抬起还原用）。</summary>
	private static readonly Dictionary<ulong, Color> _origModulate = new Dictionary<ulong, Color>();
	private static readonly Dictionary<ulong, Vector2> _origScale = new Dictionary<ulong, Vector2>();

	/// <summary>扫描节流（视觉反馈与图鉴注入不需要每帧扫）。</summary>
	private static int _frame;

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


	// ============================================================ 配置面板接线
	//
	// mod.json 声明了 fixPressVisual / fixAutoCollect 两个 bool 配置。
	// 值可能是 bool、string 或 JsonElement（宿主存的是 Dictionary<string, object>），
	// 所以统一用 Get<object> 再稳健转换；老宿主没有 context.Settings 时吞异常保持默认值。
	private static bool ReadSwitch(XWModRuntimeContext context, string key, bool fallback)
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

	/// <summary>按配置面板的值刷新两个开关（Initialize 与 Changed 都调它）。</summary>
	private static void ApplySwitches()
	{
		try
		{
			FixPressVisual = ReadSwitch(_context, "fixPressVisual", true);
			FixAutoCollectSun = ReadSwitch(_context, "fixAutoCollectSun", true);
			FixAutoCollectCoin = ReadSwitch(_context, "fixAutoCollectCoin", true);
			// ★ v1.2.0 起：配置 = 功能本体——配置开 ⇒ Feature=1；配置关 ⇒ Feature=0。
			//   v1.3.0：**去掉了"没购买不许开"的门槛**（旧门槛会让配置开着也永远不生效，
			//   详见 ApplyAutoCollect 注释），并额外每 30 帧与游戏 Feature 对齐一次
			//   （见 ReconcileAutoCollect）⇒ 配置页与商店购买状态不可能再不一致。
			ApplyAutoCollect("SunCollect", FixAutoCollectSun, sunOnly: true);
			ApplyAutoCollect("CoinCollect", FixAutoCollectCoin, sunOnly: false);
			Log("自动拾取已同步：阳光=" + FixAutoCollectSun + "，金币=" + FixAutoCollectCoin);
		}
		catch { }
	}


	/// <summary>面板保存后重新读开关（这两个开关是"用到时读字段"，刷新后下一帧即生效）。</summary>
	private static void OnSettingsChanged(System.Collections.Generic.IReadOnlyList<string> keys)
	{
		ApplySwitches();
		Log("配置已更新：" + string.Join("、", keys));
	}

internal static void Initialize(XWModRuntimeContext context)
	{
			try { SponsorQrRoot = context.PackageRoot; } catch { }
			_context = context;
			ApplySwitches();   // 从「Mod 配置」读取两个开关
			try
			{
				_context.Settings.Changed += OnSettingsChanged;
			}
			catch { }

			EnableLog = false;   // ★ 2026-10-14 用户要求：默认关闭所有日志（原：EnableLog = ReadLogSetting(context)）

		try
		{
			Log("初始化完成（关卡按压反馈 / 图鉴自动拾取开关）。");
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
			_started = true;
			Log("已挂载 process_frame（③按压反馈 / ④图鉴自动拾取开关）。");
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
			_started = false;
		}
	}

	// ================================================================ 每帧驱动

	private static void OnFrame()
	{
		try { SponsorQrInjector.Tick(Engine.GetMainLoop() as SceneTree, "点击反馈与自动拾取", SponsorQrRoot, "Resources/SponsorQR.png"); } catch { }
		try
		{
			if (_tree == null || !GodotObject.IsInstanceValid(_tree))
			{
				return;
			}
			_frame++;

			FlushPendingRestore();                     // ③ 到点的按压态还原
			if (_frame % 15 == 0)
			{
				ScanSelectItems();                     // ③ 关卡/章节按压反馈
				ScanAlmanac();                         // ④ 图鉴注入自动拾取框
			}
			if (_frame % 30 == 0)
			{
				ReconcileAutoCollect();                // ④-b 配置 ↔ 游戏 Feature（商店购买状态）对齐
			}
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

	/// <summary>③ 把到点的"按压态还原"执行掉（保证按压反馈至少可见 PressVisibleMs 毫秒）。</summary>
	private static void FlushPendingRestore()
	{
		if (_pendingRestore.Count == 0)
		{
			return;
		}
		try
		{
			ulong now = Time.GetTicksMsec();
			List<ulong> done = null;
			foreach (KeyValuePair<ulong, Action> kv in _pendingRestore)
			{
				bool ready = !_dimUntil.TryGetValue(kv.Key, out ulong until) || now >= until;
				if (ready)
				{
					(done ??= new List<ulong>()).Add(kv.Key);
					kv.Value?.Invoke();
				}
			}
			if (done != null)
			{
				foreach (ulong k in done)
				{
					_pendingRestore.Remove(k);
				}
			}
		}
		catch { }
	}

	// ================================================================ ③ 关卡/章节按压反馈

	/// <summary>
	/// 给所有 `DragMenuSelectItem`（关卡/章节共同基类）挂按压视觉：
	/// ButtonDown → graphics 压暗 0.62 + 缩放 ×0.94；ButtonUp / MouseExited → 还原。
	/// 缩放必须用"乘"不能用"设"——`InitLevel()` 会把某些入口 `graphics.Scale = 0.5`。
	/// </summary>
	private static void ScanSelectItems()
	{
		try
		{
			if (!FixPressVisual)
			{
				return;
			}
			// 清掉已销毁的条目，避免列表无限增长
			_hookedItems.RemoveAll(x => x == null || !GodotObject.IsInstanceValid(x));
			int before = _hookedItems.Count;

			var items = new List<DragMenuSelectItem>();
			// ★ is 判定（覆盖 level/chapter 两个子类）—— 不能按类名精确匹配
			CollectByType(_tree != null && GodotObject.IsInstanceValid(_tree) ? _tree.Root : null,
				0, items, 200);

			foreach (DragMenuSelectItem item in items)
			{
				if (item == null || !GodotObject.IsInstanceValid(item) || item.button == null)
				{
					continue;
				}
				if (item.HasMeta(MetaItemHooked))
				{
					continue;
				}
				Control g = item.graphics;
				if (g == null || !GodotObject.IsInstanceValid(g))
				{
					continue;
				}
				item.SetMeta(MetaItemHooked, true);
				_hookedItems.Add(item);
				ulong id = item.GetInstanceId();
				_origModulate[id] = g.Modulate;
				_origScale[id] = g.Scale;

				item.button.ButtonDown += () =>
				{
					try
					{
						if (!GodotObject.IsInstanceValid(g))
						{
							return;
						}
						_dimUntil[id] = Time.GetTicksMsec() + PressVisibleMs;
						_pendingRestore.Remove(id);
						Color m = _origModulate.TryGetValue(id, out Color om) ? om : g.Modulate;
						Vector2 s = _origScale.TryGetValue(id, out Vector2 os) ? os : g.Scale;
						g.Modulate = new Color(m.R * 0.62f, m.G * 0.62f, m.B * 0.62f, m.A);
						g.Scale = s * 0.94f;
					}
					catch { }
				};
				Action applyRestore = () =>
				{
					try
					{
						if (!GodotObject.IsInstanceValid(g))
						{
							return;
						}
						if (_origModulate.TryGetValue(id, out Color om))
						{
							g.Modulate = om;
						}
						if (_origScale.TryGetValue(id, out Vector2 os))
						{
							g.Scale = os;
						}
					}
					catch { }
				};
				Action restore = () =>
				{
					try
					{
						ulong now = Time.GetTicksMsec();
						ulong until = _dimUntil.TryGetValue(id, out ulong u) ? u : 0UL;
						if (now >= until)
						{
							applyRestore();
							_pendingRestore.Remove(id);
						}
						else
						{
							// 未达到最短可见时长 ⇒ 推迟到 OnFrame 里到点还原
							_pendingRestore[id] = applyRestore;
						}
					}
					catch { }
				};
				item.button.ButtonUp += restore;
				item.button.MouseExited += restore;
			}
			if (_hookedItems.Count != before)
			{
				Log("③按压反馈已挂钩条目数 = " + _hookedItems.Count);
			}
		}
		catch (Exception ex)
		{
			if (_diag < 104)
			{
				_diag = 104;
				Log("关卡按压反馈异常（本条只报一次）：" + ex.Message);
			}
		}
	}

	// ================================================================ ④ 图鉴-工具页 自动拾取开关

	/// <summary>
	/// 找到图鉴（Almanac）就往**工具页（PropLayer）**注入「自动拾取」框。
	/// 图鉴的页签切换就是 `propLayer.Visible` 切换 ⇒ 挂在它下面，可见性天然跟工具页一致。
	/// 每次打开图鉴都会新建对话框节点 ⇒ 用 meta 防重复注入，节点关了标记随节点一起没了。
	/// </summary>
	private static void ScanAlmanac()
	{
		try
		{
			return;   // ★ v1.2.0：图鉴开关框整个去掉（用户要求：配置页直接控制，图鉴框冗余）
			var list = new List<Almanac>();
			CollectByType(_tree != null && GodotObject.IsInstanceValid(_tree) ? _tree.Root : null,
				0, list, 20);
			foreach (Almanac al in list)
			{
				if (al == null || !GodotObject.IsInstanceValid(al) || al.HasMeta(MetaAlmanacBox))
				{
					continue;
				}
				if (al.propLayer == null || !GodotObject.IsInstanceValid(al.propLayer))
				{
					continue;
				}
				al.SetMeta(MetaAlmanacBox, true);
				BuildAutoCollectBox(al);
				Log("已在图鉴-工具页注入「自动拾取」开关框。");
			}
		}
		catch (Exception ex)
		{
			if (_diag < 105)
			{
				_diag = 105;
				Log("图鉴注入异常（本条只报一次）：" + ex.Message);
			}
		}
	}

	/// <summary>构建「自动拾取」框：标题 + 阳光开关 + 金币开关（两个独立开关）。</summary>
	private static void BuildAutoCollectBox(Almanac al)
	{
		PanelContainer panel = new PanelContainer();
		panel.Name = "MTFAutoCollectBox";
		// 右上角（距右 24 / 距顶 24）。
		panel.AnchorLeft = 1f;
		panel.AnchorTop = 0f;
		panel.AnchorRight = 1f;
		panel.AnchorBottom = 0f;
		panel.OffsetLeft = -304f;
		panel.OffsetTop = 24f;
		panel.OffsetRight = -24f;
		panel.OffsetBottom = 184f;
		panel.GrowHorizontal = Control.GrowDirection.Begin;
		panel.GrowVertical = Control.GrowDirection.End;

		VBoxContainer box = new VBoxContainer();
		box.Name = "Box";
		box.AddThemeConstantOverride("separation", 6);
		panel.AddChild(box);

		Label title = new Label();
		title.Text = "自动拾取";
		box.AddChild(title);

		if (FixAutoCollectSun)
		{
			box.AddChild(MakeCollectToggle("阳光", "SunCollect", sunOnly: true));
		}
		if (FixAutoCollectCoin)
		{
			box.AddChild(MakeCollectToggle("金币", "CoinCollect", sunOnly: false));
		}

		al.propLayer.AddChild(panel);
	}

	// ================================================================ ④ 购买状态与偏好
	//
	// ── 源码事实 ────────────────────────────────────────────────────
	// · `SunCollect`（阳光自动拾取，商店 1000 金币）/ `CoinCollect`（金币自动拾取，2000 金币）
	//   是 **WWM 商店商品**（Asset/Config/Shop/WWM/WWMShop.json，"Type":"Item"，一次性）。
	//   购买走 `ShopItem.Sale()` → `GlobalFeatureManager.AddValue(key, AddNum=1)`
	//   → `GameSaveManager.SetFeatureValue(key, 1)`；商店"能不能买"的判据是
	//   `GetValue(key) ∈ [openMinNum=0, openMaxNum=1)` ⇒ **值 0 = 可购买**。
	// · `FeatureInit.json` 里两者默认 **0.0**（未购买）。
	// · `Feature` 分类挂在 `config.saveDictionary[userCurrent]` 下 ⇒ **按存档位独立**，
	//   多存档互不影响。
	// · 阳光的 `autoCollect` 只在**生成后的下一个物理帧**从 Feature 值求值一次
	//   （`OnRefreshPhysicsFrame`，一次性回调）⇒ 改场上实例字段会被这次评估覆盖，
	//   **改 Feature 值是唯一可靠路径**。
	//
	// ── 语义设计（★ v1.3.0 定稿）────────────────────────────────────
	// 游戏里「已购买」与「已开启」是同一个键 ⇒ Mod **不再自作聪明**地去区分它们，
	// 两个配置开关直接就是那个值：开 ⇒ 1，关 ⇒ 0；每 30 帧对齐一次
	// （见 ReconcileAutoCollect），并认"商店购买"这个反向变更。
	// 下面这套 `RecPath` 记录只是**历史记录 / 诊断**（旧包兼容），不再参与任何门槛判断。

	/// <summary>本 Mod 的偏好文件（user:// 根下，按存档位分档）。</summary>
	private const string RecPath = "user://mobiletapfeedback.json";

	/// <summary>拆分前旧包的偏好文件（只读兼容）。</summary>
	private const string LegacyRecPath = "user://mobileuxfixes.json";

	/// <summary>当前存档位标识（游戏存档就是按它分的）。</summary>
	private static string CurrentSlot()
	{
		try
		{
			return GameSaveManager.Instance.GetUserCurrent() ?? "";
		}
		catch
		{
			return "";
		}
	}

	private static Godot.Collections.Dictionary LoadRec(string path)
	{
		try
		{
			if (Godot.FileAccess.FileExists(path))
			{
				using Godot.FileAccess f = Godot.FileAccess.Open(path, Godot.FileAccess.ModeFlags.Read);
				if (f != null)
				{
					Variant v = Json.ParseString(f.GetAsText());
					if (v.VariantType == Variant.Type.Dictionary)
					{
						return v.AsGodotDictionary();
					}
				}
			}
		}
		catch { }
		return new Godot.Collections.Dictionary();
	}

	private static void SaveRec(Godot.Collections.Dictionary root)
	{
		try
		{
			using Godot.FileAccess f = Godot.FileAccess.Open(RecPath, Godot.FileAccess.ModeFlags.Write);
			if (f != null)
			{
				f.StoreString(Json.Stringify(root, "\t"));
			}
		}
		catch { }
	}

	/// <summary>从指定偏好文件的当前存档位里取键值。</summary>
	private static bool RecGetFrom(string path, string key)
	{
		try
		{
			Godot.Collections.Dictionary root = LoadRec(path);
			string slot = CurrentSlot();
			if (root.ContainsKey(slot))
			{
				Godot.Collections.Dictionary d = root[slot].AsGodotDictionary();
				if (d.ContainsKey(key))
				{
					return d[key].AsBool();
				}
			}
		}
		catch { }
		return false;
	}

	/// <summary>本 Mod（或拆分前旧包）记录的「这个存档曾拥有该拾取商品」。</summary>
	private static bool RecGet(string key)
	{
		return RecGetFrom(RecPath, key) || RecGetFrom(LegacyRecPath, key);
	}

	private static void RecSet(string key, bool val)
	{
		try
		{
			Godot.Collections.Dictionary root = LoadRec(RecPath);
			string slot = CurrentSlot();
			Godot.Collections.Dictionary d = root.ContainsKey(slot)
				? root[slot].AsGodotDictionary()
				: new Godot.Collections.Dictionary();
			d[key] = val;
			root[slot] = d;
			SaveRec(root);
		}
		catch { }
	}

	/// <summary>
	/// 这个存档是否拥有该拾取商品（游戏购买记录 **或** 本 Mod 记录）。
	/// ⚠️ v1.3.0 起**不再用于任何开关门槛**（配置就是总开关）——
	/// 只被已停用的「图鉴-工具页开关框」用到（`ScanAlmanac` 现在直接 return），保留作历史实现。
	/// </summary>
	private static bool IsOwned(string key)
	{
		return GetFeature(key) > 0 || RecGet(key);
	}

	/// <summary>单个拾取开关：未购买置灰；切换写 Feature 值（游戏自己的开关语义）。</summary>
	private static CheckBox MakeCollectToggle(string label, string key, bool sunOnly)
	{
		bool owned = IsOwned(key);
		CheckBox cb = new CheckBox();
		cb.Text = owned ? label : label + "（未购买）";
		cb.ButtonPressed = owned && GetFeature(key) > 0;
		cb.Disabled = !owned;
		if (!owned)
		{
			cb.TooltipText = "在商店购买后可用";
		}
		cb.Pressed += () =>
		{
			try
			{
				ApplyAutoCollect(key, cb.ButtonPressed, sunOnly);
			}
			catch (Exception ex)
			{
				try { GD.PrintErr(P + "开关异常：" + ex.Message); } catch { }
			}
		};
		return cb;
	}

	/// <summary>★ v1.1.1：关闭某项自动拾取（配置开关关掉时调用）：Feature=0 + 同步场上实例。</summary>
	private static void DisableAutoCollect(string key, bool sunOnly)
	{
		try
		{
			GameSaveManager.Instance.SetFeatureValue(key, 0);
			SyncLiveAutoCollect(key, false, sunOnly);
			Log("已关闭自动拾取：" + key);
		}
		catch { }
	}

	/// <summary>
	/// 写游戏自己的拾取特性值（= 配置开关的唯一真值）。
	///
	/// ── ★ v1.3.0：**删掉"没买过就不许开"的门槛** ────────────────────────
	/// 旧版本（v1.1.x / v1.2.0）要求 `Feature > 0`（= 商店里已购买）才允许写 1，
	/// 而"配置关"会把值写成 0 ⇒ 门槛从此再也不可能满足 ⇒ **配置开着也永远不生效**：
	///   · 用户实测存档：SunCollect = 0、CoinCollect = 0（本目录下用 hexdump 核对过）
	///   · 而配置页里「自动拾取（阳光）」= 开
	///   · 商店里该商品显示**可购买**（ShopItem.Refresh 判据 `0 <= v < openMaxNum(=1)`）
	/// ⇒ 三处对不上，用户报的「配置和实际对不上 / 商店购买状态和配置不一样」就是这个。
	///
	/// 游戏里"已购买"和"已开启"本来就是同一个值（`ShopItem.Sale()` →
	/// `GlobalFeatureManager.AddValue(saveKey, 1)` → `SetFeatureValue`），
	/// 所以**配置直接写这个值**就是唯一自洽的语义：
	///   开 ⇒ 1（自动拾取生效 + 商店里显示已购买/售罄）
	///   关 ⇒ 0（不自动拾取 + 商店里重新可购买）
	/// </summary>
	private static void ApplyAutoCollect(string key, bool on, bool sunOnly)
	{
		try
		{
			int before = GetFeature(key);
			GameSaveManager.Instance.SetFeatureValue(key, on ? 1 : 0);
			if (GetFeature(key) != before)
			{
				GameSaveManager.Instance.ScheduleSave();   // 真变了才落盘（没存档位时写入是 no-op）
			}
			RecSet(key, true);                  // 本 Mod 的历史记录「曾经拥有」（旧包兼容 / 诊断）
			SyncLiveAutoCollect(key, on, sunOnly);
		}
		catch (Exception ex)
		{
			try { GD.PrintErr(P + "切换自动拾取异常：" + ex.Message); } catch { }
		}
	}

	/// <summary>
	/// 把场上**已生成**的阳光/金币实例的 `autoCollect` 同步成 `on`
	/// （新生成的实例会自己在下一个物理帧读 Feature 值，无需处理）。
	///
	/// ★ v1.3.0：改用 `is T` 收集（原来的按类名精确匹配**漏掉全部子类**：
	///   场上普通阳光的运行时类型是 `TowerDefenseSun`（`TowerDefenseSunBase` 的子类）、
	///   金币有 `TowerDefenseGoldShard` / `TowerDefenseLuckyBag` 等子类 ⇒ 一个都匹配不到）。
	/// </summary>
	private static void SyncLiveAutoCollect(string key, bool on, bool sunOnly)
	{
		try
		{
			Node root = (_tree != null && GodotObject.IsInstanceValid(_tree)) ? _tree.Root : null;
			if (root == null)
			{
				return;
			}
			if (sunOnly)
			{
				var suns = new List<TowerDefenseSunBase>();
				CollectByType(root, 0, suns, 500);
				foreach (TowerDefenseSunBase s in suns)
				{
					try
					{
						if (s != null && GodotObject.IsInstanceValid(s))
						{
							s.autoCollect = on;
						}
					}
					catch { }
				}
			}
			else
			{
				var coins = new List<TowerDefenseCoinBase>();
				CollectByType(root, 0, coins, 500);
				// 与游戏原判据保持一致：场上有吸金磁时金币不自动收
				bool goldMagnet = _tree.GetNodeCountInGroup("GoldMagnet") > 0;
				foreach (TowerDefenseCoinBase c in coins)
				{
					try
					{
						if (c != null && GodotObject.IsInstanceValid(c))
						{
							c.autoCollect = on && !goldMagnet;
						}
					}
					catch { }
				}
			}
		}
		catch { }
	}

	// ================================================================ ④-b 配置 ↔ 游戏 Feature 对齐
	//
	// 游戏里「是否购买自动拾取」与「是否生效」是**同一个值**（源码实证）：
	//   · 实战：TowerDefenseSunBase.OnRefreshPhysicsFrame
	//              → autoCollect = GetFeatureValue("SunCollect") != 0
	//           TowerDefenseCoinBase.OnInitPhysicsFrame
	//              → autoCollect = GetFeatureValue("CoinCollect") > 0 && 场上无 GoldMagnet
	//           （QX / Magic / Jalapeno 三种阳光更是每次自己读 Feature）
	//   · 商店：ShopItem.Refresh → GlobalFeatureManager.GetValue(saveKey)
	//              ∈ [openMinNum, openMaxNum)；WWMShop.json 里两项都是 0 / 1
	//           ⇒ 值 = 0 ⇒ 显示**可购买**；值 ≥ 1 ⇒ 显示已购买（售罄）
	//   · 购买：ShopItem.Sale → GlobalFeatureManager.AddValue(saveKey, 1) → SetFeatureValue
	//
	// ⇒ 只要保证「配置开 ⇔ Feature=1、配置关 ⇔ Feature=0」，配置页 / 实战 / 商店三处永远一致。
	//   ★ 别依赖宿主 `Settings.Changed` 做状态同步（交接文档血泪教训：Changed 有三条件，
	//     任一不满足就静默不发）⇒ 每 30 帧轮询对齐（Get 是内存字典读，开销可忽略）。
	//   ★ **反向也要认**：若游戏那边把值变成了 >0（玩家在商店里买了），要**采纳**它
	//     （把配置改成"开"）。否则我们的下一轮对齐会立刻把玩家花金币买来的东西抹回 0
	//     ⇒ 商店/配置又对不上，而且金币白花。

	//   ★ v1.3.0 语义（三重保证）：
	//     1) **配置为准**：配置开 ⇒ 写 1；配置关 ⇒ 写 0（写的是**当前存档位**）；
	//     2) **唯一例外**＝本会话＋本存档位内**观察到 0→≥1 的跃迁**（＝玩家刚在商店买了）
	//        ⇒ 采纳它（把配置改成"开"）。绝不抹掉刚买的东西；
	//     3) 每 30 帧**重新读一次配置**再对齐（不依赖宿主 `Changed` 是否送达 —— 见 〇-q2 教训）。
	//
	//   ⚠️ 刻意不做的两件事（否则就是自己制造新的"对不上"）：
	//     · 不拿"当前值 > 0"直接当"玩家刚买"：`Feature` 是**按存档位**存的，而配置是全局的，
	//       那样切换存档位会把别位的购买状态反向污染成本位的"配置=开"；
	//     · 采纳写配置失败时**不会**把游戏值压回 0（那会抹掉玩家花金币买的东西）：
	//       只挂起重试，重试期间不做"配置关"的强制写。

	/// <summary>本会话·本存档位内上一次观察到的值（-1 = 未知 ⇒ 不当成"刚购买"）。</summary>
	private static int _seenSun = -1;
	private static int _seenCoin = -1;
	private static string _seenSlot;

	/// <summary>采纳写配置失败后的挂起重试标志。</summary>
	private static bool _adoptPendingSun;
	private static bool _adoptPendingCoin;

	/// <summary>采纳日志只打前几条。</summary>
	private static int _adoptLogged;

	private static void ReconcileAutoCollect()
	{
		try
		{
			string slot = CurrentSlot();
			if (slot != _seenSlot)
			{
				// 换存档位（或首次）⇒ 观察值作废，避免跨存档位互相污染
				_seenSlot = slot;
				_seenSun = -1;
				_seenCoin = -1;
				_adoptPendingSun = false;
				_adoptPendingCoin = false;
			}
			if (string.IsNullOrEmpty(slot))
			{
				// 还没建/没选存档位：`SetCategoryValue` 会静默 no-op（`EnsureUser()==""`）
				// ⇒ 写也没用，直接跳过（省掉每 30 帧的写盘与全树扫描）
				return;
			}
			ReconcileOne("SunCollect", "fixAutoCollectSun", sunOnly: true);
			ReconcileOne("CoinCollect", "fixAutoCollectCoin", sunOnly: false);
		}
		catch { }
	}

	private static void ReconcileOne(string key, string settingKey, bool sunOnly)
	{
		try
		{
			// ★ 每次都重读配置：宿主 `Changed` 万一没送达也能自愈
			bool want = ReadSwitch(_context, settingKey, true);
			if (sunOnly) { FixAutoCollectSun = want; } else { FixAutoCollectCoin = want; }

			int cur = GetFeature(key);
			int seen = sunOnly ? _seenSun : _seenCoin;
			bool pending = sunOnly ? _adoptPendingSun : _adoptPendingCoin;
			bool curOn = cur > 0;

			if (curOn == want)
			{
				MarkSeen(sunOnly, cur, adoptPending: false);
				return;                     // 已一致
			}
			if (curOn && !want)
			{
				if (pending || seen == 0)
				{
					// 本会话观察到它刚从 0 变成 ≥1 ⇒ 玩家刚在商店买了 ⇒ 采纳
					bool saved = TryAdoptConfig(key, settingKey, sunOnly);
					MarkSeen(sunOnly, cur, adoptPending: !saved);
					return;                 // ★ 无论如何都不写 0
				}
				ApplyAutoCollect(key, false, sunOnly);   // 历史值/未知 ⇒ 配置为准
				MarkSeen(sunOnly, 0, adoptPending: false);
				return;
			}
			ApplyAutoCollect(key, true, sunOnly);        // 配置为准 ⇒ 写 1
			MarkSeen(sunOnly, 1, adoptPending: false);
		}
		catch { }
	}

	private static void MarkSeen(bool sunOnly, int value, bool adoptPending)
	{
		if (sunOnly)
		{
			_seenSun = value;
			_adoptPendingSun = adoptPending;
		}
		else
		{
			_seenCoin = value;
			_adoptPendingCoin = adoptPending;
		}
	}

	/// <summary>
	/// 把配置里该项改成"开"（采纳游戏侧的值）。**只写配置，不碰游戏值**。
	/// 成功 ⇒ 宿主发 `Changed` ⇒ ApplySwitches 重读并把场上实例对齐。
	/// </summary>
	private static bool TryAdoptConfig(string key, string settingKey, bool sunOnly)
	{
		bool saved = false;
		try
		{
			if (_context != null)
			{
				var changes = new Dictionary<string, object> { [settingKey] = true };
				saved = _context.Settings.TrySave(changes, out _);
			}
		}
		catch { }
		if (saved)
		{
			if (sunOnly) { FixAutoCollectSun = true; } else { FixAutoCollectCoin = true; }
			SyncLiveAutoCollect(key, true, sunOnly);     // 游戏值本来就是 1 ⇒ 只是把字段对齐
			if (_adoptLogged < 4)
			{
				_adoptLogged++;
				Log("检测到商店刚购买了自动拾取 ⇒ 配置已同步为「开」：" + settingKey);
			}
		}
		else if (_adoptLogged < 8)
		{
			_adoptLogged++;
			Log("采纳商店购买状态失败（配置写盘被拒）⇒ 挂起重试，期间不会抹掉已购买状态：" + settingKey);
		}
		return saved;
	}

	private static int GetFeature(string key)
	{
		try
		{
			return GameSaveManager.Instance.GetFeatureValue(key);
		}
		catch
		{
			return 0;
		}
	}

	// ================================================================ 工具

	/// <summary>
	/// 按**类型**递归收集节点（深度优先，最多 max 个）。
	///
	/// ★ `is T` 判定天然覆盖子类 —— 不能按类名精确匹配：
	///   关卡/章节入口的实际类型是 `DragMenuSelectItemlevel` / `DragMenuSelectItemChapter`
	///   （都是 `DragMenuSelectItem` 的**子类**）⇒ 精确匹配永远匹配不上 ⇒ 按压反馈整个不生效（实测）。
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

	/// <summary>按类名递归收集节点（深度优先，最多 max 个）。</summary>
	private static void CollectByClassName(Node node, int depth, string className,
		List<Node> outList, int max)
	{
		try
		{
			if (node == null || !GodotObject.IsInstanceValid(node) || outList.Count >= max || depth > 40)
			{
				return;
			}
			if (node.GetType().Name == className)
			{
				outList.Add(node);
			}
			int n = node.GetChildCount();
			for (int i = 0; i < n; i++)
			{
				CollectByClassName(node.GetChild(i), depth + 1, className, outList, max);
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
