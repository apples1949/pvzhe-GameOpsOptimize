using System;
using Godot;
using PVZHE.ModEditor.ModSystem;

/// <summary>
/// 「滚动条设置」Part —— 本 Mod 现在只剩两块功能之一（另一块见 <see cref="AutoCollectPart"/>）。
///
/// ★ 2026-10-23（用户要求）：本 Mod 由「游戏操作场景优化」改名为「自动拾取开关与滚动条设置」，
///   并**删除**了「点空处取消拿卡/铲子」（原 fixPickCancel）与「点击反馈」（原 fixPressVisual）
///   两项功能及其全部代码 —— 原 MobileUxFixesPart.cs / TapFeedbackPart.cs 已按此重写：
///     · 滚动条部分 → 本文件（原 MobileUxFixesPart.cs 的滚动条的一半，逻辑原样搬来）
///     · 自动拾取部分 → AutoCollectPart.cs
///
/// 滚动条能力（6 项配置，全部可在「Mod 配置」里改，改完立即生效、无需重启）：
///   · 总开关 / 粗细 / 轨道颜色 / 轨道不透明度 / 滑块颜色 / 滑块粗细
///
/// 实现口径（务必保留）：
///   · **零轮询**：启动扫一次整树 + 只接 `SceneTree.NodeAdded / NodeRemoved`；
///   · 粗细写 `custom_minimum_size`（Godot 4 已无 `scroll_width` 常量，覆写无效，留作无害兜底）；
///   · 颜色改**克隆后的样式盒**（绝不污染主题里的共享实例）；
///   · 滑块比轨道窄 ⇒ 给滑块样式盒加内容内缩（居中收窄）。
///
/// 铁律（前几个 Mod 已定案）：
///   · 手写 csproj 没有 Godot 源码生成器 ⇒ 自定义 Node 子类的引擎回调不会被调用，
///     全部逻辑走 `SceneTree.Connect("process_frame", Callable.From(Action))` 信号通道。
///   · Initialize / OnAllModsLoaded / Shutdown 一律 try/catch 吞异常（抛出 = 整包回滚）。
/// </summary>
internal static class ScrollTunePart
{
	/// <summary>赞助码注入器（SponsorQrInjector.cs 共享组件）用的包根缓存。</summary>
	internal static string SponsorQrRoot = null;

	private const string P = "[ScrollTune] ";

	/// <summary>诊断日志（默认关闭；排查时把 mod.json 的 enableLog 打开）。</summary>
	private static bool EnableLog;

	private static SceneTree _tree;
	private static Callable _tick;
	private static bool _started;
	private static int _diag;

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

	/// <summary>★ 设置变更后要求"重刷已存在的滚动条"（否则要重启才能看到新宽度/颜色）。</summary>
	private static bool _scrollDirty;

	// ============================================================ 配置读取

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

	/// <summary>读布尔配置（兼容 string / JsonElement）。</summary>
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

	/// <summary>把 6 项滚动条配置读进静态字段。</summary>
	private static void ReadScrollSettings(XWModRuntimeContext context)
	{
		try
		{
			_scrollOn = ReadBoolSetting(context, KScrollOn, true);
			_scrollW = Math.Clamp(ReadIntSetting(context, KScrollW, 24), 8, 48);
			int alpha = Math.Clamp(ReadIntSetting(context, KScrollAlpha, 8), 0, 100);
			Color tc = ReadColorSetting(context, KScrollColor, new Color(0f, 0f, 0f, 1f));
			tc.A = alpha / 100f;
			_trackColor = tc;
			_grabColor = ReadColorSetting(context, KGrabColor, new Color(0.16f, 0.42f, 0.26f, 0.55f));
			_grabW = Math.Clamp(ReadIntSetting(context, KGrabW, 0), 0, 48);
			if (_grabW > _scrollW) { _grabW = _scrollW; }
		}
		catch { }
	}

	// ============================================================ 滚动条接管

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

	// ============================================================ 生命周期

	private static XWModRuntimeContext _ctxRef;
	private static bool _settingsSubscribed;

	private static void OnSettingsChanged(System.Collections.Generic.IReadOnlyList<string> keys)
	{
		ReadScrollSettings(_ctxRef);
		_scrollDirty = true;   // 由 OnFrame 下一帧执行重刷
	}

	internal static void Initialize(XWModRuntimeContext context)
	{
		try { SponsorQrRoot = context.PackageRoot; } catch { }
		EnableLog = false;   // ★ 2026-10-14 用户要求：默认关闭所有日志

		_ctxRef = context;
		ReadScrollSettings(context);   // 从「Mod 配置」读取 6 项滚动条配置
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
			Log("初始化完成（滚动条设置）。");
		}
		catch (System.Exception ex)
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
				Log("拿不到 SceneTree，滚动条设置不生效。");
				return;
			}
			_tick = Callable.From(new System.Action(OnFrame));
			_tree.Connect("process_frame", _tick);
			_started = true;
			Log("已挂载 process_frame（滚动条设置）。");
		}
		catch (System.Exception ex)
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
				try { _tree.NodeAdded -= OnAnyNodeAdded; } catch { }
				try { _tree.NodeRemoved -= OnAnyNodeRemoved; } catch { }
				_tree.Disconnect("process_frame", _tick);
			}
		}
		catch (System.Exception ex)
		{
			try { GD.PrintErr(P + "Shutdown 异常（已吞）：" + ex.Message); } catch { }
		}
		finally
		{
			_started = false;
			_scrollHooked = false;
		}
	}

	// ================================================================ 每帧驱动

	private static void OnFrame()
	{
		try
		{
			if (_tree == null || !GodotObject.IsInstanceValid(_tree))
			{
				return;
			}
			// ★ 滚动条优化：首帧挂钩信号 + 全树扫一次；之后纯事件驱动、零轮询
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
		catch (System.Exception ex)
		{
			if (_diag < 100)
			{
				_diag = 100;
				Log("每帧驱动异常（本条只报一次）：" + ex.Message);
			}
		}
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
