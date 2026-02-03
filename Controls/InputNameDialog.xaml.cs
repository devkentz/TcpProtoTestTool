using System.Windows;

namespace ProtoTestTool.Controls
{
    public partial class InputNameDialog : Window
    {
        public string ResponseText { get; private set; } = string.Empty;

        public InputNameDialog()
        {
            InitializeComponent();
            InputBox.Focus();
        }

        private void Ok_Click(object sender, RoutedEventArgs e)
        {
            ResponseText = InputBox.Text;
            DialogResult = true;
        }

        private void Cancel_Click(object sender, RoutedEventArgs e)
        {
            DialogResult = false;
        }
    }
}
