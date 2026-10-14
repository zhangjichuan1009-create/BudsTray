# 开源来源与边界

## Windows 默认声音输出接口资料

- 参考：https://github.com/frgnca/AudioDeviceCmdlets/blob/master/SOURCE/IPolicyConfig.cs
- 使用 IPolicyConfig 的公开接口声明资料，独立编写端点选择、角色切换和失败回滚逻辑。该接口并非微软正式支持的稳定公共 API，已在当前 Windows 11 真机回读验证。

## Gadgetbridge（SonoFlow 协议资料）

- 来源：https://codeberg.org/Freeyourgadget/Gadgetbridge/src/branch/master/app/src/main/java/nodomain/freeyourgadget/gadgetbridge/service/devices/onemore_sonoflow
- 上游许可证：AGPL-3.0。
- 参考内容：公开的 SPP 命令字节、响应字段和模式编号；C# 流式解码与连接逻辑独立实现，并以本机 SonoFlow S 抓包核对。未复制上游 Java/Kotlin 源码。
- 已验证：电量、固件、声音模式；LDAC 开关仅读取。

## TWS-Pods-PC

- 项目：https://github.com/Zhaoyi-ya/TWS-Pods-PC
- 核对版本：`3350f424c1ab69c4bc6cff92dd7b603fb0025db2`
- 许可证：GPL-3.0-only
- 复用内容：Xiaomi MMA 帧结构、RFCOMM 服务 UUID/通道 6 回退、早期兼容认证算法、电量和配置项命令。
- 上游状态：协议单元测试完整；Xiaomi 的 Windows 真实 RFCOMM 连接仍标注为待真机验证。

## BatteryMonitor

- 项目：https://github.com/JKWTCN/BatteryMonitor
- 核对版本：`ee8e0631ac48ca7b189e2db20606ac6de92e487c`
- 许可证：MIT
- 参考内容：Windows 上 Xiaomi/Redmi BLE 广播电量解析与设备型号映射。
- 重要信息：其型号表已列出 Redmi Buds 8 Pro 的 V2 标识 `01:41`、`01:42`、`01:43`，Gaming Black 为 `01:44`；该项目说明 Redmi Buds 5 Pro 已验证，其他小米型号多为理论支持。

## BudsLink

- 项目：https://github.com/maniacx/BudsLink
- 核对版本：`b1c9f1006f6b911ce3a378099a9f4f4ebc18eaca`
- 许可证：GPL-3.0
- 复用/参考内容：Redmi Buds 家族功能映射、降噪模式值，以及 Buds 8 Pro 真机实际匹配的固定序列认证实现。
- 重要信息：其 Buds 8 Pro 设备配置目前的展示名仍写作 `Redmi Buds 6 Pro`；功能表因此仍需谨慎。不过认证实现已由本项目在真实 Redmi Buds 8 Pro 上完成逐包验证。

## ToothTray（蓝牙音频拓扑参考）

- 项目：https://github.com/m2jean/ToothTray
- 参考内容：从音频端点的连接器定位蓝牙 KS 过滤器，并发送 Windows `KSPROPSETID_BtAudio` 单次连接/断开请求。本应用以 C# 实现，并按具体蓝牙地址筛选目标。
- 许可证：BSD-2-Clause，完整声明见 `TOOTHTRAY-LICENSE.md`。
- Windows 接口文档：https://learn.microsoft.com/en-us/windows-hardware/drivers/audio/ksproperty-oneshot-reconnect

## 集成范围

万魔线协议常量另参考 Gadgetbridge 的 OneMorePacket/OneMoreSonoFlowProtocol（https://codeberg.org/Freeyourgadget/Gadgetbridge，AGPL-3.0）。本项目只独立实现有界解析器并对 SonoFlow S 抓包核对，不复制其 Java 实现。发行前若进一步引入其实现代码，应重新审核对应许可证义务，不将品牌识别视为型号兼容证明。

本应用只移植完成 MVP 所需的最小协议面，并保留清晰的 UI/控制器/传输/协议分层。BatteryMonitor 的 BLE Provider 很适合作为后续“未建立 RFCOMM 时仍显示广播电量”的补充，但引入完整 Qt/C++ 子系统会明显增大当前原型的体积。
