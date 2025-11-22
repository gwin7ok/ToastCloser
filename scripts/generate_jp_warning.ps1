<#
Generate a temporary .NET console project that emits a compiler warning in Japanese.
This uses a `#warning` directive with a Japanese message so the compiler prints the
exact message (useful to verify terminal encoding). The source file is written as UTF-8.

Usage: run from workspace root:
  & .\scripts\generate_jp_warning.ps1

The script creates `scripts\tempJpWarn`, builds it, and leaves the folder for inspection.
#>
param()

$dir = Join-Path -Path $PSScriptRoot -ChildPath 'tempJpWarn'
if (Test-Path $dir) { Remove-Item -Recurse -Force $dir }

Write-Host "Creating temporary project at: $dir"
dotnet new console -o $dir -f net8.0 | Out-Null

$code = @'
#warning "日本語の警告: これは文字化けチェック用のメッセージです"
using System;

class Program {
    static void Main() {
        Console.WriteLine("ビルド完了");
    }
}
'@

Set-Content -Path (Join-Path $dir 'Program.cs') -Value $code -Encoding UTF8

Push-Location $dir
try {
    Write-Host "Running: dotnet build (this should emit a Japanese warning)"
    dotnet build
} finally {
    Pop-Location
}

Write-Host "Done. Project left at: $dir"
Write-Host "If you want to remove it: Remove-Item -Recurse -Force $dir"

