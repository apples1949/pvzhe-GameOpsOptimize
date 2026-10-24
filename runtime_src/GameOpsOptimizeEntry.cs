using Godot;
using System;
using PVZHE.ModEditor.ModSystem;

/// <summary>
/// ★ 2026-10-23 改名 + 瘦身：本 Mod 由「游戏操作场景优化」（GameOpsOptimize）
///   改为 **「自动拾取开关与滚动条设置」**，并**删除**了「点空处取消拿卡/铲子」
///   （原 fixPickCancel）与「点击反馈」（原 fixPressVisual）两项功能及其全部代码。
///
/// 现在只剩两块功能，各自在独立文件里：
///   · 滚动条设置   → <see cref="ScrollTunePart"/>（ScrollTunePart.cs）
///   · 自动拾取开关 → <see cref="AutoCollectPart"/>（AutoCollectPart.cs）
///
/// 合并方式（**逻辑未改**，只做接线）：两套逻辑各自挂自己的 `SceneTree.process_frame`
/// 回调 ⇒ 互不干扰；本类只负责把宿主的三个生命周期回调**转发**给它们，任一抛出都吞掉
/// （宿主规则：Initialize/OnAllModsLoaded/Shutdown 抛出 = 整包回滚）。
///
/// 铁律（沿用前几个 Mod 的定案）：
///   · 手写 csproj 没有 Godot 源码生成器 ⇒ 自定义 Node 子类的引擎回调不会被调用，
///     全部逻辑走 `SceneTree.Connect("process_frame", Callable.From(Action))`。
///   · 本类**不直接碰游戏对象**，避免与两个 Part 的时序互相影响。
/// </summary>
public sealed class GameOpsOptimizeEntry : IXWModRuntimeEntry
{
	private const string P = "[GameOpsOptimize] ";

	public void Initialize(XWModRuntimeContext context)
	{
		try { AutoCollectPart.Initialize(context); }
		catch (Exception ex) { try { GD.PrintErr(P + "自动拾取部分 Initialize 异常（已吞）：" + ex.Message); } catch { } }
		try { ScrollTunePart.Initialize(context); }
		catch (Exception ex) { try { GD.PrintErr(P + "滚动条部分 Initialize 异常（已吞）：" + ex.Message); } catch { } }
	}

	public void OnAllModsLoaded()
	{
		try { AutoCollectPart.OnAllModsLoaded(); }
		catch (Exception ex) { try { GD.PrintErr(P + "自动拾取部分 OnAllModsLoaded 异常（已吞）：" + ex.Message); } catch { } }
		try { ScrollTunePart.OnAllModsLoaded(); }
		catch (Exception ex) { try { GD.PrintErr(P + "滚动条部分 OnAllModsLoaded 异常（已吞）：" + ex.Message); } catch { } }
	}

	public void Shutdown()
	{
		// 先关"滚动条"再关"自动拾取"：两者都会还原自己动过的节点，顺序无强依赖，但保持稳定顺序。
		try { ScrollTunePart.Shutdown(); }
		catch (Exception ex) { try { GD.PrintErr(P + "滚动条部分 Shutdown 异常（已吞）：" + ex.Message); } catch { } }
		try { AutoCollectPart.Shutdown(); }
		catch (Exception ex) { try { GD.PrintErr(P + "自动拾取部分 Shutdown 异常（已吞）：" + ex.Message); } catch { } }
	}
}
