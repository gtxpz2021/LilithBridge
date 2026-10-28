param(
    [Parameter(Mandatory=$true)][string]$PythonExe,
    [Parameter(Mandatory=$true)][string]$RuntimeRoot,
    [ValidateSet('plan','download','install','verify','remove-venv')][string]$Action='plan',
    [string[]]$WheelCache=@(),
    [ValidateRange(0,2147483647)][int]$MaxDownloadMiB=0,
    [switch]$PreviewCommand
)
$ErrorActionPreference='Stop'
$python=(Resolve-Path -LiteralPath $PythonExe).Path
if([IO.Path]::GetExtension($python) -ne '.exe'){throw 'Choose your own ordinary Windows Python executable.'}
if(-not [IO.Path]::IsPathRooted($RuntimeRoot)){throw 'RuntimeRoot must be an explicit local absolute path.'}
$runtime=[IO.Path]::GetFullPath($RuntimeRoot)
$tool=Join-Path $PSScriptRoot 'optional-recall\runtime_setup.py'
if(-not(Test-Path -LiteralPath $tool -PathType Leaf)){throw 'Optional tool files are incomplete.'}
if($Action -eq 'download' -and $MaxDownloadMiB -le 0){throw 'Download is an explicit network operation; supply the reviewed byte budget.'}
$arguments=@('-I','-X','utf8','-B',$tool,$Action,'--root',$runtime)
foreach($cache in $WheelCache){$arguments+=@('--wheel-cache',[IO.Path]::GetFullPath($cache))}
if($Action -eq 'download'){$arguments+=@('--max-download-mib',[string]$MaxDownloadMiB)}
if($PreviewCommand){[pscustomobject]@{executable=$python;arguments=$arguments;executed=$false};return}
& $python @arguments
if($LASTEXITCODE -ne 0){throw 'Optional tool stopped; inspect its own result before retrying. No game configuration was changed.'}
