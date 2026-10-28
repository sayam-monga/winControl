param(
    [switch] $Install,
    [ValidateSet('x64', 'arm64')]
    [string] $Architecture = 'x64'
)

$ErrorActionPreference = 'Stop'
$projectPath = Join-Path $PSScriptRoot 'FreeMyTVRemote.csproj'
$manifestPath = Join-Path $PSScriptRoot 'Package.appxmanifest'
$assetsPath = Join-Path $PSScriptRoot 'Assets'
$outputDirectory = Join-Path $PSScriptRoot 'artifacts'
$packagePath = Join-Path $outputDirectory "FreeMyTVRemote-$Architecture.msix"
$certificatePath = Join-Path $outputDirectory 'FreeMyTVRemote.cer'
$publisher = 'CN=FreeMyTVRemote'
$packageRoot = Join-Path ([System.IO.Path]::GetTempPath()) "FreeMyTVRemote-$([guid]::NewGuid())"
$publishDirectory = Join-Path $packageRoot 'publish'
$stagingDirectory = Join-Path $packageRoot 'stage'
$runtimeIdentifier = "win-$Architecture"
$platformTarget = if ($Architecture -eq 'arm64') { 'ARM64' } else { 'x64' }
$manifestArchitecture = if ($Architecture -eq 'arm64') { 'arm64' } else { 'x64' }

if (-not (Get-Command dotnet -ErrorAction SilentlyContinue)) {
    throw 'Install the .NET 8 SDK first, then run this script again.'
}
if (-not (Test-Path (Join-Path $assetsPath 'PackageLogo.png')) -or
    -not (Test-Path (Join-Path $assetsPath 'Square150x150Logo.png')) -or
    -not (Test-Path (Join-Path $assetsPath 'Square44x44Logo.png'))) {
    throw 'One or more correctly sized Windows package icons are missing from Assets.'
}

$windowsKitBin = Join-Path ${env:ProgramFiles(x86)} 'Windows Kits\10\bin'
$makeAppx = Get-ChildItem -Path $windowsKitBin -Filter MakeAppx.exe -Recurse -ErrorAction SilentlyContinue |
    Where-Object { $_.DirectoryName -like '*\x64' } |
    Sort-Object { [version](Split-Path (Split-Path $_.DirectoryName -Parent) -Leaf) } -Descending |
    Select-Object -First 1
$signTool = Get-ChildItem -Path $windowsKitBin -Filter SignTool.exe -Recurse -ErrorAction SilentlyContinue |
    Where-Object { $_.DirectoryName -like '*\x64' } |
    Sort-Object { [version](Split-Path (Split-Path $_.DirectoryName -Parent) -Leaf) } -Descending |
    Select-Object -First 1
if (-not $makeAppx -or -not $signTool) {
    throw 'Install the Windows 10/11 SDK so MakeAppx.exe and SignTool.exe are available.'
}

try {
    New-Item -ItemType Directory -Path $publishDirectory -Force | Out-Null
    New-Item -ItemType Directory -Path $stagingDirectory -Force | Out-Null
    # Some Windows images have a NuGet config with no enabled sources; provide nuget.org for this build without editing user config.
    & dotnet publish $projectPath -c Release -r $runtimeIdentifier --self-contained true "-p:PlatformTarget=$platformTarget" -p:RestoreSources=https://api.nuget.org/v3/index.json -o $publishDirectory
    if ($LASTEXITCODE -ne 0) { throw 'The .NET publish step failed.' }

    Copy-Item -Path (Join-Path $publishDirectory '*') -Destination $stagingDirectory -Recurse -Force
    [xml] $packageManifest = Get-Content -LiteralPath $manifestPath -Raw
    $identityNode = $packageManifest.SelectSingleNode("/*[local-name()='Package']/*[local-name()='Identity']")
    $identityNode.SetAttribute('ProcessorArchitecture', $manifestArchitecture)
    $packageManifest.Save((Join-Path $stagingDirectory 'AppxManifest.xml'))
    New-Item -ItemType Directory -Path (Join-Path $stagingDirectory 'Assets') -Force | Out-Null
    Copy-Item -Path (Join-Path $assetsPath '*.png') -Destination (Join-Path $stagingDirectory 'Assets')
    New-Item -ItemType Directory -Path $outputDirectory -Force | Out-Null
    & $makeAppx.FullName pack /d $stagingDirectory /p $packagePath /o
    if ($LASTEXITCODE -ne 0) { throw 'The MSIX packaging step failed.' }

    $certificate = Get-ChildItem Cert:\CurrentUser\My |
        Where-Object { $_.Subject -eq $publisher -and $_.HasPrivateKey -and $_.NotAfter -gt (Get-Date) } |
        Sort-Object NotAfter -Descending |
        Select-Object -First 1
    if (-not $certificate) {
        # A local development signing certificate lets Windows verify this package after the user trusts its public half.
        # A two-year local certificate avoids frequent development reinstall friction while remaining clearly non-production.
        $certificate = New-SelfSignedCertificate -Type CodeSigningCert -Subject $publisher `
            -CertStoreLocation Cert:\CurrentUser\My -NotAfter (Get-Date).AddYears(2)
    }
    Export-Certificate -Cert $certificate -FilePath $certificatePath -Force | Out-Null
    & $signTool.FullName sign /fd SHA256 /sha1 $certificate.Thumbprint $packagePath
    if ($LASTEXITCODE -ne 0) { throw 'The MSIX signing step failed.' }

    Write-Host "Package created: $packagePath"
    Write-Host "Development certificate: $certificatePath"
    if ($Install) {
        Import-Certificate -FilePath $certificatePath -CertStoreLocation Cert:\CurrentUser\TrustedPeople | Out-Null
        Add-AppxPackage -Path $packagePath
        Write-Host 'Free My TV Remote is installed for this Windows user.'
    }
}
finally {
    if (Test-Path $packageRoot) {
        try {
            Remove-Item -LiteralPath $packageRoot -Recurse -Force
        }
        catch [System.IO.IOException] {
            # Best-effort temp cleanup must not hide an earlier publish or packaging error.
        }
        catch [System.UnauthorizedAccessException] {
            # Best-effort temp cleanup must not hide an earlier publish or packaging error.
        }
    }
}
