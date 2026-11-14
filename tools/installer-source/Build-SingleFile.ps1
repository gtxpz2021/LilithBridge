param([Parameter(Mandatory=$true)][string]$Payload,[Parameter(Mandatory=$true)][string]$Destination,[string]$ReleaseProfile=(Join-Path $PSScriptRoot 'release-profile.json'))
$ErrorActionPreference='Stop'
& python (Join-Path $PSScriptRoot 'prepare_release.py') --profile $ReleaseProfile
if($LASTEXITCODE -ne 0){throw 'Release input identity preparation failed; no installer was run.'}
$payloadPath=[IO.Path]::GetFullPath($Payload)
$destinationPath=[IO.Path]::GetFullPath($Destination)
$digest=(Get-FileHash -LiteralPath $payloadPath -Algorithm SHA256).Hash.ToLowerInvariant()
$length=(Get-Item -LiteralPath $payloadPath).Length
$identity=Join-Path ([IO.Path]::GetDirectoryName($destinationPath)) 'PayloadIdentity.g.cs'
$code='namespace LilithSetupLauncher { internal static class PayloadInfo { internal const string Hash="'+$digest+'"; internal const long Bytes='+$length+'L; } }'
[IO.File]::WriteAllText($identity,$code,[Text.UTF8Encoding]::new($false))
$compiler=Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
& $compiler /nologo /codepage:65001 /langversion:5 /target:winexe /platform:x64 /optimize+ /debug- /reference:System.dll /reference:System.Core.dll /reference:System.Drawing.dll /reference:System.Windows.Forms.dll /reference:System.IO.Compression.dll /reference:System.IO.Compression.FileSystem.dll "/win32manifest:$PSScriptRoot\src\app.manifest" "/resource:$payloadPath,LilithBridge.SetupPayload.zip" "/out:$destinationPath" "$PSScriptRoot\launcher\Bootstrap.cs" $identity
if($LASTEXITCODE -ne 0){throw 'Single-file launcher compilation failed. No installer or test was run.'}
& python (Join-Path $PSScriptRoot 'verify_embedded_payload.py') --exe $destinationPath --payload $payloadPath --receipt (Join-Path ([IO.Path]::GetDirectoryName($destinationPath)) 'launcher-payload-verification.json')
if($LASTEXITCODE -ne 0){throw 'Embedded payload identity differs from the compiled launcher. Candidate was not run; do not distribute it.'}
