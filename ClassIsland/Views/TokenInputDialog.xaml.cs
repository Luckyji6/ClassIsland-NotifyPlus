using System.Windows;

namespace ClassIsland.Views
{
    /// <summary>
    /// TokenInputDialog.xaml 的交互逻辑
    /// </summary>
    public partial class TokenInputDialog : Window
    {
        /// <summary>
        /// 获取或设置用户输入的令牌
        /// </summary>
        public string Token { get; private set; } = string.Empty;

        public TokenInputDialog()
        {
            InitializeComponent();
            TokenTextBox.Focus();
        }

        private void ConfirmButton_Click(object sender, RoutedEventArgs e)
        {
            Token = TokenTextBox.Text?.Trim() ?? string.Empty;
            
            if (string.IsNullOrEmpty(Token))
            {
                MessageBox.Show("请输入退出令牌！", "提示", MessageBoxButton.OK, MessageBoxImage.Warning);
                TokenTextBox.Focus();
                return;
            }
            
            DialogResult = true;
            Close();
        }

        private void CancelButton_Click(object sender, RoutedEventArgs e)
        {
            DialogResult = false;
            Close();
        }

        protected override void OnActivated(System.EventArgs e)
        {
            base.OnActivated(e);
            TokenTextBox.Focus();
            TokenTextBox.SelectAll();
        }
    }
} 