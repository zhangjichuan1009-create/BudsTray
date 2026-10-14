# Buds Tray

Windows 11 风格的简约蓝牙耳机托盘应用，使用原生 WPF，无 Electron、遥测或后台网页。

支持小米 / Redmi 与万魔两类协议；已真机验证 **Redmi Buds 8 Pro**、**1MORE SonoFlow S**。其他型号可添加并尝试识别，但不保证同品牌所有耳机兼容。

## 下载与安装

前往 [最新发行版](https://github.com/zhangjichuan1009-create/BudsTray/releases/latest)，下载 `BudsTray-Setup-版本号.exe`，双击即开始安装，完成后自动启动到系统托盘。

- 安装到当前用户的 `%LOCALAPPDATA%\Programs\BudsTray`，不请求管理员权限，无需另装 .NET。
- 首次安装前请退出正在运行的便携版；已安装版本升级时会关闭旧实例并覆盖程序，保留配置。
- 未签名的安装包可能触发 Windows SmartScreen 的发布者提示。仅从本仓库下载，并可核对 `SHA256SUMS.txt`；本项目不要求关闭系统安全功能。
- 不想安装可下载 `BudsTray-win-x64.zip`，完整解压后运行 `RedmiBudsTray.exe`。
- 从 Windows“已安装的应用”卸载。保留设备列表、个人偏好和日志；只清理指向该安装副本的自启动任务。
- 自启动默认由应用设置控制，安装不擅自开启；升级后启动应用会修正已有自启动路径。

## 使用

1. 先在 Windows 蓝牙设置中配对耳机。
2. 点击托盘图标，从添加引导中选择已配对设备加入面板。
3. 查看电量、切换降噪 / 通透 / 关闭、调节 Windows 音量。
4. 可加入多个设备；主动切换设备时同步声音输出。
5. 自动识别失败时，在“管理设备”中点击设备名称，选择小米或万魔协议，并配置显示哪些模式按钮。

不使用型号白名单；识别只读取信息，不自动切换耳机设置。未知功能允许手动试用，控制成功以设备状态回读为准。管理页只显示协议，不把已读到的某个声音模式冒充完整功能列表。

支持系统托盘、动画卡片、连接 / 断开、持续控制通道、电量更新、音量调整、低电量提醒、自启动、设备管理与部分协议设置。实际字段因设备和固件而异。

## 构建

需要 Windows、.NET 8 SDK；构建安装程序另外需要 [NSIS 3](https://nsis.sourceforge.io/)。

```powershell
dotnet build src/BudsTray/RedmiBudsTray.csproj -c Release
powershell -ExecutionPolicy Bypass -File scripts/Build.ps1
```

如工具不在 PATH，传入 `-Dotnet` 和 `-MakeNsis` 的可执行文件路径。输出位于 `artifacts/`。发布 `v*` 标签时，GitHub Actions 自动构建安装包和便携包。

安装器支持 `/S` 静默安装。`/NOAUTORUN` 仅供隔离安装测试，跳过启动及停止应用；卸载器同样支持它。默认不会把它用于正式安装。

源码：[src/BudsTray](src/BudsTray)。协议、实现历史和真机验证边界详见 [详细说明](docs/DETAILS.md)、[第三方声明](THIRD_PARTY.md)。

## 许可证

[GPL-3.0-only](LICENSE)。发行二进制时应同时提供相应源码及第三方声明。协议实现参考 TWS-Pods-PC、BudsLink、Gadgetbridge 等开源资料，归属见第三方声明。本项目不是小米或万魔官方软件，商标仅用于兼容性描述。
