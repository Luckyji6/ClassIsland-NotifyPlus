# ClassIsland 学校排课系统API使用说明

## API概览

ClassIsland现已支持学校排课系统API功能，允许外部系统通过HTTP请求获取和更新课表、时间表和科目信息。

API服务运行在端口**8089**上，使用与WebMessageServer相同的密钥进行身份验证。

## API端点列表

### 1. 课表API

- **获取课表**
  - 请求：`GET http://[IP]:8089/api/schedule`
  - 权限：需要提供有效的API密钥
  - 说明：获取当前配置的所有课表信息

- **更新课表**
  - 请求：`POST http://[IP]:8089/api/schedule`
  - 权限：需要提供有效的API密钥
  - 数据格式：JSON
  - 说明：更新课表信息

### 2. 时间表API

- **获取时间表**
  - 请求：`GET http://[IP]:8089/api/timeLayout`
  - 权限：需要提供有效的API密钥
  - 说明：获取当前配置的所有时间表信息

- **更新时间表**
  - 请求：`POST http://[IP]:8089/api/timeLayout`
  - 权限：需要提供有效的API密钥
  - 数据格式：JSON
  - 说明：更新时间表信息

### 3. 科目API

- **获取科目列表**
  - 请求：`GET http://[IP]:8089/api/subjects`
  - 权限：需要提供有效的API密钥
  - 说明：获取当前配置的所有科目信息

- **更新科目列表**
  - 请求：`POST http://[IP]:8089/api/subjects`
  - 权限：需要提供有效的API密钥
  - 数据格式：JSON
  - 说明：更新科目信息

## 身份验证

所有API请求需要在HTTP请求头中添加`Authorization`字段，格式为：

```
Authorization: Bearer [API密钥]
```

API密钥与WebMessageServer使用的令牌相同，可以在WebMessageServer的设置页面查看和配置。

## 数据格式示例

### 课表数据格式

```json
{
  "id": "default-schedule",
  "name": "默认课表",
  "days": [
    {
      "dayOfWeek": 1,
      "classes": [
        {
          "index": 0,
          "subjectId": "math",
          "subjectName": "数学",
          "teacherName": "张老师",
          "classroom": "101教室"
        },
        {
          "index": 1,
          "subjectId": "chinese",
          "subjectName": "语文",
          "teacherName": "李老师",
          "classroom": "102教室"
        }
      ]
    }
  ]
}
```

### 时间表数据格式

```json
{
  "name": "默认时间表",
  "items": [
    {
      "index": 0,
      "startSecond": 28800,
      "endSecond": 31500,
      "name": "第一节课",
      "type": 0
    },
    {
      "index": 1,
      "startSecond": 31800,
      "endSecond": 34500,
      "name": "第二节课",
      "type": 0
    }
  ]
}
```

### 科目数据格式

```json
[
  {
    "id": "math",
    "name": "数学",
    "color": "#FF5733",
    "icon": "Calculator",
    "description": "数学课程",
    "defaultTeacher": "张老师"
  },
  {
    "id": "chinese",
    "name": "语文",
    "color": "#33FF57",
    "icon": "Book",
    "description": "语文课程",
    "defaultTeacher": "李老师"
  }
]
```

## 注意事项

1. API服务默认在端口8089上运行，确保该端口未被其他应用占用
2. 使用API前，必须先在ClassIsland中配置访问令牌
3. 更新数据时请小心，确保数据格式正确，否则可能导致课表显示异常

## 故障排除

如果无法连接到API服务，请检查：

1. ClassIsland是否正在运行
2. 端口8089是否被其他应用占用
3. 防火墙是否允许端口8089的访问
4. API密钥是否正确

如需更多帮助，请参考`网络连接工具说明.md`文件。 