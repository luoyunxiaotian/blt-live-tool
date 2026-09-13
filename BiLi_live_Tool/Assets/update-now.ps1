# update-now.ps1 — 手动引导更新（用于没有内置更新器的旧版本，或修复安装）
# 用法：把 update-now.cmd 与 update-now.ps1 放到程序目录，双击 .cmd。
$ErrorActionPreference = 'Stop'
$dir = Split-Path -Parent $MyInvocation.MyCommand.Path
Write-Host "安装目录: $dir"
$api = 'https://api.github.com/repos/luoyunxiaotian/blt-live-tool/releases'
$hdr = @{ 'User-Agent' = 'blt-update-bootstrap' }
$rels = Invoke-RestMethod -Uri $api -Headers $hdr -TimeoutSec 60
$rel = $rels | Where-Object { $_.tag_name -like '*-maui' } | Select-Object -First 1
if (-not $rel) { throw '未找到 MAUI 版本发布' }
$asset = $rel.assets | Where-Object { $_.name -like '*.zip' } | Select-Object -First 1
if (-not $asset) { throw '该发布没有 zip 资产' }
Write-Host "最新版本: $($rel.tag_name)  资产: $($asset.name)  ($([math]::Round($asset.size/1MB,1)) MB)"
$zip = Join-Path $env:TEMP $asset.name
Write-Host '下载中…'
Invoke-WebRequest -Uri $asset.browser_download_url -OutFile $zip -Headers $hdr -TimeoutSec 3600
$digest = $asset.digest
if ($digest -and $digest.StartsWith('sha256:')) {
  $want = $digest.Substring(7).ToLower()
  $got = (Get-FileHash $zip -Algorithm SHA256).Hash.ToLower()
  if ($got -ne $want) { Remove-Item $zip -Force; throw "校验失败：$got vs $want（已丢弃，安装目录未改动）" }
  Write-Host 'SHA256 校验通过 ✓'
} else { Write-Host '发布未提供 digest，跳过校验（仅此一次）' }
$stage = Join-Path $env:TEMP ('blt-payload-' + [guid]::NewGuid().ToString('N').Substring(0,8))
Write-Host '解压…'
Expand-Archive -Path $zip -DestinationPath $stage -Force
Write-Host '停止正在运行的程序…'
Get-Process -Name BiLi_live_Tool -ErrorAction SilentlyContinue | ForEach-Object { $_.CloseMainWindow() | Out-Null }
Start-Sleep -Seconds 3
Get-Process -Name BiLi_live_Tool -ErrorAction SilentlyContinue | Stop-Process -Force
$bak = Join-Path $dir ('update_backup\' + (Get-Date -Format 'yyyyMMdd-HHmmss'))
Write-Host "备份到 $bak …"
robocopy $dir $bak /E /XF config.json /XD data update_staging update_backup /NFL /NDL /NJH /NJS /R:1 /W:1 | Out-Null
Write-Host '复制新文件（保留 config.json 与 data）…'
robocopy $stage $dir /E /XF config.json /XD data update_staging update_backup /NFL /NDL /NJH /NJS /R:2 /W:1 | Out-Null
if ($LASTEXITCODE -gt 7) { throw "复制失败：robocopy $LASTEXITCODE（备份在 $bak，可手动还原）" }
Remove-Item $zip -Force -ErrorAction SilentlyContinue
Remove-Item $stage -Recurse -Force -ErrorAction SilentlyContinue
Write-Host '启动新版…'
Start-Process (Join-Path $dir 'BiLi_live_Tool.exe') -WorkingDirectory $dir
Write-Host "完成 ✓ 已更新到 $($rel.tag_name)"
