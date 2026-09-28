$ErrorActionPreference = 'Stop'
Set-Location $PSScriptRoot
if (-not (Get-Command dotnet -ErrorAction SilentlyContinue)) {
    throw 'Install the .NET 10 SDK first (or open WorkTimeTaskbar.csproj in Visual Studio).'
}
dotnet publish .\WorkTimeTaskbar.csproj -c Release -r win-x64 --self-contained true `
    -p:PublishSingleFile=true -p:EnableCompressionInSingleFile=true
if ($LASTEXITCODE -ne 0) { throw "Publish failed with exit code $LASTEXITCODE" }
Write-Host "Ready: $PSScriptRoot\bin\Release\net10.0-windows\win-x64\publish\WorkTimeTaskbar.exe"
