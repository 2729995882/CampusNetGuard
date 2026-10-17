from PIL import Image, ImageDraw, ImageFont
from pathlib import Path

OUT = Path(__file__).resolve().parents[2] / 'docs' / 'assets'
OUT.mkdir(parents=True, exist_ok=True)
MSYH = 'C:/Windows/Fonts/msyh.ttc'
MSYH_BD = 'C:/Windows/Fonts/msyhbd.ttc'

def font(size, bold=False):
    return ImageFont.truetype(MSYH_BD if bold else MSYH, size)

def measure(draw, text, f):
    return draw.textlength(text, font=f)

def wrap_text(draw, text, f, max_width):
    lines = []
    for raw in text.split('\n'):
        line = ''
        for ch in raw:
            test = line + ch
            if line and measure(draw, test, f) > max_width:
                lines.append(line)
                line = ch
            else:
                line = test
        lines.append(line)
    return lines

def draw_box(draw, xy, text, fill, outline, f, radius=22, text_color=(28, 38, 55), align='center', pad=10):
    x1, y1, x2, y2 = xy
    draw.rounded_rectangle(xy, radius=radius, fill=fill, outline=outline, width=3)
    lines = wrap_text(draw, text, f, (x2 - x1) - 2 * pad)
    total = len(lines) * (f.size + 10)
    cy = (y1 + y2) / 2 - total / 2
    for i, line in enumerate(lines):
        if align == 'left':
            tx = x1 + pad
            anchor = 'la'
        else:
            tx = (x1 + x2) / 2
            anchor = 'ma'
        draw.text((tx, cy + i * (f.size + 10)), line, font=f, fill=text_color, anchor=anchor)

def arrow(draw, start, end, color=(45, 108, 223), width=5, label=None, label_font=None):
    draw.line([start, end], fill=color, width=width)
    import math
    angle = math.atan2(end[1] - start[1], end[0] - start[0])
    length = 18
    left = (end[0] - length * math.cos(angle - math.pi / 6), end[1] - length * math.sin(angle - math.pi / 6))
    right = (end[0] - length * math.cos(angle + math.pi / 6), end[1] - length * math.sin(angle + math.pi / 6))
    draw.polygon([end, left, right], fill=color)
    if label:
        mx = (start[0] + end[0]) / 2
        my = (start[1] + end[1]) / 2
        f = label_font or font(22)
        draw.rounded_rectangle((mx - measure(draw, label, f) / 2 - 8, my - f.size / 2 - 6,
                                mx + measure(draw, label, f) / 2 + 8, my + f.size / 2 + 8),
                               radius=8, fill=(255, 255, 255))
        draw.text((mx, my), label, font=f, fill=(80, 90, 105), anchor='mm')

# 1. Architecture diagram
img = Image.new('RGB', (1800, 1100), 'white')
d = ImageDraw.Draw(img)
f_title = font(44, True)
f_box = font(28, True)
f_note = font(23)
d.text((80, 48), '校园网助手整体架构', font=f_title, fill=(25, 44, 72))
top_y = 150
top_boxes = [
    (110, top_y, 560, top_y + 130, 'Windows 网络变化事件\n网络地址或可用性变化时立即排队检查', '#EAF2FF', '#3D78D8'),
    (675, top_y, 1125, top_y + 130, '30 秒兜底定时器\n防止事件丢失，维持周期性检查', '#EAF2FF', '#3D78D8'),
    (1240, top_y, 1690, top_y + 130, '托盘界面与状态窗口\n查看状态、手动检测、开关自动重连', '#EAF2FF', '#3D78D8'),
]
for x1, y1, x2, y2, text, fill, outline in top_boxes:
    draw_box(d, (x1, y1, x2, y2), text, fill, outline, f_note)
for sx in (335, 900, 1465):
    arrow(d, (sx, top_y + 130), (900, 390), color=(61, 120, 216), width=4)
draw_box(d, (170, 390, 1630, 620), 'GuardContext 状态机与调度中心\n计划暂停 / 在线 / 有线认证 / Wi-Fi 回退 / 30 秒重试 / 凭据错误 / 人工处理', '#DCEBFF', '#245CB8', font(34, True), radius=30)
bottom = [
    (70, 790, 400, 1000, 'NetworkInspector\n识别真实有线与无线网卡\n过滤 VPN 和虚拟适配器', '#F3F7FD', '#7C93B3'),
    (430, 790, 760, 1000, 'WifiManager\n检查配置、连接 gdei-WiFi-S\n等待获取 IP', '#F3F7FD', '#7C93B3'),
    (790, 790, 1120, 1000, 'PortalAuthenticator\n发现门户、解析表单\n提交认证、解析响应', '#F3F7FD', '#7C93B3'),
    (1150, 790, 1480, 1000, 'BrowserPortalFallback\nChrome / Edge 临时回退\n通过本机浏览器完成提交', '#F3F7FD', '#7C93B3'),
    (1510, 790, 1740, 1000, 'SettingsStore\nLogger / AppPaths\nDPAPI、设置与日志', '#F3F7FD', '#7C93B3'),
]
for x1, y1, x2, y2, text, fill, outline in bottom:
    draw_box(d, (x1, y1, x2, y2), text, fill, outline, font(21, True))
for sx in (235, 595, 955, 1315, 1625):
    arrow(d, (900, 620), (sx, 790), color=(105, 126, 151), width=3)
img.save(OUT / 'architecture.png', quality=95)

# 2. Runtime flow diagram
img = Image.new('RGB', (1900, 1450), 'white')
d = ImageDraw.Draw(img)
d.text((70, 45), '运行状态分流与恢复路径', font=font(44, True), fill=(25, 44, 72))
blue = '#EAF2FF'; blue_line = '#3D78D8'; green = '#EAF7EF'; green_line = '#3B9A5B'; amber = '#FFF4DE'; amber_line = '#D69A2D'; red = '#FDEBEC'; red_line = '#C94B55'
boxf = font(25, True); smallf = font(21)
# left path
draw_box(d, (90, 150, 500, 250), '触发一次检查', blue, blue_line, boxf)
draw_box(d, (90, 310, 500, 430), '计划暂停或手动暂停？', amber, amber_line, boxf)
draw_box(d, (90, 500, 500, 620), '有真实有线网卡且有 IP？', blue, blue_line, boxf)
draw_box(d, (90, 690, 500, 810), '探测互联网\n失败两次才判定断线', blue, blue_line, boxf)
draw_box(d, (90, 890, 500, 1010), '互联网已恢复\n进入在线或 Wi-Fi 在线状态', green, green_line, boxf)
draw_box(d, (90, 1090, 500, 1210), '校园环境判断', amber, amber_line, boxf)
draw_box(d, (90, 1290, 500, 1410), '有线门户认证入口', '#EDF0FF', '#6378D7', boxf)
# right path
draw_box(d, (1380, 310, 1810, 430), '保持暂停\n06:00 或用户恢复后重新检查', amber, amber_line, boxf)
draw_box(d, (1380, 500, 1810, 620), '有线物理断开持续 10 秒？', red, red_line, boxf)
draw_box(d, (1380, 690, 1810, 810), 'Wi-Fi 回退\n软件开关可尝试自动开启', blue, blue_line, boxf)
draw_box(d, (1380, 890, 1810, 1010), '取得 IP 后检查互联网\n必要时认证 Wi-Fi 门户', blue, blue_line, boxf)
draw_box(d, (1380, 1090, 1810, 1210), '凭据错误或需要人工处理\n安全停止自动提交', red, red_line, boxf)
draw_box(d, (1380, 1290, 1810, 1410), '普通失败固定等待 30 秒\n持续重试，无每小时上限', amber, amber_line, boxf)
# arrows left
for y1, y2 in [(250, 310), (430, 500), (620, 690), (810, 890), (1010, 1090), (1210, 1290)]:
    arrow(d, (295, y1), (295, y2), blue_line, 4)
arrow(d, (500, 370), (1380, 370), red_line, 4, '是', smallf)
arrow(d, (500, 560), (1380, 560), red_line, 4, '断开', smallf)
arrow(d, (500, 750), (1380, 750), blue_line, 4, '需要回退', smallf)
arrow(d, (500, 950), (1380, 950), green_line, 4, '仍无法访问', smallf)
arrow(d, (500, 1150), (1380, 1150), red_line, 4, '达到锁定条件', smallf)
arrow(d, (500, 1350), (1380, 1350), amber_line, 4, '普通失败', smallf)
img.save(OUT / 'runtime_flow.png', quality=95)

# 3. Portal auth diagram
img = Image.new('RGB', (1900, 1300), 'white')
d = ImageDraw.Draw(img)
d.text((70, 45), '门户认证流程与安全分支', font=font(44, True), fill=(25, 44, 72))
blue = '#EAF2FF'; blue_line = '#3D78D8'; green = '#EAF7EF'; green_line = '#3B9A5B'; red = '#FDEBEC'; red_line = '#C94B55'; amber = '#FFF4DE'; amber_line = '#D69A2D'
boxf = font(25, True); smallf = font(21)
# main column centers
xs = 90; xe = 640
nodes = [
    ('请求门户页面\n普通浏览器 UA，保持 Cookie', blue, blue_line),
    ('页面和登录表单可用？', amber, amber_line),
    ('解析动态隐藏字段\n保留门户原始字段', blue, blue_line),
    ('填写 userName / userPwd\n密码按 UTF-8 转 Base64', blue, blue_line),
    ('POST /portal/pws?t=li&ifEmailAuth=false', blue, blue_line),
    ('解码响应\nURL 解码与 Base64 解码', blue, blue_line),
    ('errorNumber = 1 或互联网已恢复？', amber, amber_line),
    ('按认证成功处理\n再次验证互联网', green, green_line),
]
y = 150
positions = []
for text, fill, line in nodes:
    draw_box(d, (xs, y, xe, y + 108), text, fill, line, font(22, True))
    positions.append((xs, y, xe, y + 108))
    y += 140
for i in range(len(positions) - 1):
    arrow(d, (365, positions[i][3]), (365, positions[i + 1][1]), blue_line, 4)
# right fallback column
draw_box(d, (820, 290, 1260, 420), '浏览器回退\nChrome / Edge 无头模式\n临时配置，不常驻', '#EDF0FF', '#6378D7', font(24, True))
draw_box(d, (820, 510, 1260, 640), '加载门户并执行门户原生脚本\n调用 f_preLoginForInputPanel\n失败再执行 fetch 提交', '#EDF0FF', '#6378D7', font(22, True))
arrow(d, (640, 344), (820, 355), red_line, 4, '页面为空或表单无法解析', smallf)
arrow(d, (1040, 420), (1040, 510), blue_line, 4)
arrow(d, (820, 575), (640, 575), green_line, 4, '返回响应内容', smallf)
# safety branch
draw_box(d, (1450, 150, 1830, 280), '可见验证码 / 二次认证字段', red, red_line, font(23, True))
draw_box(d, (1450, 350, 1830, 480), '立即停止自动提交\n提示用户处理', red, red_line, font(23, True))
draw_box(d, (1450, 620, 1830, 750), 'errorNumber 63032\n或凭据错误信息', red, red_line, font(23, True))
draw_box(d, (1450, 820, 1830, 950), '锁定自动认证\n直到用户修改账号密码', red, red_line, font(23, True))
draw_box(d, (1450, 1080, 1830, 1210), '其他普通失败\n30 秒后继续重试', amber, amber_line, font(23, True))
arrow(d, (640, 220), (1450, 220), red_line, 4, '检测到', smallf)
arrow(d, (1640, 280), (1640, 350), red_line, 4)
arrow(d, (640, 780), (1450, 685), red_line, 4, '明确错误', smallf)
arrow(d, (1640, 750), (1640, 820), red_line, 4)
arrow(d, (640, 920), (1450, 1145), amber_line, 4, '无法识别或暂态失败', smallf)
img.save(OUT / 'portal_flow.png', quality=95)
print('diagrams created:', ', '.join(str(p) for p in sorted(OUT.glob('*.png'))))
