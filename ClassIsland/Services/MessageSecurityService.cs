using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json;
using System.Collections.Generic;

namespace ClassIsland.Services
{
    /// <summary>
    /// 消息安全服务，用于管理Web消息服务器的密钥和消息历史记录
    /// </summary>
    public class MessageSecurityService
    {
        private readonly ILogger<MessageSecurityService> _logger;
        private readonly string _messageFolderPath;
        private readonly string _keyFilePath;
        private string _encryptionKey; // 用于加密密钥文件的密钥
        private string _accessToken; // 访问令牌
        
        public bool IsTokenConfigured { get; private set; }
        
        /// <summary>
        /// 初始化消息安全服务
        /// </summary>
        public MessageSecurityService(ILogger<MessageSecurityService> logger)
        {
            _logger = logger;
            
            // 在应用程序目录下创建message文件夹
            _messageFolderPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "message");
            if (!Directory.Exists(_messageFolderPath))
            {
                Directory.CreateDirectory(_messageFolderPath);
                _logger.LogInformation("已创建消息历史目录: {Path}", _messageFolderPath);
            }
            
            // 密钥文件路径
            _keyFilePath = Path.Combine(_messageFolderPath, "security.dat");
            
            // 生成固定的加密密钥（基于机器信息）
            _encryptionKey = GenerateMachineBasedKey();
            
            // 尝试加载现有的密钥
            LoadToken();
        }
        
        /// <summary>
        /// 基于机器信息生成固定的加密密钥
        /// </summary>
        private string GenerateMachineBasedKey()
        {
            // 使用机器名和处理器ID作为基础生成一个相对固定的密钥
            string machineInfo = Environment.MachineName + Environment.ProcessorCount;
            using (SHA256 sha256 = SHA256.Create())
            {
                byte[] hashBytes = sha256.ComputeHash(Encoding.UTF8.GetBytes(machineInfo));
                return Convert.ToBase64String(hashBytes).Substring(0, 32); // 取32字符作为密钥
            }
        }
        
        /// <summary>
        /// 生成随机访问令牌
        /// </summary>
        public string GenerateToken()
        {
            // 生成6字节的随机数
            byte[] tokenBytes = new byte[6];
            using (var rng = RandomNumberGenerator.Create())
            {
                rng.GetBytes(tokenBytes);
            }
            
            // 转换为易于记忆的格式：XXX-XXX
            string token = BitConverter.ToString(tokenBytes).Replace("-", "");
            token = string.Format("{0}-{1}",
                token.Substring(0, 3),
                token.Substring(3, 3));
            
            return token;
        }
        
        /// <summary>
        /// 设置新的访问令牌
        /// </summary>
        public async Task<bool> SetToken(string token)
        {
            try
            {
                _accessToken = token;
                IsTokenConfigured = true;
                
                // 加密并保存
                await SaveTokenAsync();
                _logger.LogInformation("已设置新的访问令牌");
                return true;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "设置访问令牌时出错");
                return false;
            }
        }
        
        /// <summary>
        /// 验证访问令牌
        /// </summary>
        public bool ValidateToken(string inputToken)
        {
            if (!IsTokenConfigured || string.IsNullOrEmpty(_accessToken))
            {
                _logger.LogWarning("未配置访问令牌，无法验证");
                return false;
            }
            
            // 验证令牌是否匹配
            bool isValid = _accessToken.Equals(inputToken, StringComparison.Ordinal);
            if (!isValid)
            {
                _logger.LogWarning("访问令牌验证失败");
            }
            
            return isValid;
        }
        
        /// <summary>
        /// 加载保存的令牌
        /// </summary>
        private void LoadToken()
        {
            try
            {
                if (File.Exists(_keyFilePath))
                {
                    byte[] encryptedData = File.ReadAllBytes(_keyFilePath);
                    string decryptedJson = Decrypt(encryptedData, _encryptionKey);
                    
                    var tokenData = JsonConvert.DeserializeObject<Dictionary<string, string>>(decryptedJson);
                    if (tokenData != null && tokenData.ContainsKey("token"))
                    {
                        _accessToken = tokenData["token"];
                        IsTokenConfigured = !string.IsNullOrEmpty(_accessToken);
                        _logger.LogInformation("成功加载访问令牌");
                    }
                }
                else
                {
                    IsTokenConfigured = false;
                    _logger.LogInformation("未找到访问令牌配置文件");
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "加载访问令牌时出错");
                IsTokenConfigured = false;
            }
        }
        
        /// <summary>
        /// 保存当前令牌
        /// </summary>
        private async Task SaveTokenAsync()
        {
            try
            {
                var tokenData = new Dictionary<string, string>
                {
                    { "token", _accessToken }
                };
                
                string json = JsonConvert.SerializeObject(tokenData);
                byte[] encryptedData = Encrypt(json, _encryptionKey);
                
                await File.WriteAllBytesAsync(_keyFilePath, encryptedData);
                _logger.LogInformation("成功保存访问令牌");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "保存访问令牌时出错");
                throw;
            }
        }
        
        /// <summary>
        /// 加密文本
        /// </summary>
        private byte[] Encrypt(string plainText, string key)
        {
            byte[] iv = new byte[16];
            byte[] array;
            
            using (Aes aes = Aes.Create())
            {
                aes.Key = Encoding.UTF8.GetBytes(key);
                aes.IV = iv;
                
                ICryptoTransform encryptor = aes.CreateEncryptor(aes.Key, aes.IV);
                
                using (MemoryStream memoryStream = new MemoryStream())
                {
                    using (CryptoStream cryptoStream = new CryptoStream(memoryStream, encryptor, CryptoStreamMode.Write))
                    {
                        using (StreamWriter streamWriter = new StreamWriter(cryptoStream))
                        {
                            streamWriter.Write(plainText);
                        }
                        
                        array = memoryStream.ToArray();
                    }
                }
            }
            
            return array;
        }
        
        /// <summary>
        /// 解密文本
        /// </summary>
        private string Decrypt(byte[] cipherText, string key)
        {
            byte[] iv = new byte[16];
            
            using (Aes aes = Aes.Create())
            {
                aes.Key = Encoding.UTF8.GetBytes(key);
                aes.IV = iv;
                
                ICryptoTransform decryptor = aes.CreateDecryptor(aes.Key, aes.IV);
                
                using (MemoryStream memoryStream = new MemoryStream(cipherText))
                {
                    using (CryptoStream cryptoStream = new CryptoStream(memoryStream, decryptor, CryptoStreamMode.Read))
                    {
                        using (StreamReader streamReader = new StreamReader(cryptoStream))
                        {
                            return streamReader.ReadToEnd();
                        }
                    }
                }
            }
        }
        
        /// <summary>
        /// 记录消息历史
        /// </summary>
        public async Task LogMessageHistoryAsync(string message, bool success, string sourceIp)
        {
            try
            {
                var messageLog = new
                {
                    Timestamp = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"),
                    Message = message,
                    Success = success,
                    SourceIp = sourceIp
                };
                
                string json = JsonConvert.SerializeObject(messageLog);
                string logFilePath = Path.Combine(_messageFolderPath, $"message_log_{DateTime.Now:yyyyMMdd}.json");
                
                // 追加到日志文件
                await File.AppendAllTextAsync(logFilePath, json + Environment.NewLine);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "记录消息历史时出错");
            }
        }
    }
} 