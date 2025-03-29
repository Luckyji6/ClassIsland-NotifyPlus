# ClassIsland-NotifyPlus

让ClassIsland实现远程消息推送的扩展插件。

> 本项目基于[ClassIsland](https://github.com/ClassIsland)开发，感谢原作者的优秀工作！

## 🌟 主要特性

- **远程消息推送**: 支持从其他设备向Windows电脑发送通知消息
- **WebMessage服务**: 提供Web接口，支持HTTP请求发送消息
- **跨平台支持**: 任何支持HTTP的设备都可以发送消息
- **自定义通知**: 支持自定义消息内容、样式和显示时间
- **原版功能**: 保留ClassIsland的所有原有功能

## 📦 安装说明

1. 从Release页面下载最新版本
2. 解压到任意目录
3. 运行`ClassIsland.exe`
4. 首次运行时请以管理员身份运行`network_fix.bat`配置网络权限

## 🔧 配置说明

### 网络配置
1. 运行`network_fix.bat`（需要管理员权限）
2. 确保Windows防火墙允许ClassIsland的网络访问
3. 查看程序显示的本地IP地址，用于远程访问

### 远程消息发送
- 网页地址地址：`http://<本机IP>:8088`
注意：本网站只能在内网访问
- ClassIsland 目前支持以下 Web API 请求：

1. **发送自定义消息**
   - **端点**: `http://[IP地址]:8088/api/message` 或直接 `http://[IP地址]:8088/`
   - **方法**: POST
   - **内容类型**: application/json
   - **请求体格式**:
     ```json
     {
       "message": "要显示的消息内容",
       "speech": true,  // 可选，是否语音朗读
       "duration": 10   // 可选，显示时长(秒)
     }
     ```
   - **响应**:
     ```json
     {
       "success": true|false,
       "error": "错误信息"    // 如果出错才会有
     }
     ```

2. **获取课表信息**
   - **端点**: `http://[IP地址]:8088/api/schedule`
   - **方法**: GET
   - **响应**:
     ```json
     {
       "classes": [
         {
           "startTime": "开始时间 (hh:mm)",
           "endTime": "结束时间 (hh:mm)",
           "subject": "课程名称",
           "isCurrent": true|false  // 是否为当前课程
         },
         // 可能包含下一节课
       ]
     }
     ```
     或者出错时:
     ```json
     {
       "error": "错误信息"
     }

## ❓ 常见问题

### 1. 无法从其他设备访问
- 确认已以管理员身份运行`network_fix.bat`
- 检查Windows防火墙设置
- 确保在同一局域网内
- 验证使用的是正确的本机IP地址

### 2. 端口8088被占用
- 使用`netstat -ano | findstr :8088`检查端口占用
- 关闭占用端口的程序
- 或修改配置文件使用其他端口

### 3. 权限不足
- 以管理员身份运行`network_fix.bat`
- 检查URL保留设置：`netsh http show urlacl`
- 必要时重新运行`network_fix.bat`

## 🛠️ 调试工具

我们提供了几个实用的调试工具：

- `network_diagnostic.bat`: 网络配置诊断工具
- `test_connection.bat`: 连接测试工具
- `network_fix.bat`: 网络配置修复工具

## 📝 更新日志

### v1.0.0
- 添加远程消息推送功能
- 提供Web消息接口
- 添加网络配置工具

## 🙏 致谢

- [ClassIsland](https://github.com/ClassIsland) - 原版项目
- [MaterialDesignInXamlToolkit](https://github.com/MaterialDesignInXAML/MaterialDesignInXamlToolkit) - UI框架

## 📄 许可证

本项目基于GNU General Public License v3.0 (GPL-3.0)开源许可证。这意味着您可以：
- 使用、修改和分发本项目
- 将本项目用于商业用途

但您必须：
- 保持源代码开放
- 在您的项目中使用相同的GPL-3.0许可证
- 标注原始版权和许可证信息

详见[LICENSE](LICENSE)文件。