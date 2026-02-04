using System.Windows;
using Wpf.Ui.Controls;

namespace ProtoTestTool.Views
{
    public partial class FluentConfirmDialog : FluentWindow
    {
        public System.Windows.MessageBoxResult Result { get; private set; } = System.Windows.MessageBoxResult.Cancel;

        public FluentConfirmDialog(string title, string message)
        {
            InitializeComponent();
            Title = title;
            MessageText.Text = message;
        }

        private void YesButton_Click(object sender, RoutedEventArgs e)
        {
            Result = System.Windows.MessageBoxResult.Yes;
            Close();
        }

        private void NoButton_Click(object sender, RoutedEventArgs e)
        {
            Result = System.Windows.MessageBoxResult.No;
            Close();
        }

        private void CancelButton_Click(object sender, RoutedEventArgs e)
        {
            Result = System.Windows.MessageBoxResult.Cancel;
            Close();
        }
    }
}
