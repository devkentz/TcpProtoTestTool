using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using ProtoTestTool.Network;
using Wpf.Ui.Controls;

namespace ProtoTestTool.Controls
{
    public partial class PacketSelector : UserControl, INotifyPropertyChanged
    {
        public class PacketNode
        {
            public string Name { get; set; } = "";
            public PacketConvertor? Packet { get; set; }
            public ObservableCollection<PacketNode> Children { get; set; } = new();
            public bool IsLeaf => Packet != null;
        }

        public static readonly DependencyProperty SelectedPacketProperty =
            DependencyProperty.Register(nameof(SelectedPacket), typeof(PacketConvertor), typeof(PacketSelector), 
                new PropertyMetadata(null, OnSelectedPacketChanged));

        public PacketConvertor? SelectedPacket
        {
            get => (PacketConvertor?)GetValue(SelectedPacketProperty);
            set => SetValue(SelectedPacketProperty, value);
        }

        public static readonly RoutedEvent PacketSelectedEvent = EventManager.RegisterRoutedEvent(
            nameof(PacketSelected), RoutingStrategy.Bubble, typeof(RoutedEventHandler), typeof(PacketSelector));

        public event RoutedEventHandler PacketSelected
        {
            add { AddHandler(PacketSelectedEvent, value); }
            remove { RemoveHandler(PacketSelectedEvent, value); }
        }

        private static void OnSelectedPacketChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            if (d is PacketSelector control && e.NewValue is PacketConvertor packet)
            {
                if (control.SearchBox.Text != packet.Name)
                {
                    control._isUpdatingText = true;
                    control.SearchBox.Text = packet.Name;
                    control._isUpdatingText = false;
                }
                
                control.RaiseEvent(new RoutedEventArgs(PacketSelectedEvent));
            }
        }

        private ObservableCollection<PacketNode> _allNodes = new();
        private ObservableCollection<PacketNode> _filteredNodes = new();
        private bool _isUpdatingText = false;

        public ObservableCollection<PacketNode> FilteredNodes => _filteredNodes;

        public PacketSelector()
        {
            InitializeComponent();
            Loaded += (s, e) => LoadPackets();
        }

        public void LoadPackets()
        {
            var packets = ProtoLoaderManager.Instance.GetIMessages();
            _allNodes.Clear();

            foreach (var packet in packets.OrderBy(p => p.Name)) 
                _allNodes.Add(new PacketNode { Name = packet.Name, Packet = packet });

            Filter("");
        }
        
        // Refresh method to be called when protos are reloaded
        public void Refresh()
        {
             LoadPackets();
        }

        private void Filter(string searchText)
        {
            _filteredNodes.Clear();
            if (string.IsNullOrWhiteSpace(searchText))
            {
                foreach (var node in _allNodes) _filteredNodes.Add(node);
                return;
            }

            var search = searchText.ToLower();

            foreach (var node in _allNodes)
            {
                if (node.Name.ToLower().Contains(search))
                {
                    _filteredNodes.Add(node);
                }
            }
        }

        private void SearchBox_TextChanged(object sender, TextChangedEventArgs e)
        {
            if (_isUpdatingText) return;

            Filter(SearchBox.Text);
            SuggestionsPopup.IsOpen = true;
        }

        private void SearchBox_GotFocus(object sender, RoutedEventArgs e)
        {
            Filter(SearchBox.Text);
            SuggestionsPopup.IsOpen = true;
        }

        private void PacketTree_SelectedItemChanged(object sender, RoutedPropertyChangedEventArgs<object> e)
        {
            // TreeView selection is tricky with hierarchy.
            // We usually handle MouseDoubleClick or similar for selection.
            // But if user clicks once, we might want to select if it's a leaf.
            
            if (e.NewValue is PacketNode node && node.IsLeaf)
            {
                 SelectNode(node);
            }
        }
        
        private void TreeViewItem_MouseDoubleClick(object sender, MouseButtonEventArgs e)
        {
             if (sender is System.Windows.Controls.TreeViewItem item && item.DataContext is PacketNode node && node.IsLeaf)
             {
                 SelectNode(node);
                 e.Handled = true;
             }
        }

        private void TreeViewItem_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Enter && sender is System.Windows.Controls.TreeViewItem item && item.DataContext is PacketNode node && node.IsLeaf)
            {
                SelectNode(node);
                e.Handled = true;
            }
        }

        private void SearchBox_PreviewKeyDown(object sender, KeyEventArgs e)
        {
             if (e.Key == Key.Down)
             {
                 SuggestionsPopup.IsOpen = true;
                 PacketTree.Focus();
             }
        }

        private void SelectNode(PacketNode node)
        {
            _isUpdatingText = true;
            SearchBox.Text = node.Name;
            _isUpdatingText = false;
            
            SelectedPacket = node.Packet;
            SuggestionsPopup.IsOpen = false;
        }

        public event PropertyChangedEventHandler? PropertyChanged;
        protected void OnPropertyChanged([CallerMemberName] string? name = null)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
        }
    }
}
