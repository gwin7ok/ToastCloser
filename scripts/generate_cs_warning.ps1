<#
Create a temporary console project that triggers a C# compiler warning (Obsolete API usage)
and builds it so you can verify terminal encoding for warning output.
#>
param()
$dir = Join-Path -Path $PSScriptRoot -ChildPath 'tempWarn'
if (Test-Path $dir) { Remove-Item -Recurse -Force $dir }

dotnet new console -o $dir -f net8.0 | Out-Null

$code = @'
using System;

class Program {
    [Obsolete("OldMethod is obsolete")] 
    static void OldMethod() { }

    static void Main() {
        OldMethod();
        Console.WriteLine("Finished");
    }
}
'@

Set-Content -Path (Join-Path $dir 'Program.cs') -Value $code -Force

Write-Host "Building temporary project at: $dir"
Push-Location $dir
try {
    dotnet build
} finally {
    Pop-Location
}

Write-Host 'Build finished. Remove the temp project if desired: Remove-Item -Recurse -Force $dir' 
