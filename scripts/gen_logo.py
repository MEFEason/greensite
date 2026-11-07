# -*- coding: utf-8 -*-
"""GreenSite 品牌图标生成器（零第三方素材）。

图形由本脚本用 Pillow 直接绘制，同时输出等价的 SVG 矢量源。
产物：
  src/GreenSite/assets/logo.svg   矢量源（README / 关于页）
  src/GreenSite/assets/logo.png   512x512 预览
  src/GreenSite/assets/app.ico    多尺寸图标（托盘 / 窗口 / 安装包）

设计：绿色渐变圆角底（品牌色）
      + 白色浏览器窗口与地址栏（网站元素）
      + 窗口内深绿盾牌与白色对勾（访问控制 / 安全放行）
小尺寸只保留「白窗口 + 顶栏 + 深绿盾」三层强轮廓，16x16 仍可辨。
"""
from __future__ import annotations

import os
from PIL import Image, ImageChops, ImageDraw, ImageFilter

BASE = 1024
SS = 4  # 超采样倍数，抗锯齿
S = BASE * SS

HERE = os.path.dirname(os.path.abspath(__file__))
ASSETS = os.path.normpath(os.path.join(HERE, "..", "src", "GreenSite", "assets"))

TOP = (52, 211, 153)      # #34D399
BOTTOM = (5, 150, 105)    # #059669
WINDOW = (255, 255, 255)
TOPBAR = (241, 245, 249)  # #F1F5F9
ADDRESS = (226, 232, 240) # #E2E8F0
URL_BAR = (203, 213, 225) # #CBD5E1
DOTS = (148, 163, 184)    # #94A3B8
SHIELD = (6, 95, 70)      # #065F46
CHECK = (255, 255, 255)

SVG = """<svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 1024 1024" width="1024" height="1024" role="img" aria-label="GreenSite">
  <title>GreenSite — 网站访问控制</title>
  <defs>
    <linearGradient id="bg" x1="0" y1="0" x2="0" y2="1">
      <stop offset="0" stop-color="#34D399"/>
      <stop offset="1" stop-color="#059669"/>
    </linearGradient>
    <clipPath id="round"><rect x="64" y="64" width="896" height="896" rx="216"/></clipPath>
    <clipPath id="win"><rect x="188" y="246" width="648" height="548" rx="58"/></clipPath>
  </defs>
  <g clip-path="url(#round)">
    <rect x="64" y="64" width="896" height="896" fill="url(#bg)"/>
    <ellipse cx="512" cy="0" rx="372" ry="330" fill="#ffffff" opacity="0.16"/>
  </g>
  <g clip-path="url(#win)">
    <rect x="188" y="246" width="648" height="548" fill="#ffffff"/>
    <rect x="188" y="246" width="648" height="104" fill="#F1F5F9"/>
    <circle cx="248" cy="298" r="15" fill="#94A3B8"/>
    <circle cx="302" cy="298" r="15" fill="#94A3B8"/>
    <circle cx="356" cy="298" r="15" fill="#94A3B8"/>
    <rect x="410" y="278" width="376" height="40" rx="20" fill="#E2E8F0"/>
    <rect x="438" y="293" width="170" height="10" rx="5" fill="#CBD5E1"/>
  </g>
  <g transform="translate(512 573) scale(0.54) translate(-512 -537)">
    <path d="M300 268 Q512 236 724 268 Q734 452 712 566 Q652 716 512 806 Q372 716 312 566 Q290 452 300 268 Z" fill="#065F46"/>
    <path d="M382 520 L468 606 L652 404" fill="none" stroke="#ffffff" stroke-width="96" stroke-linecap="round" stroke-linejoin="round"/>
  </g>
</svg>
"""


def px(v: float) -> int:
    """基准坐标 -> 超采样坐标。"""
    return int(round(v * SS))


def lerp(a, b, t):
    return tuple(int(round(a[i] + (b[i] - a[i]) * t)) for i in range(3))


def quad(p0, p1, p2, steps=48):
    """二次贝塞尔采样，返回点列表（不含起点、含终点）。"""
    out = []
    for i in range(1, steps + 1):
        t = i / steps
        u = 1 - t
        out.append((u * u * p0[0] + 2 * u * t * p1[0] + t * t * p2[0],
                    u * u * p0[1] + 2 * u * t * p1[1] + t * t * p2[1]))
    return out


def shield_points():
    """盾牌轮廓：上缘微弧、两侧竖直、底部收尖（1024 基准坐标）。"""
    pts = [(300, 268)]
    pts += quad((300, 268), (512, 236), (724, 268))
    pts += quad((724, 268), (734, 452), (712, 566))
    pts += quad((712, 566), (652, 716), (512, 806))
    pts += quad((512, 806), (372, 716), (312, 566))
    pts += quad((312, 566), (290, 452), (300, 268))
    return pts


def placed(points, scale=0.54, cx=512, cy=537, tx=512, ty=573):
    """把盾/勾按统一变换放进窗口内容区。SVG 用同参数的 transform。"""
    return [((x - cx) * scale + tx, (y - cy) * scale + ty) for x, y in points]


def build():
    os.makedirs(ASSETS, exist_ok=True)

    # 1) 垂直渐变底 + 圆角裁剪
    grad = Image.new("RGB", (S, S), TOP)
    gd = ImageDraw.Draw(grad)
    for y in range(S):
        gd.line([(0, y), (S, y)], fill=lerp(TOP, BOTTOM, y / (S - 1)))
    mask = Image.new("L", (S, S), 0)
    ImageDraw.Draw(mask).rounded_rectangle(
        [px(64), px(64), px(960), px(960)], radius=px(216), fill=255)
    canvas = Image.new("RGBA", (S, S), (0, 0, 0, 0))
    canvas.paste(grad, (0, 0), mask)

    # 2) 顶部柔光（裁剪在圆角内）
    glow = Image.new("RGBA", (S, S), (0, 0, 0, 0))
    ImageDraw.Draw(glow).ellipse(
        [px(140), px(-330), px(884), px(330)], fill=(255, 255, 255, 46))
    glow = glow.filter(ImageFilter.GaussianBlur(px(70)))
    canvas = Image.alpha_composite(canvas, glow)
    canvas.putalpha(ImageChops.multiply(canvas.getchannel("A"), mask))

    # 3) 浏览器窗口（网站元素）
    win_box = [px(188), px(246), px(836), px(794)]
    win_mask = Image.new("L", (S, S), 0)
    ImageDraw.Draw(win_mask).rounded_rectangle(win_box, radius=px(58), fill=255)

    shadow = Image.new("RGBA", (S, S), (0, 0, 0, 0))
    ImageDraw.Draw(shadow).rounded_rectangle(
        [win_box[0] + px(4), win_box[1] + px(20), win_box[2] + px(4), win_box[3] + px(20)],
        radius=px(58), fill=(0, 45, 30, 110))
    shadow = shadow.filter(ImageFilter.GaussianBlur(px(24)))
    canvas = Image.alpha_composite(canvas, shadow)

    win = Image.new("RGBA", (S, S), (0, 0, 0, 0))
    wd = ImageDraw.Draw(win)
    wd.rounded_rectangle(win_box, radius=px(58), fill=WINDOW + (255,))
    wd.rectangle([px(188), px(246), px(836), px(350)], fill=TOPBAR + (255,))
    for cx in (248, 302, 356):
        wd.ellipse([px(cx - 15), px(283), px(cx + 15), px(313)], fill=DOTS + (255,))
    wd.rounded_rectangle([px(410), px(278), px(786), px(318)], radius=px(20), fill=ADDRESS + (255,))
    wd.rounded_rectangle([px(438), px(293), px(608), px(303)], radius=px(5), fill=URL_BAR + (255,))
    win.putalpha(ImageChops.multiply(win.getchannel("A"), win_mask))
    canvas = Image.alpha_composite(canvas, win)

    # 4) 窗口内盾牌 + 白色对勾
    sp = [(px(x), px(y)) for x, y in placed(shield_points())]
    shield_shadow = Image.new("RGBA", (S, S), (0, 0, 0, 0))
    ImageDraw.Draw(shield_shadow).polygon(
        [(x + px(6), y + px(14)) for x, y in sp], fill=(0, 40, 25, 70))
    shield_shadow = shield_shadow.filter(ImageFilter.GaussianBlur(px(12)))
    canvas = Image.alpha_composite(canvas, shield_shadow)

    layer = Image.new("RGBA", (S, S), (0, 0, 0, 0))
    d = ImageDraw.Draw(layer)
    d.polygon(sp, fill=SHIELD + (255,))
    pts = [(px(x), px(y)) for x, y in placed([(382, 520), (468, 606), (652, 404)])]
    w = px(52)
    d.line(pts, fill=CHECK + (255,), width=w, joint="curve")
    r = w // 2
    for x, y in (pts[0], pts[-1]):
        d.ellipse([x - r, y - r, x + r, y + r], fill=CHECK + (255,))
    canvas = Image.alpha_composite(canvas, layer)

    # 5) 收采样并输出
    full = canvas.resize((BASE, BASE), Image.LANCZOS)
    svg_path = os.path.join(ASSETS, "logo.svg")
    png_path = os.path.join(ASSETS, "logo.png")
    ico_path = os.path.join(ASSETS, "app.ico")

    with open(svg_path, "w", encoding="utf-8", newline="\n") as f:
        f.write(SVG)
    full.resize((512, 512), Image.LANCZOS).save(png_path)
    full.save(ico_path, format="ICO", sizes=[
        (16, 16), (24, 24), (32, 32), (48, 48), (64, 64), (128, 128), (256, 256),
    ])

    for p in (svg_path, png_path, ico_path):
        print(f"{os.path.basename(p):12s} {os.path.getsize(p):>8d} B  {p}")


if __name__ == "__main__":
    build()
