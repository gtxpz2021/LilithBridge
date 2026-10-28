$ErrorActionPreference = 'Stop'
$compiler = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
if (-not (Test-Path -LiteralPath $compiler)) { throw 'Existing Framework compiler required; no SDK initialization or dependency install.' }
$common = @('/nologo','/platform:x64','/optimize+','/debug-','/langversion:5','/utf8output','/reference:System.dll','/reference:System.Core.dll','/reference:System.Drawing.dll','/reference:System.Windows.Forms.dll','/reference:System.Web.Extensions.dll')
$sources = @('Setup.Core.cs','Setup.Form.cs','Installer.Core.cs','Installer.Form.cs','Program.cs') | ForEach-Object { Join-Path $PSScriptRoot ('src\'+$_) }
$setup = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\setup'))
& $compiler @common /target:winexe "/win32manifest:$PSScriptRoot\src\app.manifest" "/out:$setup\LilithBridge-Installer.exe" @sources
if ($LASTEXITCODE -ne 0) { throw 'Installer compilation failed.' }