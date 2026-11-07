# -*- coding: utf-8 -*-
"""GreenSite 发布前只读体检（绝不修改 / 删除 / 创建任何文件）。

检查项：
  - .version.ini 可解析、channel=release、semver 符合 ^\\d+\\.\\d+\\.\\d+$
  - dist/GreenSite-Setup-<full>.exe 存在且 > 100 KB
  - 同名 .sha256（若存在）与实算 SHA256 一致
  - outputs/GreenSite/GreenSite.exe 存在
  - outputs/GreenSite/version.ini 的 full 与根 .version.ini 一致
  - RELEASE.md 存在且包含 semver；README.md 存在
  - git status --porcelain 为空（非空仅警告）
  - git rev-parse HEAD 与 origin/main 一致（远端不可达仅警告）

每项输出 [OK] / [FAIL] / [WARN]；任一 [FAIL] 则退出码 1。
最后打印产物清单（路径、大小、SHA256 前 16 位）。

用法：python scripts/verify_release.py
"""
from __future__ import annotations

import hashlib
import os
import pathlib
import re
import subprocess
import sys

ROOT = pathlib.Path(__file__).resolve().parent.parent
MIN_SETUP_BYTES = 100 * 1024

_failures = 0
_warns = 0


def ok(name: str, detail: str = "") -> None:
    line = f"[OK]   {name}"
    if detail:
        line += f" — {detail}"
    print(line)


def fail(name: str, reason: str) -> None:
    global _failures
    _failures += 1
    print(f"[FAIL] {name} — {reason}")


def warn(name: str, reason: str) -> None:
    global _warns
    _warns += 1
    print(f"[WARN] {name} — {reason}")


def parse_ini(path: pathlib.Path) -> dict[str, str]:
    values: dict[str, str] = {}
    for line in path.read_text(encoding="utf-8-sig").splitlines():
        m = re.match(r"^\s*([A-Za-z_][A-Za-z0-9_]*)\s*=\s*(.*?)\s*$", line)
        if m:
            values[m.group(1)] = m.group(2)
    return values


def sha256_file(path: pathlib.Path) -> str:
    h = hashlib.sha256()
    with path.open("rb") as f:
        for chunk in iter(lambda: f.read(1024 * 1024), b""):
            h.update(chunk)
    return h.hexdigest()


def git_out(*args: str, timeout: int = 20):
    """只读 git 命令；不可用 / 超时返回 None。"""
    try:
        return subprocess.run(
            ["git", "-C", str(ROOT), *args],
            capture_output=True, text=True, timeout=timeout,
            env={**os.environ, "GIT_TERMINAL_PROMPT": "0"},
        )
    except (OSError, subprocess.TimeoutExpired):
        return None


def main() -> int:
    # 1. .version.ini 可解析
    ini_path = ROOT / ".version.ini"
    try:
        ver = parse_ini(ini_path)
    except OSError as exc:
        fail(".version.ini 可解析", f"读取失败：{exc}")
        ver = {}
    if ver:
        ok(".version.ini 可解析", f"共 {len(ver)} 个键")
    else:
        fail(".version.ini 可解析", "文件为空或无有效 key=value")

    full = ver.get("full", "")
    semver = ver.get("semver", "")
    channel = ver.get("channel", "")

    # 2. channel = release
    if channel == "release":
        ok("channel 为 release", "正式发布渠道")
    else:
        fail("channel 为 release", f"实际 channel={channel or '<空>'}")

    # 3. semver 格式
    if re.fullmatch(r"\d+\.\d+\.\d+", semver):
        ok("semver 符合 x.y.z", semver)
    else:
        fail("semver 符合 ^\\d+\\.\\d+\\.\\d+$", f"实际 semver={semver or '<空>'}")

    # 4. 安装包存在且 > 100 KB
    setup = ROOT / "dist" / f"GreenSite-Setup-{full}.exe"
    setup_digest = ""
    if not full:
        fail("安装包存在且 > 100 KB", "full 为空，无法定位 dist/GreenSite-Setup-<full>.exe")
    elif not setup.is_file():
        fail("安装包存在且 > 100 KB", f"未找到 {setup}")
    else:
        size = setup.stat().st_size
        if size > MIN_SETUP_BYTES:
            ok("安装包存在且 > 100 KB", f"{size} 字节，{setup.name}")
        else:
            fail("安装包存在且 > 100 KB", f"体积 {size} 字节，未超过 {MIN_SETUP_BYTES} 字节")
        setup_digest = sha256_file(setup)

    # 5. .sha256（若存在）与实算 SHA256 一致
    sha_path = setup.with_name(setup.name + ".sha256")
    if not setup.is_file():
        warn(".sha256 校验文件", "安装包不存在，跳过哈希比对")
    elif not sha_path.is_file():
        warn(".sha256 校验文件（可选）", f"未找到 {sha_path.name}；建议运行 create_release.py 生成")
    else:
        try:
            text = sha_path.read_text(encoding="utf-8-sig")
            m = re.search(r"\b[0-9a-fA-F]{64}\b", text)
            recorded = m.group(0).lower() if m else ""
        except OSError as exc:
            fail(".sha256 可读取", f"读取失败：{exc}")
            recorded = ""
        if not recorded:
            fail(".sha256 与实算 SHA256 一致", "文件内未找到 64 位十六进制哈希")
        elif recorded == setup_digest:
            ok(".sha256 与实算 SHA256 一致", recorded[:16] + "…")
        else:
            fail(".sha256 与实算 SHA256 一致",
                 f"记录={recorded[:16]}… 实算={setup_digest[:16]}…")

    # 6. outputs/GreenSite/GreenSite.exe 存在
    app_exe = ROOT / "outputs" / "GreenSite" / "GreenSite.exe"
    if app_exe.is_file():
        ok("outputs/GreenSite/GreenSite.exe 存在", f"{app_exe.stat().st_size} 字节")
    else:
        fail("outputs/GreenSite/GreenSite.exe 存在", f"未找到 {app_exe}")

    # 7. outputs/GreenSite/version.ini 的 full 与根一致
    out_ini = ROOT / "outputs" / "GreenSite" / "version.ini"
    if not out_ini.is_file():
        fail("outputs 版本 full 与根一致", f"未找到 {out_ini}")
    else:
        try:
            out_full = parse_ini(out_ini).get("full", "")
            if full and out_full == full:
                ok("outputs 版本 full 与根一致", full)
            else:
                fail("outputs 版本 full 与根一致",
                     f"根 full={full or '<空>'} / 产物 full={out_full or '<空>'}")
        except OSError as exc:
            fail("outputs 版本 full 与根一致", f"读取失败：{exc}")

    # 8. RELEASE.md 存在且包含 semver
    release_md = ROOT / "RELEASE.md"
    if not release_md.is_file():
        fail("RELEASE.md 存在且包含 semver", "未找到 RELEASE.md")
    else:
        try:
            text = release_md.read_text(encoding="utf-8-sig")
            if semver and semver in text:
                ok("RELEASE.md 存在且包含 semver", f"含 {semver}")
            else:
                fail("RELEASE.md 存在且包含 semver", f"未包含 semver={semver or '<空>'}")
        except OSError as exc:
            fail("RELEASE.md 存在且包含 semver", f"读取失败：{exc}")

    # 9. README.md 存在
    if (ROOT / "README.md").is_file():
        ok("README.md 存在", "")
    else:
        fail("README.md 存在", "未找到 README.md")

    # 10. git status --porcelain 为空（非空仅警告，不判失败）
    status_proc = git_out("status", "--porcelain")
    if status_proc is None:
        warn("git status --porcelain 为空", "git 不可用 / 超时，跳过（不判失败）")
    elif status_proc.returncode != 0:
        warn("git status --porcelain 为空",
             f"git 退出码 {status_proc.returncode}：{status_proc.stderr.strip()[:120]}")
    elif status_proc.stdout.strip():
        lines = status_proc.stdout.strip().splitlines()
        warn("git status --porcelain 为空",
             f"存在 {len(lines)} 处未提交改动（按要求仅警告，不判失败）")
        for line in lines[:10]:
            print(f"         {line}")
    else:
        ok("git status --porcelain 为空", "工作区干净")

    # 11. HEAD 与 origin/main 一致（远端不可达仅警告）
    head = git_out("rev-parse", "HEAD")
    origin = git_out("rev-parse", "origin/main")
    if head is None or origin is None:
        warn("HEAD 与 origin/main 一致", "git 命令不可用 / 超时，跳过")
    elif head.returncode != 0 or origin.returncode != 0:
        warn("HEAD 与 origin/main 一致",
             "origin/main 引用不可用（可能尚未 fetch 或远端不可达），按约定给警告")
    else:
        h = head.stdout.strip()
        o = origin.stdout.strip()
        if h and h == o:
            ok("HEAD 与 origin/main 一致", h[:12])
        else:
            fail("HEAD 与 origin/main 一致",
                 f"HEAD={h[:12] or '<空>'} origin/main={o[:12] or '<空>'}")

    # 产物清单（路径、大小、SHA256 前 16 位）
    print("")
    print("产物清单：")
    for label, path in (
        ("安装包", setup),
        ("绿色目录主程序", app_exe),
        ("SHA256 校验文件", sha_path),
        ("Release 说明", release_md),
    ):
        if path.is_file():
            digest16 = sha256_file(path)[:16]
            print(f"  {label}: {path.relative_to(ROOT)}  |  {path.stat().st_size} 字节  |  SHA256 {digest16}…")
        else:
            print(f"  {label}: {path.relative_to(ROOT)}  |  (不存在)")

    print("")
    if _failures:
        print(f"[verify] 共 {_failures} 项 [FAIL]、{_warns} 项 [WARN]，发布前体检未通过")
        return 1
    print(f"[verify] 全部检查通过（{_warns} 项 [WARN]）")
    return 0


if __name__ == "__main__":
    sys.exit(main())
