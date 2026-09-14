"""把 UCI「Character Trajectories」数据集导成测试可直接读的紧凑格式。

数据集：UCI ML Repository, id 175, "Character Trajectories"
  - 2858 条真实手写轨迹（拉丁字母），**采样率 200 Hz（Δt = 5 ms）**
  - 每个采样点三个通道：x、y、**笔尖压力（force）**
  - 原始文件：mixoutALL_shifted.mat（MATLAB v5）

为什么用它：预测必须有**真实时间轴**才有意义（QuickDraw 那类只有坐标没有时间，
就没法验证"往前 10 ms"这件事）；这份数据既有时基又有压力，
顺手也能给我们自己的压感管线当输入。

用法：
    python tools/datasets/Export-CharacterTrajectories.py <mixoutALL_shifted.mat> <out.bin>

输出格式（小端）：
    magic  "CTRJ" (4 字节)
    sample_dt_us  int32          —— 采样间隔（微秒），数据集是 5000
    n_traj int32
    每条轨迹： n_points int32，然后 n_points × (float32 x, float32 y, float32 force)
"""

import struct
import sys

import numpy as np
from scipy.io import loadmat


def main() -> int:
    if len(sys.argv) < 3:
        print(__doc__)
        return 2

    src, dst = sys.argv[1], sys.argv[2]
    mat = loadmat(src, squeeze_me=True, struct_as_record=False)

    keys = [k for k in mat.keys() if not k.startswith("__")]
    print("mat vars:", keys)

    # 数据变量是那个"有很多条轨迹"的 object 数组；这版数据集叫 mixout
    data = mat["mixout"] if "mixout" in mat else max(
        (mat[k] for k in keys), key=lambda v: np.asarray(v, dtype=object).size)
    trajs = list(np.atleast_1d(data))
    print("trajectories:", len(trajs))

    # 采样间隔：consts.dt（秒）。数据集是 200 Hz。
    dt_us = 5000
    if "consts" in mat and hasattr(mat["consts"], "dt"):
        dt = float(np.asarray(mat["consts"].dt).ravel()[0])
        if 0 < dt < 1:
            dt_us = int(round(dt * 1e6))
        print(f"consts.dt = {dt} s  ->  {dt_us} us")

    total = 0
    written = 0
    lens = []
    fx_min, fx_max = float("inf"), float("-inf")

    with open(dst, "wb") as f:
        f.write(b"CTRJ")
        f.write(struct.pack("<i", dt_us))
        f.write(struct.pack("<i", len(trajs)))
        for t in trajs:
            arr = np.asarray(t, dtype=np.float64)
            if arr.ndim != 2 or arr.shape[0] < 3:
                continue
            if arr.shape[0] > 3:                  # 若按列存，转成 (3, N)
                arr = arr.T
            n = arr.shape[1]
            if n < 8:
                continue
            xs, ys, fs = arr[0], arr[1], arr[2]
            f.write(struct.pack("<i", n))
            for i in range(n):
                f.write(struct.pack("<fff", float(xs[i]), float(ys[i]), float(fs[i])))
            total += n
            written += 1
            lens.append(n)
            fx_min = min(fx_min, float(fs.min()))
            fx_max = max(fx_max, float(fs.max()))

    lens_arr = np.asarray(lens)
    print(f"wrote {written} trajectories, {total} samples -> {dst}")
    print(f"points per trajectory: median {np.median(lens_arr):.0f}, min {lens_arr.min()}, max {lens_arr.max()}")
    print(f"force range: {fx_min:.4f} ~ {fx_max:.4f}")
    print(f"total duration about {total * dt_us / 1e6:.1f} s")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
