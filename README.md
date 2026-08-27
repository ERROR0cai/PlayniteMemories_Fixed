
基于John Wuller的[SharpMemories](https://github.com/2br-2b/PlayniteMemories)：

- 进行了字符串本地化
- 可自定义重命名（可用变量: {{game}} 游戏名, {{date}} 日期, {{time}} 时间, {{datetime}} 日期时间, {{original}} 原名）
- 可选调用API刷新[screenshotsvisualizer_Fixed](https://github.com/ERROR0cai/screenshotsvisualizer_Fixed)

---

虽然[SharpMemories](https://github.com/2br-2b/PlayniteMemories)原版貌似也有调用API刷新，但是我测试下了没效果，好像是[ScreenshotsVisualizer](https://github.com/Lacro59/playnite-screenshotsvisualizer-plugin)不支持（大概）。由于[ScreenshotsVisualizer](https://github.com/Lacro59/playnite-screenshotsvisualizer-plugin)插件默认关闭游戏后自动刷新，并不是截图后立刻刷新，所以我在[ScreenshotsVisualizer](https://github.com/Lacro59/playnite-screenshotsvisualizer-plugin)原版基础上加入了API变成[screenshotsvisualizer_Fixed](https://github.com/ERROR0cai/screenshotsvisualizer_Fixed)，顺便对[SharpMemories](https://github.com/2br-2b/PlayniteMemories)修改了API调用变成[PlayniteMemories_Fixed](https://github.com/ERROR0cai/PlayniteMemories_Fixed)（本插件）
