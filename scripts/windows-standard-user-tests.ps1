<#
.SYNOPSIS
Runs the Windows sandbox tests once as a standard user: not an administrator, at medium integrity, as `sof` runs for most
people. CI runs as an elevated administrator otherwise. This is the recipe the sandbox spike (S00a) used.

.DESCRIPTION
Creates a local standard user with a random password, copies the built sandbox tests to a folder only that user and the
administrators can use, runs them there as that user, prints their output, and deletes the user and the folder again, also
when the tests fail. Exits with 1 when a test fails.

Run it on a Windows runner after the Release build, from the repository's root:

    pwsh scripts/windows-standard-user-tests.ps1

As a CI step of the Windows job, after "Test":

    - name: Windows sandbox tests as a standard user
      if: runner.os == 'Windows'
      shell: pwsh
      run: ./scripts/windows-standard-user-tests.ps1
#>
param(
    [string]$Configuration = "Release"
)

$ErrorActionPreference = "Stop"
$built = Join-Path $PSScriptRoot "..\tests\Sleepyshark.Officina.Sandbox.Tests\bin\$Configuration\net10.0"
if (-not (Test-Path (Join-Path $built "Sleepyshark.Officina.Sandbox.Tests.dll"))) {
    throw "Build the solution in $Configuration first: $built has no sandbox tests."
}

$user = "sofstandard"
# net user prompts when a password is longer than 14 characters.
$password = "Sf!" + [guid]::NewGuid().ToString("N").Substring(0, 10)
$root = "C:\sof-standard-user"
net user $user $password /add | Out-Null
try {
    New-Item -ItemType Directory -Force $root | Out-Null
    Copy-Item -Recurse -Force $built "$root\tests"
    icacls $root /grant "${user}:(OI)(CI)F" /T | Out-Null
    $log = "$root\tests.log"

    # The test project is an executable (xunit v3), so it runs with no restore and no access to the administrator's packages.
    $dotnet = (Get-Command dotnet).Source
    $command = "whoami /groups > `"$log`" 2>&1 & `"$dotnet`" `"$root\tests\Sleepyshark.Officina.Sandbox.Tests.dll`" " +
        "-class Sleepyshark.Officina.Sandbox.Tests.WindowsSandboxTests >> `"$log`" 2>&1 && (echo EXIT 0>> `"$log`") || (echo EXIT 1>> `"$log`")"
    $credential = New-Object System.Management.Automation.PSCredential($user, (ConvertTo-SecureString $password -AsPlainText -Force))
    Start-Process -FilePath cmd.exe -ArgumentList "/c", $command -Credential $credential -Wait -WorkingDirectory $root -LoadUserProfile

    Get-Content $log
    if (-not (Select-String -Path $log -Pattern "^EXIT 0\s*$" -Quiet)) {
        Write-Error "The Windows sandbox tests failed as a standard user."
        exit 1
    }
}
finally {
    net user $user /delete | Out-Null
    Remove-Item -Recurse -Force $root -ErrorAction SilentlyContinue
    # The user's profile folder stays until the runner is discarded; it holds nothing but what the tests left in it.
}
