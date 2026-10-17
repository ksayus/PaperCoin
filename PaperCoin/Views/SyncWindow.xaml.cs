using System.Windows;
using System.Windows.Input;
using PaperCoin.Service.LanSync;
using PaperCoin.ViewModels;

namespace PaperCoin.Views;

/// <summary>局域网同步管理窗口。所有网络操作由 ViewModel 异步执行，窗口只负责生命周期。</summary>
public partial class SyncWindow : Window
{
    private readonly LanSyncViewModel _vm;

    public SyncWindow(LanSyncService service)
    {
        InitializeComponent();

        _vm = new LanSyncViewModel(service);
        DataContext = _vm;

        Closed += OnClosed;
    }

    internal LanSyncViewModel ViewModel => _vm;

    private void OnClosed(object? sender, EventArgs e)
    {
        Closed -= OnClosed;
        _vm.Dispose();
    }

    private void TitleBorder_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ClickCount == 1) DragMove();
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();
}
