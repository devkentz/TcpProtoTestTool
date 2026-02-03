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

        public void SetInterceptors(IReadOnlyList<InterceptorItem> allInterceptors, List<string> activeInterceptors)
        {
            var items = allInterceptors
                .Select(i => new InterceptorItem(i.Name, i.Type) { IsActive = activeInterceptors.Contains(i.Name) })
                .ToList();

            InterceptorList.ItemsSource = items;
        }

        public List<InterceptorItem> GetActiveInterceptors()
        {
            if (InterceptorList.ItemsSource is IEnumerable<InterceptorItem> items)
            {
                return items.Where(i => i.IsActive).ToList();
            }

            return [];
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