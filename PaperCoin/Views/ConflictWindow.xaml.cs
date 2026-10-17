using System.Windows;
using System.Windows.Input;
using PaperCoin.Service.LanSync;
using PaperCoin.ViewModels;

namespace PaperCoin.Views;

/// <summary>
/// 冲突处理窗口。Apply 会先关闭窗口再后台重连，避免重试期间窗口内容被新一轮冲突更新；
/// Cancel、标题栏 X 与 Alt+F4 都走同一套“取消且不写任何数据”的路径。
/// </summary>
public partial class ConflictWindow : Window
{
    private readonly LanSyncService _service;
    private readonly ConflictWindowViewModel _vm;

    public ConflictWindow(LanSyncService service, SyncConflictSession session)
    {
        _service = service;

        InitializeComponent();

        _vm = new ConflictWindowViewModel(service, session);
        DataContext = _vm;

        _vm.RequestClose += OnRequestClose;
        Closed += OnClosed;
    }

    public string PeerId => _vm.PeerId;

    /// <summary>同一台设备的新冲突列表替换当前窗口内容，不重复开窗。</summary>
    public void UpdateSession(SyncConflictSession session)
    {
        _vm.UpdateSession(session);
        Activate();
    }

    private void OnRequestClose()
    {
        _vm.RequestClose -= OnRequestClose;
        Close();
    }

    private void OnClosed(object? sender, EventArgs e)
    {
        _vm.RequestClose -= OnRequestClose;

        // X / Alt+F4 与“取消”等价：协议要求取消不能改变任何一侧的数据。
        if (!_vm.Applied && !_vm.Cancelled)
            _service.CancelConflicts(_vm.PeerId);
    }

    private void TitleBorder_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ClickCount == 1) DragMove();
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();
}
