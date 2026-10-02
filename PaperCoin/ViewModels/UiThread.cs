using System.Windows;

namespace PaperCoin.ViewModels;

/// <summary>把异步操作编组回 UI 线程；没有 Application（纯单测场景）时直接执行。</summary>
internal static class UiThread
{
    public static Task RunAsync(Func<Task> operation)
    {
        var dispatcher = Application.Current?.Dispatcher;

        if (dispatcher is null || dispatcher.CheckAccess())
            return operation();

        return dispatcher.InvokeAsync(operation).Task.Unwrap();
    }
}
