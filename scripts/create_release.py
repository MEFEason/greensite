# -*- coding: utf-8 -*-
"""创建 / 复用 GitHub Release 并上传 GreenSite 安装包（纯标准库，零第三方依赖）。

凭据安全（硬约束，与 scripts/publish_github.py 一致）：
  - token 由 `git credential fill`（stdin 仅 protocol/host）取得，只驻留本进程内存；
  - 不打印、不写入任何文件、不出现在命令行参数里；
  - 所有 GitHub 访问均使用普通 https URL，Authorization 仅放内存中的请求头；
  - 所有外部输出经 redact() 过滤（gh[pousr]_*、https://user@、Authorization/token/password=...）；
  - finally 中把 token 置空并清空动态密钥表。

流程：
  1. 读 .version.ini（full / semver / channel），定位 dist/GreenSite-Setup-<full>.exe；
  2. 计算安装包 SHA256（打印前 16 位；完整哈希写入同名 .sha256 交付物）；
  3. git status --porcelain 检查工作区（有改动仅警告，不自动 commit/push）；
  4. 取凭据；
  5. GET releases/tags/v{semver}：已存在则 PATCH 复用并更新 body，不存在则 POST 创建；
  6. 上传安装包 asset（同名已存在则跳过，幂等）；
  7. 打印 Release 页面 URL 与安装包 SHA256。

网络失败 / 超时 / 非 2xx 一律明确报告并给出手动上传步骤，绝不静默跳过。
分页（Link: rel="next"）与重定向（urlopen 自动跟随 30x）均设 20~60s 超时，
网络类 / 网关暂态错误最多重试 3 次（仅这些错误重试）。

用法：
  python scripts/create_release.py            # 正式发布
  python scripts/create_release.py --dry-run  # 只检查产物与版本号，不调用 API、不写文件
"""
from __future__ import annotations

import hashlib
import json
import os
import pathlib
import re
import subprocess
import sys
import urllib.error
import urllib.parse
import urllib.request

OWNER = "MEFEason"
REPO = "greensite"
API_BASE = "https://api.github.com"
UPLOAD_BASE = "https://uploads.github.com"
ROOT = pathlib.Path(__file__).resolve().parent.parent

TIMEOUT_API = 30      # 常规 API：20~60s
TIMEOUT_UPLOAD = 60   # 上传大文件：上限 60s
MAX_RETRIES = 3
RETRYABLE_STATUS = {408, 429, 500, 502, 503, 504}  # 暂态 / 网关（代理 CONNECT 502）错误

USAGE = (
    "用法：python scripts/create_release.py [--dry-run]\n"
    "  --dry-run   仅检查产物与版本号：不调用 GitHub API、不写 .sha256、不做任何 git 写操作\n"
    "  （默认）     创建 / 复用 Release 并上传安装包\n"
    "凭据由 git credential fill 在内存获取，不打印 / 不落盘 / 不进命令行。"
)

# ---- 凭据过滤 ---------------------------------------------------------------
SECRET_PATTERNS = [
    re.compile(r"github_pat_[A-Za-z0-9_]+"),
    re.compile(r"gh[pousr]_[A-Za-z0-9_]{16,}"),
    re.compile(r"https://[^/@\s]+@"),
    re.compile(r"(?i)(authorization|token|password)\s*[:=]\s*\S+"),
]
_DYNAMIC_SECRETS: list[re.Pattern[str]] = []


def register_secret(value: str) -> None:
    """把当前 token 的精确值加入过滤表（仍只在内存，绝不落盘）。"""
    if value and len(value) >= 8:
        _DYNAMIC_SECRETS.append(re.compile(re.escape(value)))


def redact(text: str) -> str:
    for pattern in _DYNAMIC_SECRETS:  # 精确值优先
        text = pattern.sub("***", text)
    for pattern in SECRET_PATTERNS:
        text = pattern.sub("***", text)
    return text


def say(message: str) -> None:
    """所有外部输出的唯一出口，统一经 redact() 过滤。"""
    print(redact(message))


class ReleaseError(Exception):
    """可预期的发布失败，main 统一报告并给出手动步骤。"""


def _brief(data: object) -> str:
    try:
        text = json.dumps(data, ensure_ascii=False, default=str)
    except (TypeError, ValueError):
        text = str(data)
    return redact(text[:400])


def _parse_json(raw: bytes) -> object:
    try:
        return json.loads(raw.decode("utf-8")) if raw else {}
    except (ValueError, UnicodeDecodeError):
        return {}


# ---- 版本 / 产物 ------------------------------------------------------------
def parse_version_ini(path: pathlib.Path) -> dict[str, str]:
    values: dict[str, str] = {}
    for line in path.read_text(encoding="utf-8-sig").splitlines():
        m = re.match(r"^\s*([A-Za-z_][A-Za-z0-9_]*)\s*=\s*(.*?)\s*$", line)
        if m:
            values[m.group(1)] = m.group(2)
    return values


def read_version() -> dict[str, str]:
    values = parse_version_ini(ROOT / ".version.ini")
    for key in ("full", "semver", "channel"):
        if not values.get(key):
            raise ValueError(f".version.ini 缺少或为空：{key}")
    return values


def sha256_file(path: pathlib.Path) -> str:
    h = hashlib.sha256()
    with path.open("rb") as f:
        for chunk in iter(lambda: f.read(1024 * 1024), b""):
            h.update(chunk)
    return h.hexdigest()


def release_page_url(semver: str) -> str:
    return f"https://github.com/{OWNER}/{REPO}/releases/tag/v{semver}"


def manual_upload_steps(semver: str, exe_name: str) -> str:
    return (
        "手动上传安装包（自动发布 / 上传失败时请照做，不静默跳过）：\n"
        f"  1. 打开 {release_page_url(semver)}\n"
        "  2. 点击 Edit（编辑该 Release）\n"
        f"  3. 将 dist\\{exe_name} 拖入页面底部 Assets 区域"
        "（若已有同名 asset，先点其右侧 Delete 删除再拖入）\n"
        "  4. 点击 Update release 保存\n"
        "  说明：本机代理对 uploads.github.com 的 CONNECT 偶发 502，自动上传失败属已知情况，"
        "按上述步骤手动完成即可。"
    )


def check_worktree() -> None:
    """只读检查工作区；有未提交改动仅警告，绝不自动 commit / push。"""
    try:
        proc = subprocess.run(
            ["git", "-C", str(ROOT), "status", "--porcelain"],
            capture_output=True, text=True, timeout=25,
            env={**os.environ, "GIT_TERMINAL_PROMPT": "0"},
        )
    except (OSError, subprocess.TimeoutExpired) as exc:
        say(f"[3/6] 工作区检查跳过（git 不可用 / 超时）：{exc}（继续发布）")
        return
    if proc.returncode != 0:
        say(f"[3/6] 工作区检查跳过（git 退出码 {proc.returncode}）：{proc.stderr.strip()[:200]}（继续发布）")
        return
    if proc.stdout.strip():
        say("[3/6] [WARN] 工作区存在未提交改动（仅警告，不自动 commit/push）：")
        for line in proc.stdout.strip().splitlines()[:20]:
            say(f"        {line}")
    else:
        say("[3/6] 工作区干净（无未提交改动）")


# ---- 凭据 -------------------------------------------------------------------
def get_token() -> str:
    """从 Git Credential Manager 取凭据；只返回内存中的 token，绝不输出其 stdout。"""
    proc = subprocess.run(
        ["git", "credential", "fill"],
        input="protocol=https\nhost=github.com\n\n",
        capture_output=True, text=True, timeout=25,
        env={**os.environ, "GIT_TERMINAL_PROMPT": "0"},
    )
    if proc.returncode != 0:
        raise RuntimeError(f"git credential fill 失败（退出码 {proc.returncode}）")
    values: dict[str, str] = {}
    for line in proc.stdout.splitlines():
        if "=" in line:
            key, value = line.split("=", 1)
            values[key.strip()] = value.strip()
    token = values.get("password", "")
    if not token:
        raise RuntimeError("凭据管理器中没有 github.com 的凭据")
    return token


# ---- HTTP（超时 / 重试 / 分页 / 重定向）-------------------------------------
def _http_open(request: urllib.request.Request, timeout: int,
               retries: int = MAX_RETRIES):
    """urlopen 自动跟随 30x 重定向；仅网络类 / 网关暂态错误重试，最多 3 次。"""
    last = ""
    for attempt in range(1, retries + 1):
        try:
            return urllib.request.urlopen(request, timeout=timeout)
        except urllib.error.HTTPError as exc:
            if exc.code in RETRYABLE_STATUS and attempt < retries:
                last = f"HTTP {exc.code} {exc.reason}"
                continue
            raise  # 业务 / 非暂态状态码交还调用方按 status 处理
        except (urllib.error.URLError, TimeoutError, OSError) as exc:
            last = f"{type(exc).__name__}: {exc}"
            if attempt < retries:
                continue
            raise ReleaseError(f"网络错误（已重试 {retries} 次）：{last}") from None
    raise ReleaseError(f"网络错误（已重试 {retries} 次）：{last}")


def _github_headers(token: str, content_type: str | None = None) -> dict[str, str]:
    headers = {
        "Authorization": f"Bearer {token}",
        "Accept": "application/vnd.github+json",
        "X-GitHub-Api-Version": "2022-11-28",
        "User-Agent": "GreenSite-Release",
    }
    if content_type:
        headers["Content-Type"] = content_type
    return headers


def api_json(method: str, url: str, token: str, payload=None,
             timeout: int = TIMEOUT_API):
    """返回 (status, parsed_json, headers)；非 2xx 以 status 形式返回，不抛。"""
    data = None
    if payload is not None:
        data = json.dumps(payload, ensure_ascii=False).encode("utf-8")
    req = urllib.request.Request(
        url, data=data, method=method,
        headers=_github_headers(token, "application/json" if data else None),
    )
    try:
        with _http_open(req, timeout) as resp:
            return resp.status, _parse_json(resp.read()), dict(resp.headers)
    except urllib.error.HTTPError as exc:
        return exc.code, _parse_json(exc.read()), dict(exc.headers)


def _next_link(headers: dict[str, str]) -> str | None:
    link = headers.get("Link") or headers.get("link") or ""
    for part in link.split(","):
        if 'rel="next"' in part:
            m = re.search(r"<([^>]+)>", part)
            if m:
                return m.group(1)
    return None


def list_assets(token: str, release_id: int) -> list[tuple[str, object]]:
    """GET assets，按 Link: rel="next" 翻页，每页 per_page=100。"""
    url = f"{API_BASE}/repos/{OWNER}/{REPO}/releases/{release_id}/assets?per_page=100"
    found: list[tuple[str, object]] = []
    for _ in range(20):
        status, data, headers = api_json("GET", url, token, timeout=TIMEOUT_API)
        if status != 200:
            raise ReleaseError(f"列出 assets 失败：HTTP {status}  {_brief(data)}")
        if isinstance(data, list):
            for item in data:
                if isinstance(item, dict) and item.get("name"):
                    found.append((str(item["name"]), item.get("id")))
        nxt = _next_link(headers)
        if not nxt:
            break
        url = nxt
    return found


def ensure_release(token: str, tag: str, semver: str, body: str) -> tuple[int, str]:
    """幂等：已存在则 PATCH 复用并更新 body，不存在则 POST 创建。"""
    base = f"{API_BASE}/repos/{OWNER}/{REPO}/releases"
    status, existing, _ = api_json("GET", f"{base}/tags/{tag}", token, timeout=TIMEOUT_API)
    if status == 200 and isinstance(existing, dict) and existing.get("id"):
        rid = int(existing["id"])
        pstatus, pdata, _ = api_json(
            "PATCH", f"{base}/{rid}", token,
            payload={"name": f"GreenSite v{semver}", "body": body},
            timeout=TIMEOUT_API,
        )
        if pstatus != 200:
            raise ReleaseError(f"复用 Release 后更新 body 失败：HTTP {pstatus}  {_brief(pdata)}")
        return rid, "已存在，复用并更新 body（未重复创建）"
    if status == 404:
        cstatus, created, _ = api_json(
            "POST", base, token,
            payload={
                "tag_name": tag,
                "target_commitish": "main",
                "name": f"GreenSite v{semver}",
                "body": body,
                "draft": False,
                "prerelease": False,
            },
            timeout=TIMEOUT_API,
        )
        if cstatus not in (200, 201) or not isinstance(created, dict) or not created.get("id"):
            raise ReleaseError(f"创建 Release 失败：HTTP {cstatus}  {_brief(created)}")
        return int(created["id"]), "已新建（draft=false, prerelease=false, target_commitish=main）"
    raise ReleaseError(f"查询 tag={tag} 异常：HTTP {status}  {_brief(existing)}")


def upload_post(url: str, token: str, data: bytes):
    """POST 安装包字节到 uploads.github.com；Content-Type 为 octet-stream。"""
    req = urllib.request.Request(
        url, data=data, method="POST",
        headers={
            "Content-Type": "application/octet-stream",
            "Authorization": f"Bearer {token}",
            "Accept": "application/vnd.github+json",
            "X-GitHub-Api-Version": "2022-11-28",
            "User-Agent": "GreenSite-Release",
        },
    )
    try:
        with _http_open(req, TIMEOUT_UPLOAD) as resp:
            return resp.status, _parse_json(resp.read())
    except urllib.error.HTTPError as exc:
        return exc.code, _parse_json(exc.read())


def ensure_asset(token: str, release_id: int, exe: pathlib.Path) -> str:
    """幂等：同名 asset 已存在则跳过并提示；否则上传，非 201 明确报错。"""
    for name, asset_id in list_assets(token, release_id):
        if name == exe.name:
            return (
                f"同名 asset 已存在（id={asset_id}），跳过上传（幂等，未重复上传）；"
                "如需替换为新构建，请到 Release 页面 Edit 删除该 asset 后重跑本脚本"
            )
    url = (
        f"{UPLOAD_BASE}/repos/{OWNER}/{REPO}/releases/{release_id}/assets"
        f"?name={urllib.parse.quote(exe.name, safe='')}"
    )
    status, resp = upload_post(url, token, exe.read_bytes())
    if status != 201:
        raise ReleaseError(
            f"上传安装包失败：HTTP {status}  {_brief(resp)}"
            "（uploads.github.com 可能因代理 CONNECT 偶发 502 不可达）"
        )
    return f"已上传（{exe.stat().st_size} 字节，HTTP 201）"


# ---- main -------------------------------------------------------------------
def main() -> int:
    token = ""
    try:
        argv = sys.argv[1:]
        if any(a in ("-h", "--help") for a in argv):
            say(USAGE)
            return 0
        unknown = [a for a in argv if a != "--dry-run"]
        if unknown:
            say(f"未知参数：{', '.join(unknown)}")
            say(USAGE)
            return 2
        dry_run = "--dry-run" in argv

        # [1/6] 版本与产物
        try:
            ver = read_version()
        except (OSError, ValueError) as exc:
            say(f"[1/6] 读取版本失败：{exc}")
            return 2
        full, semver, channel = ver["full"], ver["semver"], ver["channel"]
        say(f"[1/6] 版本 full={full} | semver={semver} | channel={channel}")
        if channel != "release":
            say(f"[WARN] channel={channel} 不是 release，但 tag 仍按正式版 v{semver} 发布，请确认 .version.ini 已切换")
        exe = ROOT / "dist" / f"GreenSite-Setup-{full}.exe"
        if not exe.is_file():
            say(f"[1/6] 安装包不存在：{exe}")
            say("请先运行 scripts/build.ps1 生成 dist/GreenSite-Setup-<full>.exe 后再发布。")
            return 2
        say(f"[1/6] 安装包 {exe.name}（{exe.stat().st_size} 字节）")

        # [2/6] SHA256
        digest = sha256_file(exe)
        say(f"[2/6] 安装包 SHA256（前 16 位）：{digest[:16]}…")
        if not dry_run:
            sha_path = exe.with_name(exe.name + ".sha256")
            sha_path.write_text(f"{digest}  {exe.name}\n", encoding="utf-8")
            say(f"[2/6] 已写入校验文件 {sha_path.name}（完整哈希，交付物）")

        # [3/6] 工作区
        check_worktree()

        # Release body 来自 RELEASE.md
        release_md = ROOT / "RELEASE.md"
        if not release_md.is_file():
            say("[前置] 缺少 RELEASE.md，无法作为 Release body，请先补齐后重试。")
            return 2
        body = release_md.read_text(encoding="utf-8-sig")

        if dry_run:
            say("[dry-run] 仅检查产物与版本号：未调用 GitHub API、未写 .sha256、未做任何 git 写操作")
            say(f"[dry-run] 计划 tag=v{semver} | name=GreenSite v{semver} | asset={exe.name}")
            say(f"[dry-run] 预计 Release 页面：{release_page_url(semver)}")
            say(f"[dry-run] 安装包 SHA256：{digest}")
            return 0

        # [4/6] 凭据
        try:
            token = get_token()
        except Exception as exc:  # noqa: BLE001 - 凭据失败统一降级到手动步骤
            say(f"[4/6] 凭据获取失败：{exc}")
            say(manual_upload_steps(semver, exe.name))
            return 3
        register_secret(token)
        say("[4/6] 凭据：已由 git credential fill 取回并仅驻留内存（不打印 / 不落盘 / 不进命令行）")

        # [5/6] Release 查询 / 复用 / 创建
        tag = f"v{semver}"
        try:
            release_id, action = ensure_release(token, tag, semver, body)
        except ReleaseError as exc:
            say(f"[5/6] Release 处理失败：{exc}")
            say(manual_upload_steps(semver, exe.name))
            return 4
        say(f"[5/6] Release {action}（id={release_id}，tag={tag}）")

        # [6/6] 上传资源（幂等）
        try:
            asset_msg = ensure_asset(token, release_id, exe)
        except ReleaseError as exc:
            say(f"[6/6] 安装包上传失败：{exc}")
            say(manual_upload_steps(semver, exe.name))
            return 5
        say(f"[6/6] 安装包资源：{asset_msg}")

        say("")
        say(f"[完成] Release 页面：{release_page_url(semver)}")
        say(f"[完成] 安装包：dist/{exe.name}")
        say(f"[完成] 安装包 SHA256：{digest}")
        return 0
    finally:
        # token 用完立即从内存移除，并清空动态过滤表
        token = ""
        _DYNAMIC_SECRETS.clear()


if __name__ == "__main__":
    sys.exit(main())
