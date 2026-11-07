# -*- coding: utf-8 -*-
"""在 GitHub 建仓并推送 GreenSite。

凭据安全（硬约束）：
  - token 由 `git credential fill` 取得，只驻留本进程内存；
  - 不打印、不写入任何文件、不出现在命令行参数里；
  - remote 使用普通 https URL，不含 token；push 由 Git Credential Manager 提供凭据；
  - 所有外部输出经过 redact() 过滤，防止意外泄露。

流程：
  1. 取凭据
  2. POST /user/repos 建公开仓 MEFEason/greensite（已存在则跳过）
  3. 配置 origin
  4. git push -u origin main

失败时明确报告并给出 GitHub Desktop 手动 Publish 步骤，不静默降级。
"""
from __future__ import annotations

import json
import os
import re
import subprocess
import sys
import urllib.error
import urllib.request

OWNER = "MEFEason"
REPO = "greensite"
REPO_URL = f"https://github.com/{OWNER}/{REPO}.git"
ROOT = os.path.normpath(os.path.join(os.path.dirname(os.path.abspath(__file__)), ".."))

SECRET_PATTERNS = [
    re.compile(r"gh[pousr]_[A-Za-z0-9_]{16,}"),
    re.compile(r"https://[^/@\s]+@"),
    re.compile(r"(?i)(authorization|token|password)\s*[:=]\s*\S+"),
]


def redact(text: str) -> str:
    for pattern in SECRET_PATTERNS:
        text = pattern.sub("***", text)
    return text


def git(*args: str, timeout: int = 60) -> subprocess.CompletedProcess[str]:
    env = {**os.environ, "GIT_TERMINAL_PROMPT": "0"}
    return subprocess.run(
        ["git", "-C", ROOT, *args],
        capture_output=True, text=True, timeout=timeout, env=env,
    )


def get_token() -> str:
    """从 Git Credential Manager 取凭据；只返回内存中的 token，绝不输出。"""
    proc = subprocess.run(
        ["git", "credential", "fill"],
        input="protocol=https\nhost=github.com\n\n",
        capture_output=True, text=True, timeout=25,
        env={**os.environ, "GIT_TERMINAL_PROMPT": "0"},
    )
    if proc.returncode != 0:
        raise RuntimeError(f"git credential fill 失败 (code={proc.returncode})")
    values: dict[str, str] = {}
    for line in proc.stdout.splitlines():
        if "=" in line:
            key, value = line.split("=", 1)
            values[key.strip()] = value.strip()
    token = values.get("password", "")
    if not token:
        raise RuntimeError("凭据管理器中没有 github.com 的凭据")
    return token


def create_repo(token: str) -> tuple[str, str]:
    """返回 (状态, 说明)。状态: created / exists / failed。"""
    payload = json.dumps({
        "name": REPO,
        "description": "GreenSite — Windows 网站访问控制程序（.NET 10 WPF / 用户级 PAC 分流）",
        "private": False,
        "has_issues": True,
        "has_wiki": False,
        "has_projects": False,
    }).encode("utf-8")

    request = urllib.request.Request(
        "https://api.github.com/user/repos",
        data=payload,
        method="POST",
        headers={
            "Authorization": f"Bearer {token}",
            "Accept": "application/vnd.github+json",
            "X-GitHub-Api-Version": "2022-11-28",
            "User-Agent": "GreenSite-Publisher",
            "Content-Type": "application/json",
        },
    )
    try:
        with urllib.request.urlopen(request, timeout=25) as response:
            status = response.status
            response.read()
        return ("created", f"HTTP {status}")
    except urllib.error.HTTPError as exc:
        if exc.code == 422:
            return ("exists", "HTTP 422（仓库已存在）")
        return ("failed", f"HTTP {exc.code} {exc.reason}")
    except Exception as exc:  # noqa: BLE001 - 网络类异常需统一降级
        return ("failed", f"{type(exc).__name__}: {exc}")


def manual_steps() -> str:
    return (
        "GitHub Desktop 手动 Publish：\n"
        "  1. File → Add Local Repository，选择 C:\\Users\\Admin\\WorkBuddy\\代码开发\\GreenSite\n"
        f"  2. Publish repository，账号 {OWNER}，名称 {REPO}，取消勾选 Keep this code private\n"
        "  3. Push origin（若未自动推送）"
    )


def main() -> int:
    token = ""
    try:
        # 1) 凭据
        try:
            token = get_token()
        except Exception as exc:  # noqa: BLE001
            print(f"[1/4] 凭据获取失败：{redact(str(exc))}")
            print(manual_steps())
            return 2
        print("[1/4] 凭据：已从 Git Credential Manager 获取（仅内存）")

        # 2) 建仓
        state, detail = create_repo(token)
        print(f"[2/4] 仓库：{state} — {redact(detail)}")
        if state == "failed":
            print(manual_steps())
            return 3

        # 3) 配置 origin
        current = git("remote", "get-url", "origin")
        if current.returncode == 0:
            git("remote", "set-url", "origin", REPO_URL)
        else:
            added = git("remote", "add", "origin", REPO_URL)
            if added.returncode != 0:
                print(f"[3/4] remote 配置失败：{redact(added.stderr.strip())}")
                print(manual_steps())
                return 4
        print(f"[3/4] remote：{REPO_URL}（普通 HTTPS，不含凭据）")

        # 4) 推送
        try:
            pushed = git("push", "-u", "origin", "main", timeout=180)
        except subprocess.TimeoutExpired:
            print("[4/4] push 超时（180s），可能被凭据管理器弹窗阻塞")
            print(manual_steps())
            return 5
        if pushed.returncode != 0:
            print(f"[4/4] push 失败：{redact(pushed.stderr.strip()[:500])}")
            print(manual_steps())
            return 5
        print("[4/4] push：成功")
        print(f"远端：https://github.com/{OWNER}/{REPO}")
        return 0
    finally:
        # token 用完立即从内存移除（不落盘、不打印）
        token = ""


if __name__ == "__main__":
    sys.exit(main())
