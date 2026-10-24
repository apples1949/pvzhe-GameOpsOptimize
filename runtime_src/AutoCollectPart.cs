using System;
using System.Collections.Generic;
using Godot;
using PVZHE.ModEditor.ModSystem;

/// <summary>
/// 「自动拾取开关」Part —— 本 Mod 现在只剩两块功能之一（另一块见 <see cref="ScrollTunePart"/>）。
///
/// ★ 2026-10-23（用户要求）：本 Mod 由「游戏操作场景优化」改名为「自动拾取开关与滚动条设置」，
///   并**删除**了「点空处取消拿卡/铲子」与「点击反馈」两项功能及其全部代码
///   （原 MobileUxFixesPart.cs / TapFeedbackPart.cs 已按此重写）。
///
/// ── 功能：无法关闭自动拾取阳光 / 金币 ─────────────────────────────
///   根因（源码实证，两个独立存档特性键）：
///       阳光 `TowerDefenseSunBase`:  autoCollect = GetFeatureValue("SunCollect") != 0
///       金币 `TowerDefenseCoinBase`: autoCollect = GetFeatureValue("CoinCollect") > 0 && 场上无吸金磁
///   游戏没给玩家任何入口去改（只有商店"买一次=永久开"）。
///   ★ v1.3.0 定稿语义：**Mod 配置页的两个开关 = 游戏那个值本身**。
///       开 ⇒ 写 1（自动拾取生效，且商店里该商品显示"已购买"）
///       关 ⇒ 写 0（不自动拾取，且商店里重新显示"可购买"）
///     每 30 帧双向对齐一次 ⇒ 配置页 / 实战 / 商店三处永远一致
///     （玩家在商店里买了 ⇒ 配置自动变"开"，绝不抹掉他花钱买来的状态）。
///
/// ── 铁律（前几个 Mod 已定案）────────────────────────────────────
/// · 手写 csproj 没有 Godot 源码生成器 ⇒ 自定义 Node 子类的引擎回调不会被调用，
///   全部逻辑走 `SceneTree.Connect("process_frame", Callable.From(Action))` 信号通道。
/// · Initialize / OnAllModsLoaded / Shutdown 一律 try/catch 吞异常（抛出 = 整包回滚）。
/// </summary>
internal static class AutoCollectPart
{
	/// <summary>赞助码注入器（SponsorQrInjector.cs 共享组件）用的包根缓存。</summary>
	internal static string SponsorQrRoot = null;

	private const string P = "[AutoCollect] ";

	/// <summary>运行上下文（读取「Mod 配置」用）。</summary>
	private static XWModRuntimeContext _context;

	/// <summary>★ v1.1.0：阳光自动拾取功能开关（由「Mod 配置」覆盖）。</summary>
	internal static bool FixAutoCollectSun = true;
	/// <summary>★ v1.1.0：金币自动拾取功能开关（由「Mod 配置」覆盖）。</summary>
	internal static bool FixAutoCollectCoin = true;

	/// <summary>诊断日志（默认关闭；排查时把 mod.json 的 enableLog 打开）。</summary>
	private static bool EnableLog;

	private static SceneTree _tree;
	private static Callable _tick;
	private static bool _started;
	private static int _diag;

	// ============================================================ 配置读取

	// mod.json 声明了 fixAutoCollectSun / fixAutoCollectCoin 两个 bool 配置。
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
			FixAutoCollectSun = ReadSwitch(_context, "fixAutoCollectSun", true);
			FixAutoCollectCoin = ReadSwitch(_context, "fixAutoCollectCoin", true);
			// ★ v1.2.0 起：配置 = 功能本体——配置开 ⇒ Feature=1；配置关 ⇒ Feature=0。
			//   v1.3.0：**去掉了"没购买不许开"的门槛**，并额外每 30 帧与游戏 Feature 对齐一次
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

	// ============================================================ 生命周期

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

		EnableLog = false;   // ★ 2026-10-14 用户要求：默认关闭所有日志

		try
		{
			Log("初始化完成（自动拾取开关）。");
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
			Log("已挂载 process_frame（自动拾取开关）。");
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
		try { SponsorQrInjector.Tick(Engine.GetMainLoop() as SceneTree, "自动拾取开关与滚动条设置", SponsorQrRoot, "Resources/SponsorQR.png"); } catch { }
		try
		{
			if (_tree == null || !GodotObject.IsInstanceValid(_tree))
			{
				return;
			}
			ulong f = Engine.GetProcessFrames();
			if (f % 30UL == 0UL)
			{
				ReconcileAutoCollect();   // ④-b 配置 ↔ 游戏 Feature（商店购买状态）对齐
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

	// ================================================================ 自动拾取：写游戏自己的特性值
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
	// ── ★ v1.3.0：**删掉"没买过就不许开"的门槛** ────────────────────────
	// 旧版本（v1.1.x / v1.2.0）要求 `Feature > 0`（= 商店里已购买）才允许写 1，
	// 而"配置关"会把值写成 0 ⇒ 门槛从此再也不可能满足 ⇒ **配置开着也永远不生效**。
	// 游戏里"已购买"和"已开启"本来就是同一个值，所以**配置直接写这个值**就是唯一自洽的语义。

	/// <summary>本 Mod 的偏好文件（user:// 根下，按存档位分档）。</summary>
	private const string RecPath = "user://mobiletapfeedback.json";

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

	/// <summary>写游戏自己的拾取特性值（= 配置开关的唯一真值）。</summary>
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

	// ================================================================ 配置 ↔ 游戏 Feature 对齐
	//
	// 只要保证「配置开 ⇔ Feature=1、配置关 ⇔ Feature=0」，配置页 / 实战 / 商店三处永远一致。
	// ★ 别依赖宿主 `Settings.Changed` 做状态同步（Changed 有三条件，任一不满足就静默不发）
	//   ⇒ 每 30 帧轮询对齐（Get 是内存字典读，开销可忽略）。
	// ★ **反向也要认**：若游戏那边把值变成了 >0（玩家在商店里买了），要**采纳**它。
	//   ⚠️ 刻意不做：不拿"当前值 > 0"直接当"玩家刚买"（Feature 按存档位存、配置是全局的，
	//   那样切存档位会反向污染）；采纳写配置失败时也不会把游戏值压回 0。

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
				// 还没建/没选存档位：`SetCategoryValue` 会静默 no-op ⇒ 直接跳过
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
	/// ★ `is T` 判定天然覆盖子类 —— 不能按类名精确匹配。
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
