using System.Windows;
using Wpf.Ui.Controls;

namespace ProtoTestTool
{
    public partial class FluentMessageBox : UiWindow
    {
        public FluentMessageBox(string title, string message)
        {
            InitializeComponent();
            Title = title;
            MessageText.Text = message;
        }

        private void OkButton_Click(object sender, RoutedEventArgs e)
        {
            DialogResult = true;
            Close();
        }

        public static void Show(string title, string message)
        {
            var msgBox = new FluentMessageBox(title, message);
            msgBox.ShowDialog();
        }

        public static void ShowError(string message)
        {
            Show("Error", message);
        }
    }
}
