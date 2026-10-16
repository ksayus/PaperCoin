# 反证脚本：逐个把实现改回错的，确认对应断言真的会失败。
# 每条都自动还原；任何一步没能还原成功就直接抛。
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
Set-Location $root

$smokeLog = Join-Path $env:TEMP 'refute.log'

# 每项: 名称 / 文件 / 原串 / 替换串 / 期望失败的测试项号
$cases = @(
    @{
        Name = '分类汇总不排除收入'
        File = 'PaperCoin.Service\BillRepository.cs'
        From = '.Where(e => e.Amount < 0)
                .GroupBy(e => Core.CategoryPalette.Normalize(e.Category)'
        To   = '.Where(e => true)
                .GroupBy(e => Core.CategoryPalette.Normalize(e.Category)'
        Test = '21.'
    },
    @{
        Name = 'CSV 不带 UTF-8 BOM'
        File = 'PaperCoin\ViewModels\StatisticsViewModel.cs'
        From = 'new UTF8Encoding(encoderShouldEmitUTF8Identifier: true)'
        To   = 'new UTF8Encoding(encoderShouldEmitUTF8Identifier: false)'
        Test = '25.'
    },
    @{
        Name = '圆环按平均角度分'
        File = 'PaperCoin\Controls\DonutChart.cs'
        From = 'double sweep = Math.Max(MinSweep, segment.Fraction / totalFraction * (Math.PI * 2));'
        To   = 'double sweep = totalFraction == 0 ? 0 : totalFraction / totalFraction * (Math.PI * 2) / segments.Count;'
        Test = '23.'
    },
    @{
        Name = '分类颜色用 GetHashCode'
        File = 'PaperCoin.Core\CategoryPalette.cs'
        From = '        return (int)(hash % SlotCount);'
        To   = '        return Math.Abs(category.Trim().ToLowerInvariant().GetHashCode()) % SlotCount;'
        Test = '20.'
    },    @{
        Name = '圆环不做布局（改回 FrameworkElement 挂可视子元素）'
        File = 'PaperCoin\Controls\DonutChart.cs'
        From = '        return base.ArrangeOverride(finalSize);'
        To   = '        return finalSize;'
        Test = '23.'
    },
    @{
        Name = '指示器漏掉统计项（switch 默认分支兜底）'
        File = 'PaperCoin\MainWindow.xaml.cs'
        From = '            NavItem.Statistics => StatisticsBtn,'
        To   = '            NavItem.Statistics => SettingsBtn,'
        Test = '3.'
    },    @{
        Name = '风格不影响调色板（Compose 忽略风格）'
        File = 'PaperCoin.Service\ThemeManager.cs'
        From = '            StyleWindows => brightness + StyleWindows,
            StyleLiquid => brightness + StyleLiquid,'
        To   = '            StyleWindows => brightness,
            StyleLiquid => brightness,'
        Test = '8.'
    },
    @{
        Name = '窗口背景改成不透明（盖住 DWM 背板）'
        File = 'PaperCoin\Themes\DarkLiquid.xaml'
        From = '    <SolidColorBrush x:Key="ColorGlassBackdrop" Color="Transparent"/>'
        To   = '    <SolidColorBrush x:Key="ColorGlassBackdrop" Color="#FF14161C"/>'
        Test = '1.'
    },
    @{
        Name = '液态玻璃仍走分层窗口（DWM 背板失效）'
        File = 'PaperCoin.Service\ThemeManager.cs'
        From = '            return NormalizeStyle(config.App.ThemeStyle) != StyleLiquid;'
        To   = '            return true;'
        Test = '28.'
    },
    @{
        Name = '玻璃明度层被拿掉（模糊太亮时白字读不清）'
        File = 'PaperCoin\Themes\DarkLiquid.xaml'
        From = '    <SolidColorBrush x:Key="ColorGlassScrim" Color="#E6101014"/>'
        To   = '    <SolidColorBrush x:Key="ColorGlassScrim" Color="Transparent"/>'
        Test = '1.'
    },

    @{
        Name = '日历页删除不落盘（界面删了磁盘还在）'
        File = 'PaperCoin\ViewModels\CalenderBillViewModel.cs'
        From = '        bool ok = await _repository.DeleteEntryAsync(entry.Date, entry.Id);'
        To   = '        bool ok = true; await Task.CompletedTask;'
        Test = '31.'
    },
    @{
        Name = '日历页新增的默认日期不看粒度（总用定位日）'
        File = 'PaperCoin\ViewModels\CalenderBillViewModel.cs'
        From = '            if (Mode == CalendarViewMode.Day) return _anchor;'
        To   = '            return _anchor; // 注入：不看粒度，三种视图都用定位日'
        Test = '31.'
    },
    @{
        # 注入成"模板内重名"而不是"字段重名"：后者编译器会直接报 CS0102（构建失败就会被
        # 上面的构建检查拦下，测不到第 30 项）。模板里的名字不进类字段，能编过，
        # 但同属一个 XAML 名字范围，加载时同样会抛 XamlParseException。
        Name = '同一个名字范围内出现重名（启动即崩）'
        File = 'PaperCoin\Views\CalenderBill.xaml'
        From = '                                                    <TranslateTransform x:Name="EntryPressShift" X="0" Y="0"/>'
        To   = '                                                    <TranslateTransform x:Name="EntryScale" X="0" Y="0"/>'
        Test = '30.'
    },
    @{
        Name = '下拉框绑回 OneWay（选了没反应）'
        File = 'PaperCoin\Views\Statistics.xaml'
        From = 'SelectedItem="{Binding SelectedCategory, Mode=TwoWay}"'
        To   = 'SelectedItem="{Binding SelectedCategory, Mode=OneWay}"'
        Test = '29.'
    },
    @{
        Name = '展开/收起后不重播排行条（颜色没了）'
        File = 'PaperCoin\Views\Statistics.xaml.cs'
        From = '            case nameof(StatisticsViewModel.IsRankExpanded):
                Dispatcher.BeginInvoke(new Action(AnimateRankBars), DispatcherPriority.Loaded);
                break;'
        To   = '            case nameof(StatisticsViewModel.IsRankExpanded):
                break;'
        Test = '29.'
    },
    @{
        Name = '下拉框选中项文本不给前景色'
        File = 'PaperCoin\Themes\Controls.xaml'
        From = '                                              TextElement.Foreground="{DynamicResource ColorTextTitle}"/>'
        To   = '                                              />'
        Test = '24.'
    },    @{
        Name = '统计页不重播展开动画（Reveal 直接置 true）'
        File = 'PaperCoin\Views\Statistics.xaml.cs'
        From = '        CategoryDonut.Played = false;
        CategoryDonut.Played = true;'
        To   = '        CategoryDonut.Played = true;'
        Test = '24.'
    },
    @{
        Name = '年视图整片入场不按当前粒度判断'
        File = 'PaperCoin\Views\CalenderBill.xaml.cs'
        From = '        if (Vm?.IsYearMode != true) return;'
        To   = '        if (Vm is null) return;'
        Test = '26.'
    },
    @{
        Name = '今日页进场不复位空提示标志'
        File = 'PaperCoin\Views\TodayBill.xaml.cs'
        From = '        _cardEntrance.Reset();
        _emptyHintShown = false;'
        To   = '        _cardEntrance.Reset();'
        Test = '26.'
    }
)

function Invoke-Smoke([string]$label) {
    if (Test-Path $smokeLog) { Remove-Item $smokeLog -Force }

    $proc = Start-Process -FilePath 'dotnet' `
        -ArgumentList 'run', '--no-build', '--project', 'tools\UiSmokeTest\UiSmokeTest.csproj' `
        -RedirectStandardOutput $smokeLog -PassThru -NoNewWindow

    if (-not $proc.WaitForExit(240000)) { $proc.Kill(); throw "$label 超时" }

    return Get-Content $smokeLog -Encoding UTF8
}

Write-Host '=== 基线：当前实现必须全部通过 ==='
$baseline = Invoke-Smoke 'baseline'
if (-not ($baseline | Select-String '== 全部通过 ==')) {
    $baseline | Select-String '结果: 失败' -Context 0, 2 | ForEach-Object { $_.Line + ' ' + $_.Context.PostContext }
    throw '基线没有全绿，反证无从谈起'
}
Write-Host '  基线全绿。'

$failed = @()

foreach ($case in $cases) {
    $path = $case.File
    $original = [System.IO.File]::ReadAllText($path, [System.Text.Encoding]::UTF8)

    if (-not $original.Contains($case.From)) {
        Write-Host "[$($case.Name)] 找不到要替换的代码，跳过（锚点已过时）"
        $failed += $case.Name
        continue
    }

    [System.IO.File]::WriteAllText($path, $original.Replace($case.From, $case.To),
        (New-Object System.Text.UTF8Encoding($false)))

    try {
        Write-Host "[$($case.Name)] 已注入错误，重建并跑测试…"

        # 构建必须先成功。注入的代码若编不过（锚点改了但 To 里有语法错、枚举名写错等），
        # 下面那次 --no-build 会拿**上一次的二进制**去跑，于是"没有失败"，
        # 看起来像断言是摆设 —— 实际是这条反证根本没跑起来。
        $psi = New-Object System.Diagnostics.ProcessStartInfo
        $psi.FileName = 'dotnet'
        $psi.Arguments = 'build tools\UiSmokeTest\UiSmokeTest.csproj -m:1 -v:q --nologo'
        $psi.UseShellExecute = $false
        $psi.RedirectStandardOutput = $true
        $psi.RedirectStandardError = $true
        $psi.WorkingDirectory = (Get-Location).Path

        $buildProc = [System.Diagnostics.Process]::Start($psi)
        $buildText = $buildProc.StandardOutput.ReadToEnd()
        $buildText += $buildProc.StandardError.ReadToEnd()
        $buildProc.WaitForExit(240000) | Out-Null

        if ($buildProc.ExitCode -ne 0) {
            Write-Host "  ✘ 注入的代码编不过（退出码 $($buildProc.ExitCode)），这条反证没跑起来 —— 锚点或替换内容有问题"
            ($buildText -split "`r?`n") | Select-String 'error' | Select-Object -First 3 |
                ForEach-Object { '      ' + $_.Line.Trim() }
            $failed += $case.Name
            continue
        }

        $output = Invoke-Smoke $case.Name

        $hit = $output | Select-String -Pattern "^\[测试\] $([regex]::Escape($case.Test))" -Context 0, 40
        $line = ($hit | Select-Object -First 1)
        $block = if ($line) { ($line.Line + "`n" + ($line.Context.PostContext -join "`n")) } else { '' }
        $isFail = $block -match '结果: 失败'

        if ($isFail) {
            $reason = ($block -split "`n" | Select-String '结果: 失败' | Select-Object -First 1).Line.Trim()
            Write-Host "  ✔ 第 $($case.Test) 项如实失败：$reason"
        } else {
            Write-Host "  ✘ 第 $($case.Test) 项没有失败 —— 这条断言是摆设"
            $failed += $case.Name
        }
    }
    finally {
        [System.IO.File]::WriteAllText($path, $original, [System.Text.Encoding]::UTF8)
        Write-Host "  已还原 $path"
    }
}

Write-Host ''
Write-Host '=== 还原后复跑 ==='
dotnet build tools\UiSmokeTest\UiSmokeTest.csproj -m:1 -v:q --nologo 2>&1 | Select-String 'error' | Select-Object -First 5
$final = Invoke-Smoke 'final'
if ($final | Select-String '== 全部通过 ==') {
    Write-Host '  还原成功，全部通过。'
} else {
    Write-Host '  ✘ 还原后没有全绿，工作区可能已损坏！'
    $failed += '还原后未全绿'
}

Write-Host ''
if ($failed.Count -eq 0) {
    Write-Host '== 反证全部成立：每一条新断言都能抓住对应的错误实现 =='
} else {
    Write-Host "== 有 $($failed.Count) 条反证未成立: $($failed -join '; ')"
    exit 1
}
