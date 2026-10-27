# -*- coding: utf-8 -*-
"""统一构建入口（本 Mod 的真实实现在 build_mod.py，这里只转发，不复制逻辑）。

为什么需要这个文件
------------------
全队构建必须串行：`python mods/_build_locked.py <Mod> [--install]`。
而 `mods/_build_locked.py` 只认 `<Mod>/build_and_install.py`（各 Mod 构建脚本开头都会
`taskkill /F /IM dotnet.exe`，并行会互相杀掉对方正在跑的 dotnet）。
本 Mod 的构建脚本历史上叫 `build_mod.py`，为不改动它、又能走统一锁入口，这里做纯转发。

用法：
    python mods/_build_locked.py GameOpsOptimize            # 只编译
    python mods/_build_locked.py GameOpsOptimize --install  # 编译 + 打包 + 装机
"""
import os
import subprocess
import sys

BASE = os.path.dirname(os.path.abspath(__file__))
_TARGET = os.path.join(BASE, "build_mod.py")

raise SystemExit(
    subprocess.run([sys.executable, _TARGET] + sys.argv[1:], cwd=BASE).returncode
)
