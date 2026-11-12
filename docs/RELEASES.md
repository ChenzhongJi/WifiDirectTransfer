# 发布记录

正式安装包和 SHA-256 以 GitHub Release 资产为准。下表记录已发布版本、提交和正式包哈希。1.6 起列出的哈希对应安装包 `setup.exe`，便携 zip 的哈希见对应 Release 资产。

| 版本 | 日期 | 提交 | 正式包 SHA-256 |
| --- | --- | --- | --- |
| 1.0.0 | 2026-11-05 | `56f6c75` | `326559a9a6a6f45647647e800810cf05e9ebff64b8513ca68a6145c5a0cbe295` |
| 1.0.1 | 2026-11-05 | `9598fca` | `6c34f2b2b364f19839978515c33d5104c530dcf16ae55c8106224dd27c2ad006` |
| 1.0.2 | 2026-11-09 | `deebe5b` | `bbf050637f5680a0b1905a1f418fc1756b7af79dfa65a5d69bb24a47a22740b9` |
| 1.1.0 | 2026-11-09 | `2917559` | `6537de93cea9b6159633b01256d5583933706cce1bcacaa6c8029cb37e4c26e7` |
| 1.1.1 | 2026-11-09 | `3f131b1` | `20f10e667c46ed5693cb2d0c045c613f488da83649855ab5c296b831c350eaf1` |
| 1.2.0 | 2026-11-09 | `3291134` | `85248d5db52cad6d0b566296f9408472e9b814f01c7756625eab3c560013fa59` |
| 1.3.0 | 2026-11-10 | `8b61e96` | `d5f6080f666773ac00a77b1208d21f9782bb13a4576e27f40e0b6e9251768598` |
| 1.3.1 | 2026-11-10 | `485f972` | `c26f5919fbc1c6ec060893b067af26bdd5859d953364d9089bcc69c6cf4af010` |
| 1.4.0 | 2026-11-10 | `6606c78` | `5df366bafcfe5a511c374362ad4fbeeb4c28e36c4c1d12b624a1fcd3d0c164d6` |
| 1.4.1 | 2026-11-10 | `4a78bb7` | `b822ffc8d1d03848bb4e1291a4951c2d141671b36f0bbd2a7fe55966ac649e31` |
| 1.5.0 | 2026-11-11 | `dc37603` | `aa6592256b45657817194565714de04a220945717bfd75c6d793cb39fed93fde` |
| 1.6.0 | 2026-11-11 | `bb6bf72` | `9f41223a0ca1a94aef38946934df644a7840f1cf083d77b0134ba60e493fb564` |
| 1.6.1 | 2026-11-11 | `cfdc626` | `3fd51913ec8a035e5a7969ad9d408e948eeda91ffce0828cb81bad496487c7e5` |
| 1.6.2 | 2026-11-12 | `a1a39c6` | `76bd29e2135d1cf66ba22001deb58f4f772594bed7fc6791ee7e03f23c4e56dd` |
| 1.6.3 | 2026-11-12 | `c6d8ee8` | `3f2ee69cb45de6a2cf5ddee9b3bd798e10e49f1b8ca6692a62cabaf19c9d164f` |

## Release 页面

- 1.0.0：https://github.com/ChenzhongJi/WifiDirectTransfer/releases/tag/v1.0.0
- 1.0.1：https://github.com/ChenzhongJi/WifiDirectTransfer/releases/tag/v1.0.1
- 1.0.2：https://github.com/ChenzhongJi/WifiDirectTransfer/releases/tag/v1.0.2
- 1.1.0：https://github.com/ChenzhongJi/WifiDirectTransfer/releases/tag/v1.1.0
- 1.1.1：https://github.com/ChenzhongJi/WifiDirectTransfer/releases/tag/v1.1.1
- 1.2.0：https://github.com/ChenzhongJi/WifiDirectTransfer/releases/tag/v1.2.0
- 1.3.0：https://github.com/ChenzhongJi/WifiDirectTransfer/releases/tag/v1.3.0
- 1.3.1：https://github.com/ChenzhongJi/WifiDirectTransfer/releases/tag/v1.3.1
- 1.4.0：https://github.com/ChenzhongJi/WifiDirectTransfer/releases/tag/v1.4.0
- 1.4.1：https://github.com/ChenzhongJi/WifiDirectTransfer/releases/tag/v1.4.1
- 1.5.0：https://github.com/ChenzhongJi/WifiDirectTransfer/releases/tag/v1.5.0
- 1.6.0：https://github.com/ChenzhongJi/WifiDirectTransfer/releases/tag/v1.6.0
- 1.6.1：https://github.com/ChenzhongJi/WifiDirectTransfer/releases/tag/v1.6.1
- 1.6.2：https://github.com/ChenzhongJi/WifiDirectTransfer/releases/tag/v1.6.2
- 1.6.3：https://github.com/ChenzhongJi/WifiDirectTransfer/releases/tag/v1.6.3

## 协议兼容性

- 1.0.x：协议 v3。
- 1.1.x：协议 v4，与 1.0.x 不兼容。
- 1.1.0 与 1.1.1 可互通。
- 1.2.0：协议 v4，与 1.1.x 在 2 GiB 以内互通；超过 2 GiB 需要两端 1.2+。
- 1.3.0：协议 v4，与 1.1.x / 1.2.x 在文件传输方面互通；空文件夹需要两端 1.3+。
- 1.3.1：协议 v4，与 1.3.0 / 1.2.x 文件传输互通；空文件夹需要两端 1.3+。
- 1.4.0：协议 v4，与 1.3.1 / 1.3.0 / 1.2.x 文件传输互通；设备身份和可信设备需要两端 1.4+，旧对端回退到接收确认。
- 1.4.1：协议 v4，文件传输与 1.4.0 / 1.3.x / 1.2.x 互通；仅替换主题化对话框，行为不变。
- 1.5.0：协议 v4，文件传输与 1.4.1 / 1.4.0 / 1.3.x / 1.2.x 互通；窗口传输需要两端 1.5+，旧对端自动回退到逐块停等 ACK。
- 1.6.0 / 1.6.1：协议 v4，文件传输与 1.5.0 及更早版本互通；新增每用户安装包和自动更新，只影响分发方式，不改变传输协议。
- 1.6.2：协议 v4，与 1.6.1 / 1.6.0 / 1.5.0 及更早版本文件传输互通；新增版本显示和托盘模式开机自启，不改变传输协议。
- 1.6.3：协议 v4，与 1.6.2 及更早版本文件传输互通；中文安装器和关闭到托盘只影响分发与窗口行为，不改变传输协议。
- 1.0.x 与 1.1.x / 1.2.0 / 1.3.0 / 1.3.1 / 1.4.0 / 1.4.1 / 1.5.0 不兼容。
