基于John Wuller的[SharpMemories](https://github.com/2br-2b/PlayniteMemories)：
- 进行了字符串本地化
- 可自定义重命名（可用变量: {{game}} 游戏名, {{date}} 日期, {{time}} 时间, {{datetime}} 日期时间, {{original}} 原名）
<img width="749" height="298" alt="image" src="https://github.com/user-attachments/assets/1ad255e4-430b-4c03-b540-ebcf4870c367" />

- 可选调用API刷新[screenshotsvisualizer_Fixed](https://github.com/ERROR0cai/screenshotsvisualizer_Fixed)
<img width="941" height="53" alt="image" src="https://github.com/user-attachments/assets/236b3a39-de3a-478d-9aa9-2ae791375d33" />

- 加入通知功能
<img width="397" height="311" alt="image" src="https://github.com/user-attachments/assets/e79c4c60-d09b-47de-9f2f-824be7edc32a" />


---
虽然[SharpMemories](https://github.com/2br-2b/PlayniteMemories)原版貌似也有调用API刷新，但是我测试下了没效果，好像是[ScreenshotsVisualizer](https://github.com/Lacro59/playnite-screenshotsvisualizer-plugin)不支持（大概）。由于[ScreenshotsVisualizer](https://github.com/Lacro59/playnite-screenshotsvisualizer-plugin)插件默认关闭游戏后自动刷新，并不是截图后立刻刷新，所以我在[ScreenshotsVisualizer](https://github.com/Lacro59/playnite-screenshotsvisualizer-plugin)原版基础上加入了API变成[screenshotsvisualizer_Fixed](https://github.com/ERROR0cai/screenshotsvisualizer_Fixed)，顺便对[SharpMemories](https://github.com/2br-2b/PlayniteMemories)修改了API调用变成[PlayniteMemories_Fixed](https://github.com/ERROR0cai/PlayniteMemories_Fixed)（本插件）
