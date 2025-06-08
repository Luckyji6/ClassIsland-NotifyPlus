using System;
using System.Collections.Generic;

namespace ClassIsland.API.Models
{
    /// <summary>
    /// API 文档
    /// </summary>
    /// <remarks>
    /// ClassIsland 排课系统 API 提供以下端点：
    /// 
    /// 1. 课表相关 - /api/schedule
    ///    - GET: 获取当前课表信息
    ///    - POST: 更新或创建课表
    /// 
    /// 2. 时间表相关 - /api/timeLayout
    ///    - GET: 获取所有时间表
    ///    - POST: 更新或创建时间表
    /// 
    /// 3. 科目相关 - /api/subjects
    ///    - GET: 获取所有科目
    ///    - POST: 更新或创建科目
    /// 
    /// 所有请求需要通过以下两种方式之一进行身份验证：
    /// 1. Bearer Token: Authorization: Bearer {token}
    /// 2. 查询参数: ?apiKey={token}
    /// 
    /// 可能的响应状态码：
    /// - 200: 成功
    /// - 400: 请求数据格式错误
    /// - 401: 未授权访问（无效的API密钥）
    /// - 404: 请求的资源不存在
    /// - 405: 不支持的HTTP方法
    /// - 500: 服务器内部错误
    /// - 503: 服务不可用
    /// </remarks>
    public static class ApiDocumentation
    {
        // 仅作为文档用途，无实际代码
    }
    
    /// <summary>
    /// 课表数据传输对象
    /// </summary>
    public class SchedulePlanDTO
    {
        /// <summary>
        /// 课表ID
        /// </summary>
        public string Id { get; set; }
        
        /// <summary>
        /// 课表名称
        /// </summary>
        public string Name { get; set; }
        
        /// <summary>
        /// 周几的课程安排 (0-6，对应周日到周六)
        /// </summary>
        public List<DayScheduleDTO> Days { get; set; } = new List<DayScheduleDTO>();
    }
    
    /// <summary>
    /// 每天的课程安排
    /// </summary>
    public class DayScheduleDTO
    {
        /// <summary>
        /// 星期几 (0-6，对应周日到周六)
        /// </summary>
        public int DayOfWeek { get; set; }
        
        /// <summary>
        /// 当天的课程列表
        /// </summary>
        public List<ClassDTO> Classes { get; set; } = new List<ClassDTO>();
    }
    
    /// <summary>
    /// 单节课的信息
    /// </summary>
    public class ClassDTO
    {
        /// <summary>
        /// 课程在当天的索引位置
        /// </summary>
        public int Index { get; set; }
        
        /// <summary>
        /// 科目ID
        /// </summary>
        public string SubjectId { get; set; }
        
        /// <summary>
        /// 科目名称（冗余字段，方便显示）
        /// </summary>
        public string SubjectName { get; set; }
        
        /// <summary>
        /// 老师名称
        /// </summary>
        public string TeacherName { get; set; }
        
        /// <summary>
        /// 教室
        /// </summary>
        public string Classroom { get; set; }
    }
    
    /// <summary>
    /// 时间表数据传输对象
    /// </summary>
    public class TimeLayoutDTO
    {
        /// <summary>
        /// 时间表名称
        /// </summary>
        public string Name { get; set; }
        
        /// <summary>
        /// 时间表项目列表
        /// </summary>
        public List<TimeLayoutItemDTO> Items { get; set; } = new List<TimeLayoutItemDTO>();
    }
    
    /// <summary>
    /// 时间表项目
    /// </summary>
    public class TimeLayoutItemDTO
    {
        /// <summary>
        /// 项目索引
        /// </summary>
        public int Index { get; set; }
        
        /// <summary>
        /// 开始时间（以秒为单位，例如：08:00:00 = 28800）
        /// </summary>
        public int StartSecond { get; set; }
        
        /// <summary>
        /// 结束时间（以秒为单位）
        /// </summary>
        public int EndSecond { get; set; }
        
        /// <summary>
        /// 名称（例如：第一节课）
        /// </summary>
        public string Name { get; set; }
        
        /// <summary>
        /// 项目类型 (0=课程, 1=休息, 2=午休, 3=其他)
        /// </summary>
        public int Type { get; set; }
    }
    
    /// <summary>
    /// 科目数据传输对象
    /// </summary>
    public class SubjectDTO
    {
        /// <summary>
        /// 科目ID
        /// </summary>
        public string Id { get; set; }
        
        /// <summary>
        /// 科目名称
        /// </summary>
        public string Name { get; set; }
        
        /// <summary>
        /// 科目颜色（Hex格式，例如：#FF5733）
        /// </summary>
        public string Color { get; set; }
        
        /// <summary>
        /// 科目图标（可选）
        /// </summary>
        public string Icon { get; set; }
        
        /// <summary>
        /// 科目描述
        /// </summary>
        public string Description { get; set; }
        
        /// <summary>
        /// 默认教师
        /// </summary>
        public string DefaultTeacher { get; set; }
    }
} 