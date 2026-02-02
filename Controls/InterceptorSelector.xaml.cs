using System.Windows;

namespace ProtoTestTool.Controls
{
    public class InterceptorItem(string name, Type type)
    {
        public string Name { get; set; } = name;
        public bool IsActive { get; set; } = false;

        public Type Type { get; set; } = type;
    }

    public partial class InterceptorSelector
    {
        public event RoutedEventHandler? SelectionChanged;

        public InterceptorSelector()
        {
            InitializeComponent();
        }

        public void SetInterceptors(List<InterceptorItem> allInterceptors, List<string> activeInterceptors)
        {
            foreach (var item in allInterceptors) 
                item.IsActive = activeInterceptors.Contains(item.Name);
            
            InterceptorList.ItemsSource = allInterceptors;
        }

        public List<InterceptorItem> GetActiveInterceptors()
        {
            if (InterceptorList.ItemsSource is IEnumerable<InterceptorItem> items)
            {
                return items.Where(i => i.IsActive).ToList();
            }

            return new List<InterceptorItem>();
        }


        public List<string> GetActiveInterceptorNames()
        {
            if (InterceptorList.ItemsSource is IEnumerable<InterceptorItem> items)
            {
                return items.Where(i => i.IsActive).Select(e => e.Name).ToList();
            }

            return [];
        }

        private void OnCheckChanged(object sender, RoutedEventArgs e)
        {
            SelectionChanged?.Invoke(this, e);
        }
    }
}