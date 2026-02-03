using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using ProtoTestTool.Network;

namespace ProtoTestTool.Controls
{
    public partial class PacketSelector : UserControl, INotifyPropertyChanged
    {
        public record PacketNode(string Name, PacketConvertor? Packet, bool IsLeaf = true)
        {
            public ObservableCollection<PacketNode> Children { get; init; } = [];
        }

        public static readonly DependencyProperty SelectedPacketProperty =
            DependencyProperty.Register(nameof(SelectedPacket), typeof(PacketConvertor), typeof(PacketSelector),
                new PropertyMetadata(null, OnSelectedPacketChanged));

        public PacketConvertor? SelectedPacket
        {
            get => (PacketConvertor?) GetValue(SelectedPacketProperty);
            set => SetValue(SelectedPacketProperty, value);
        }

        public static readonly RoutedEvent PacketSelectedEvent = EventManager.RegisterRoutedEvent(
            nameof(PacketSelected), RoutingStrategy.Bubble, typeof(RoutedEventHandler), typeof(PacketSelector));

        public event RoutedEventHandler PacketSelected
        {
            add => AddHandler(PacketSelectedEvent, value);
            remove => RemoveHandler(PacketSelectedEvent, value);
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

        private readonly ObservableCollection<PacketNode> _allNodes = [];
        private readonly ICollectionView _nodesView;
        private bool _isUpdatingText;
        private IReadOnlyList<Type> _sendPackets = [];

        public ICollectionView FilteredNodes => _nodesView;

        public PacketSelector()
        {
            InitializeComponent();
            _nodesView = CollectionViewSource.GetDefaultView(_allNodes);
            _nodesView.Filter = FilterPredicate;
            Loaded += (s, e) => LoadPackets();
        }

        public void LoadPackets()
        {
            _allNodes.Clear();

            foreach (var type in _sendPackets) 
                _allNodes.Add(new PacketNode(type.Name, new PacketConvertor { Name = type.Name, Type = type }));
            
            _nodesView.Refresh();
        }

        public void RefreshPackets(IReadOnlyList<Type> types)
        {
            _sendPackets = types;
            LoadPackets();
        }

        private bool FilterPredicate(object obj)
        {
            if (string.IsNullOrWhiteSpace(SearchBox.Text))
                return true;

            if (obj is PacketNode node)
            {
                return node.Name.Contains(SearchBox.Text, StringComparison.OrdinalIgnoreCase);
            }
            return false;
        }

        private void SearchBox_TextChanged(object sender, TextChangedEventArgs e)
        {
            if (_isUpdatingText) return;

            _nodesView.Refresh();
            SuggestionsPopup.IsOpen = true;
        }

        private void SearchBox_GotFocus(object sender, RoutedEventArgs e)
        {
            _nodesView.Refresh();
            SuggestionsPopup.IsOpen = true;
        }

        private void PacketTree_SelectedItemChanged(object sender, RoutedPropertyChangedEventArgs<object> e)
        {
            if (e.NewValue is PacketNode { IsLeaf: true } node)
            {
                SelectNode(node);
            }
        }

        private void TreeViewItem_MouseDoubleClick(object sender, MouseButtonEventArgs e)
        {
            if (sender is TreeViewItem { DataContext: PacketNode { IsLeaf: true } node })
            {
                SelectNode(node);
                e.Handled = true;
            }
        }

        private void TreeViewItem_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Enter && sender is TreeViewItem { DataContext: PacketNode { IsLeaf: true } node })
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