using Godot;
using System;
using PVZHE.ModEditor.ModSystem;

/// <summary>
/// ★ 2026-10-14 新 Mod：**游戏操作场景优化**（GameOpsOptimize）
///
/// 由原两个 Mod **合并**而来（原包已废弃）：
///   · 「点击反馈与自动拾取」(MobileTapFeedback) —— 点击视觉反馈 + 阳光/金币自动拾取
///   · 「手机操作优化」(MobileUXFixes)         —— 点空处取消拿卡/铲子 + 滚动条优化
///
/// 合并方式（**逻辑一行未改**，只做接线）：
///   · 两套逻辑各自保留在 <see cref="TapFeedbackPart"/> / <see cref="MobileUxFixesPart"/>
///     两个内部静态类里，各自挂自己的 `SceneTree.process_frame` 回调 ⇒ 互不干扰；
///   · 本类只负责把宿主的三个生命周期回调**转发**给它们，任一抛出都吞掉
///     （宿主规则：Initialize/OnAllModsLoaded/Shutdown 抛出 = 整包回滚）。
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
		try { TapFeedbackPart.Initialize(context); }
		catch (Exception ex) { try { GD.PrintErr(P + "点击反馈部分 Initialize 异常（已吞）：" + ex.Message); } catch { } }
		try { MobileUxFixesPart.Initialize(context); }
		catch (Exception ex) { try { GD.PrintErr(P + "操作修正部分 Initialize 异常（已吞）：" + ex.Message); } catch { } }
	}

	public void OnAllModsLoaded()
	{
		try { TapFeedbackPart.OnAllModsLoaded(); }
		catch (Exception ex) { try { GD.PrintErr(P + "点击反馈部分 OnAllModsLoaded 异常（已吞）：" + ex.Message); } catch { } }
		try { MobileUxFixesPart.OnAllModsLoaded(); }
		catch (Exception ex) { try { GD.PrintErr(P + "操作修正部分 OnAllModsLoaded 异常（已吞）：" + ex.Message); } catch { } }
	}

	public void Shutdown()
	{
		// 先关"操作修正"再关"点击反馈"：两者都会还原自己动过的节点，顺序无强依赖，但保持稳定顺序。
		try { MobileUxFixesPart.Shutdown(); }
		catch (Exception ex) { try { GD.PrintErr(P + "操作修正部分 Shutdown 异常（已吞）：" + ex.Message); } catch { } }
		try { TapFeedbackPart.Shutdown(); }
		catch (Exception ex) { try { GD.PrintErr(P + "点击反馈部分 Shutdown 异常（已吞）：" + ex.Message); } catch { } }
	}
}
