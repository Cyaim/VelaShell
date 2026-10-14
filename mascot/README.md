# VelaShell 看板娘

VelaShell 的卡通形象素材。拆分图由原插画裁切而来，`cat-emblem.svg` 按设定手绘矢量。

| 文件 | 内容 |
| --- | --- |
| `character-sheet.png` | 设定图：正面 / 侧面 / 背面三视图、配饰细节、配色、表情、Q 版 |
| `key-visual.png` | 主视觉海报（1254²），带 Logo 与标语 |
| `chibi.png` | 三连 Q 版原图（1983×793） |
| `chibi-hug.png` · `chibi-laptop.png` · `chibi-wave.png` | 从 `chibi.png` 拆出的单人：抱黑猫 / 抱笔记本 / 伸手招呼 |
| `cat-emblem.svg` | 黑猫玩偶矢量徽标（带 `>_` 发卡与铃铛），适合小尺寸与单色场景 |

插画都是白底，不是透明底。放到浅色背景上用 `mix-blend-mode: multiply` 就能融进去；
背景颜色要浅，否则白发和皮肤会被染色。

## 形象设定

照着这份设定出新图，才能和现有形象保持一致。

- **整体**：猫耳少女，Q 版 / 萌系日式插画，线条干净，柔和赛璐璐上色，白底
- **头发**：银白色长卷发，头顶一根呆毛
- **猫耳**：浅灰色外耳、粉色内耳、白色绒毛
- **眼睛**：青绿色，高光明显；开口笑时露出一颗小虎牙
- **左侧发饰**：青绿圆角方形 `>_` 终端图标发卡，外加一枚黑白条纹小发夹
- **右侧发饰**：黑猫头发卡、金色铃铛，搭配黑色与青绿双层蝴蝶结
- **颈部**：黑色项圈，挂金色铃铛
- **服装**：黑色连帽宽松外套（青绿帽里、青绿袖口、青绿织带与黑色搭扣），
  背后印 `>_` 图标与 “VelaShell” 字样；内搭白色连帽卫衣，下身黑色百褶短裙
- **腿部**：一侧黑色过膝袜（膝上印小猫脸），白色堆堆袜，黑白青三色运动鞋，鞋底有猫爪印
- **尾巴**：白色蓬松猫尾，末端系黑 / 青绿蝴蝶结与金铃铛
- **随身物**：黑色猫咪玩偶（青绿色眼睛与 `ω` 嘴）
- **配色**：青绿 `#19C39A` 系、墨黑、纯白、浅灰、薄荷绿、淡粉，点缀铃铛金
- **常用点缀**：猫爪印、金色四角星芒、粉色爱心、`>_` 图标、手写批注
- **表情集**：开心、兴奋、默认、好奇、生气、害羞

## 出图提示词

先写下面这段通用前缀，再接一条具体场景。出图时把 `character-sheet.png` 作为参考图一起给，一致性会好很多。

```text
chibi anime cat girl mascot, silver-white long wavy hair with ahoge, grey cat ears with pink inner ears,
teal eyes, small fang, teal rounded-square ">_" terminal icon hair clip on the left,
black cat hair clip with gold bell and black-teal ribbon on the right, black choker with gold bell,
oversized black hooded jacket with teal cuffs and teal straps, white hoodie, black pleated skirt,
one black thigh-high sock, white leg warmers, black-white-teal sneakers with paw prints,
fluffy white cat tail with black ribbon and gold bell, clean line art, soft cel shading,
pastel palette of teal, black, white, mint and pink, pure white background
```

```text
这是软件的 Logo。
现在开发者期望你能生成一张日式二次元可爱风吉祥物。
元素特征是 猫娘。
整体是可爱、明亮、治愈、活泼的主题。
服装配色可以参考软件 Logo 配色。
```

| 用途 | 场景 |
| --- | --- |
| 贴纸 · 点赞 | `giving a thumbs up and winking, sparkles around, sticker style with white outline` |
| 贴纸 · 调试 | `confused face staring at a laptop showing red error text, question marks above head` |
| 贴纸 · 睡着 | `sleeping on a keyboard hugging the black cat plush, "zzz" bubbles` |
| 贴纸 · 连接成功 | `cheering with both arms up, speech bubble with a green check mark` |
| 贴纸 · 断线 | `teary eyes holding an unplugged network cable, small storm cloud` |
| 发版庆祝 | `popping a party popper with confetti, a teal banner reading "New Release"` |
| 空状态插画 | `peeking out from behind an empty folder, curious expression, wide composition` |
| 加载中 | `running in place with the cat plush, motion lines, simple loop-friendly pose` |
| 桌面壁纸 | `sitting on a crescent moon above a night city made of terminal windows, 16:9, detailed background` |
| 设置页插画 | `holding a giant gear and a wrench, determined expression` |
