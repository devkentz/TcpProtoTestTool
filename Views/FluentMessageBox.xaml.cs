using System.Windows;
using Wpf.Ui.Controls;

namespace ProtoTestTool.Views
{
    public partial class FluentMessageBox : FluentWindow
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

        /// <summary>
        /// Shows a Fluent-styled confirmation dialog with Yes/No/Cancel.
        /// Returns MessageBoxResult.Yes, No, or Cancel.
        /// </summary>
        public static System.Windows.MessageBoxResult ShowConfirm(string title, string message)
        {
            var dialog = new FluentConfirmDialog(title, message);
            dialog.ShowDialog();
            return dialog.Result;
        }
    }
}
