# 第三方许可目录

**GreenSite 运行时零第三方库、零第三方 NuGet 包**，因此本目录不含任何第三方许可文本。

- 全部业务代码、WPF 界面样式与图标资源均为本项目自写 / 自绘，声明见仓库根目录 `NOTICE`。
- 程序依赖的 .NET 10 / WindowsDesktop 运行时与 WinINET / Crypt32 / Advapi32 等系统 API
  由操作系统或 .NET 框架提供，不随产品分发。
- 构建工具（.NET SDK、Inno Setup、Python + Pillow、Windows SDK 工具）不随产品分发，
  其许可条款不约束本产品的使用与分发。

未来若引入第三方组件，须在此目录放置其**许可全文**，并同步更新 `NOTICE`
与 README 的「许可证」章节。
