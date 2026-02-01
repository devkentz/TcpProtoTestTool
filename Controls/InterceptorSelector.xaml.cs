using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;

namespace ProtoTestTool.Controls
{
    public partial class InterceptorSelector : UserControl
    {
        public event RoutedEventHandler? SelectionChanged;

        public InterceptorSelector()
        {
            InitializeComponent();
        }

        public void SetInterceptors(IEnumerable<string> allInterceptors, IEnumerable<string> activeInterceptors)
        {
            var items = allInterceptors.Select(name => new InterceptorItem 
            { 
                Name = name, 
                IsActive = activeInterceptors.Contains(name) 
            }).ToList();

            InterceptorList.ItemsSource = items;
        }

        public List<string> GetActiveInterceptors()
        {
            if (InterceptorList.ItemsSource is IEnumerable<InterceptorItem> items)
            {
                return items.Where(i => i.IsActive).Select(i => i.Name).ToList();
            }
            return new List<string>();
        }

        private void OnCheckChanged(object sender, RoutedEventArgs e)
        {
            SelectionChanged?.Invoke(this, e);
        }

        public class InterceptorItem
        {
            public string Name { get; set; } = "";
            public bool IsActive { get; set; }
        }
    }
}
