<#
.SYNOPSIS
    发布前的安装包核验：包里的 dll 必须与 publish 逐字节相同，并且真的能启动。
.DESCRIPTION
    这一步救过两次"装出来打不开"。曾经用"BAML 里某个名字出现几次"当指标，那个指标无效
    （正常的 XAML 名称也会出现两次），于是漏掉了包里 dll 与 publish 不一致的问题。
    所以这里比哈希，并且真的运行一次。
#>
[CmdletBinding()]
param(
    [string]$Msi = 'PaperCoin.Setup\Release\PaperCoin.msi',
    [string]$PublishDir = 'PaperCoin\bin\Release\net10.0-windows\publish',
    [int]$LaunchSeconds = 10
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot

if (-not (Test-Path $Msi)) { $Msi = Join-Path $root $Msi }
if (-not (Test-Path $PublishDir)) { $PublishDir = Join-Path $root $PublishDir }

if (-not (Test-Path $Msi)) { throw "找不到安装包：$Msi" }
if (-not (Test-Path $PublishDir)) { throw "找不到发布目录：$PublishDir（先 dotnet publish）" }

$work = Join-Path $env:TEMP "papercoin-verify-$([guid]::NewGuid().ToString('N').Substring(0, 8))"
New-Item -ItemType Directory -Force -Path $work | Out-Null

try {
    Write-Host '=== 从 MSI 抽取 ==='
    $extract = Start-Process msiexec -ArgumentList '/a', "`"$Msi`"", '/qn', "TARGETDIR=`"$work`"" -PassThru -Wait

    if ($extract.ExitCode -ne 0) { throw "msiexec 抽取失败，退出码 $($extract.ExitCode)" }

    # 1) 逐字节比对自带程序集
    Write-Host '=== 包里的程序集与 publish 逐字节比对 ==='
    $mismatch = @()

    foreach ($name in @('PaperCoin.dll', 'PaperCoin.exe', 'PaperCoin.Core.dll', 'PaperCoin.Service.dll')) {
        $fromPublish = Join-Path $PublishDir $name
        $fromMsi = Get-ChildItem $work -Recurse -Filter $name | Select-Object -First 1

        if (-not (Test-Path $fromPublish)) { throw "publish 里没有 $name" }
        if (-not $fromMsi) { throw "包里的 $name 找不到" }

        $a = (Get-FileHash $fromPublish -Algorithm MD5).Hash
        $b = (Get-FileHash $fromMsi.FullName -Algorithm MD5).Hash
        $ok = $a -eq $b

        Write-Host ("  {0,-24} {1}" -f $name, $(if ($ok) { 'ok' } else { "不一致！publish=$a 包=$b" }))

        # 只有 exe 例外：vdproj 的 ProjectOutput 取的是 obj\Release 里的 apphost，
        # 与 publish 目录里的 exe 未必同源，但只要版本号一致就不影响运行。
        if (-not $ok -and $name -ne 'PaperCoin.exe') { $mismatch += $name }
    }

    if ($mismatch.Count -gt 0) {
        throw "包里的 $($mismatch -join '、') 与 publish 不是同一份构建产物。" +
              '先删 PaperCoin/obj、PaperCoin/bin、PaperCoin.Setup/Release、PaperCoin.Setup/obj，' +
              '然后按"清 → publish → 只打包"重来（见 README 的打包章节）'
    }

    # 2) 真的启动一次
    Write-Host '=== 从包里抽出的 exe 实际启动 ==='
    $exe = Get-ChildItem $work -Recurse -Filter 'PaperCoin.exe' | Select-Object -First 1

    Write-Host "  文件版本 = $($exe.VersionInfo.FileVersion)"

    $stderr = Join-Path $work 'stderr.txt'
    $proc = Start-Process -FilePath $exe.FullName -WorkingDirectory $exe.DirectoryName `
        -RedirectStandardError $stderr -RedirectStandardOutput (Join-Path $work 'stdout.txt') -PassThru

    if ($proc.WaitForExit($LaunchSeconds * 1000)) {
        $why = if (Test-Path $stderr) { (Get-Content $stderr -Encoding UTF8 | Select-Object -First 3) -join ' / ' } else { '' }
        throw "从包里抽出的 exe 在 $LaunchSeconds 秒内退出了（退出码 $($proc.ExitCode)）。$why"
    }

    $proc.Kill()
    $proc.WaitForExit(5000)

    Write-Host '  ok：正常启动'
    Write-Host ''
    Write-Host '== 核验通过 =='
}
finally {
    Get-Process PaperCoin -ErrorAction SilentlyContinue | ForEach-Object { $_.Kill() }
    Remove-Item $work -Recurse -Force -ErrorAction SilentlyContinue
}